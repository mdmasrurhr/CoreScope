using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CoreScope.Core.Control;
using CoreScope.Core.Hardware;

namespace CoreScope.Core;

/// <summary>
/// Headless end-to-end check (<c>CoreScope.exe --selftest</c>): exercises every collector, the sensor engine,
/// insights, the upgrade advisor and all Control Center *reads* (it never changes a setting), then writes
/// selftest.json next to the executable. Exit code = number of failed checks. Used for unattended testing
/// and for diagnosing a user's machine.
/// </summary>
public static class SelfTest
{
    public sealed record Check(string Name, bool Ok, long Ms, string Detail);

    public static async Task<int> RunAsync(IRadioService? radios)
    {
        var checks = new List<Check>();
        var facts = new Dictionary<string, object?>();

        async Task<T?> Run<T>(string name, Func<T> work, Func<T, string>? describe = null)
        {
            var w = Stopwatch.StartNew();
            try
            {
                var result = await Task.Run(work);
                checks.Add(new Check(name, true, w.ElapsedMilliseconds, describe?.Invoke(result) ?? ""));
                return result;
            }
            catch (Exception ex)
            {
                checks.Add(new Check(name, false, w.ElapsedMilliseconds, $"{ex.GetType().Name}: {ex.Message}"));
                Log.Error($"Self-test {name}", ex);
                return default;
            }
        }

        var spec = await Run("Spec collection", SpecCollector.Collect,
            s => $"cpu={s.Cpu.Name}; ram={Format.Bytes(s.TotalRamBytes)}; gpus={s.Gpus.Count}; disks={s.Disks.Count}; devices={s.Devices.Sum(c => c.Devices.Count)}");

        if (spec is not null)
        {
            facts["cpu"] = spec.Cpu.Name;
            facts["codename"] = spec.Cpu.Codename;
            facts["cores"] = $"{spec.Cpu.Cores}C/{spec.Cpu.Threads}T (P{spec.Cpu.PerformanceCores}/E{spec.Cpu.EfficiencyCores})";
            facts["memory"] = $"{Format.Bytes(spec.TotalRamBytes)} {spec.MemoryTypeSummary}";
            facts["gpus"] = spec.Gpus.Select(g => $"{g.Name} [{(g.IsIntegrated ? "iGPU" : "dGPU")}] {Format.Bytes(g.VramBytes)}").ToList();
            facts["disks"] = spec.Disks.Select(d => $"{d.Name} {d.BusType} {d.MediaType} {Format.Bytes(d.SizeBytes)} link={d.Link?.Current}").ToList();
            facts["board"] = $"{spec.Board.SystemVendor} | {spec.Board.SystemModel} | {spec.Board.SystemVersion}";
            facts["admin"] = spec.Os.IsAdmin;
            facts["sections"] = spec.CpuSections.Count + spec.MemorySections.Count + spec.GpuSections.Count + spec.StorageSections.Count + spec.BoardSections.Count + spec.SystemSections.Count;
            facts["upgradeItems"] = spec.Upgrade?.Items.Select(i => $"{i.Component}: {i.StatusText}").ToList();
            Expect(checks, "CPU identified", spec.Cpu.Name.Length > 0, spec.Cpu.Name);
            Expect(checks, "Memory found", spec.TotalRamBytes > 0, Format.Bytes(spec.TotalRamBytes));
            Expect(checks, "Every spec page has content", spec.CpuSections.Count > 0 && spec.MemorySections.Count > 0 && spec.BoardSections.Count > 0 && spec.SystemSections.Count > 0, "");
            Expect(checks, "Upgrade guide built", spec.Upgrade is { Items.Count: > 0 }, $"{spec.Upgrade?.Items.Count} items");
            Expect(checks, "Report export", Report.ToHtml(spec, Array.Empty<SensorReading>(), new List<Insight>()).Length > 1000, "");
        }

        // Other-PC profiles: run insights/upgrade/report on synthetic machines this PC isn't
        // (a desktop with an NVIDIA card and no battery, and a machine where every collector failed).
        // Does each temperature source react to load like a real die sensor (fast, large rise)?
        await Run("Temperature sources under load", () =>
        {
            var samples = new Dictionary<string, (double Before, double Peak)>();
            using var hub = new SensorHub { IntervalMs = 500 };
            List<SensorReading> latest = new();
            hub.Updated += r => { lock (samples) latest = r.ToList(); };
            hub.Start();
            Thread.Sleep(4000);
            List<SensorReading> Temps() { lock (samples) return latest.Where(r => r.Type == "Temperature" && r.Value is > 0).ToList(); }
            foreach (var t in Temps()) samples[$"{t.GroupName}/{t.Name}"] = (t.Value!.Value, t.Value!.Value);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var load = Task.Run(() => Benchmark.CpuMulti(TimeSpan.FromSeconds(10)));
            while (!load.IsCompleted)
            {
                Thread.Sleep(500);
                foreach (var t in Temps())
                {
                    var key = $"{t.GroupName}/{t.Name}";
                    var (before, peak) = samples.TryGetValue(key, out var v) ? v : (t.Value!.Value, t.Value!.Value);
                    samples[key] = (before, Math.Max(peak, t.Value!.Value));
                }
            }
            return string.Join("; ", samples.Select(kv => $"{kv.Key}: {kv.Value.Before:0.0} → {kv.Value.Peak:0.0} °C"));
        }, r => r);

        await Run("Network checks", () =>
        {
            var n = Network.NetworkDiagnostics.Collect();
            var findings = Network.NetworkDiagnostics.Analyze(n);
            return $"{n.Primary?.Kind} {n.Primary?.Description}; wifi={n.Wifi?.Band} {n.Wifi?.SignalPercent}% link={n.Wifi?.ReceiveMbps}; rsc={n.Tcp.RscEnabled}; autotuning={n.Tcp.AutoTuning}; vpn={n.ActiveVpns.Count}; findings={string.Join(" | ", findings.Select(f => f.Title))}";
        }, r => r);
        await Run("Synthetic: desktop + NVIDIA, no battery", () => SyntheticDesktop(), r => r);
        await Run("Synthetic: nothing collected", () => SyntheticEmpty(), r => r);

        // Sensor engine: five polls on its own thread.
        var readings = new List<SensorReading>();
        await Run("Sensor engine (5 polls)", () =>
        {
            using var hub = new SensorHub { IntervalMs = 500 };
            using var done = new CountdownEvent(5);
            hub.Updated += r =>
            {
                lock (readings) { readings.Clear(); readings.AddRange(r); }
                if (!done.IsSet) done.Signal();
            };
            hub.Start();
            return done.Wait(TimeSpan.FromSeconds(30));
        }, ok => $"completed={ok}; sensors={readings.Count}");
        facts["sensorCount"] = readings.Count;
        facts["cpuLoad"] = SensorPick.CpuLoad(readings);
        facts["cpuTemp"] = SensorPick.CpuTemperature(readings);
        facts["cpuClock"] = SensorPick.CpuClock(readings);
        facts["gpuLoad"] = SensorPick.GpuLoad(readings);
        lock (readings)
        {
            var cpuTempSensor = readings.FirstOrDefault(r => r.Kind == "Cpu" && r.Type == "Temperature");
            facts["cpuTempSource"] = cpuTempSensor?.Name;
            facts["thermalZones"] = readings.Where(r => r.Kind == "ThermalZone").Select(r => $"{r.Name}={r.Value:0.0}").ToList();
            facts["powerRails"] = readings.Where(r => r.Kind == "PowerRail").Select(r => $"{r.Name}={r.Value:0.00}W").ToList();
            facts["cpuPower"] = SensorPick.CpuPower(readings);
            facts["gpuSensors"] = readings.Where(r => r.Kind.StartsWith("Gpu", StringComparison.Ordinal)).Select(r => $"{r.GroupName}/{r.Name} {r.Type}={r.Value:0.#}").Take(40).ToList();
            Expect(checks, "Driver-free CPU load and clock", SensorPick.CpuLoad(readings) is not null && SensorPick.CpuClock(readings) is > 100,
                $"load={SensorPick.CpuLoad(readings):0.#}% clock={SensorPick.CpuClock(readings):0} MHz");
            Expect(checks, "Driver-free CPU temperature", cpuTempSensor is not null, cpuTempSensor is null ? "none" : $"{cpuTempSensor.Name} = {cpuTempSensor.Value:0.0} °C");
        }

        if (spec is not null)
        {
            await Run("Insights", () =>
            {
                var engine = new InsightEngine();
                engine.Observe(readings);
                return engine.Evaluate(spec);
            }, list => string.Join(" | ", list.Select(i => $"{i.Severity}:{i.Title}")));
        }

        // The wider checkup: security, updates, recovery, maintenance, startup, display. Reads only.
        HealthSnapshot? health = null;
        await Run("Health checkup", () => health = HealthCollector.Collect(), h =>
            $"antivirus={h.AntivirusName ?? "?"}/{h.AntivirusActive}; firewall={h.FirewallPublic}/{h.FirewallPrivate}/{h.FirewallDomain}; updateAge={h.DaysSinceUpdate}d; " +
            $"restart pending={h.PendingReboot}; restore={h.SystemRestoreOn}; clock={h.ClockServiceRunning}; temp={h.TempBytes / (1 << 20)} MB; bin={h.RecycleBinBytes / (1 << 20)} MB; startup={h.StartupApps.Count}; refresh={h.RefreshHz}/{h.MaxRefreshHz} Hz");
        if (spec is not null && health is not null)
        {
            await Run("Insights with health", () =>
            {
                var engine = new InsightEngine();
                engine.Observe(readings);
                var all = engine.Evaluate(spec, health);
                var stuck = all.Where(i => i.Severity <= Severity.Warning && !i.HasFixes).Select(i => i.Title).ToList();
                if (stuck.Count > 0) throw new InvalidOperationException("Serious findings without a fix button: " + string.Join("; ", stuck));
                return all;
            }, list => $"{list.Count} findings ({list.Count(i => i.Severity == Severity.Good)} passed, {list.Count(i => i.HasFixes)} with fix buttons)");
        }

        // Control Center reads only — nothing is changed.
        await Run("Displays", DisplayControl.Displays, d => string.Join("; ", d.Select(x => $"{x.Label} {x.Current} ({x.Modes.Count} modes)")));
        await Run("Brightness", () => DisplayControl.Brightness(), b => b?.ToString() ?? "n/a (external monitor / desktop)");
        await Run("Power plans", PowerControl.Plans, p => string.Join(", ", p.Select(x => x.Name)));
        await Run("Power mode", () => PowerControl.PowerMode(), m => m?.ToString() ?? "n/a");
        await Run("CPU boost", () => PowerControl.BoostMode(true), b => b?.ToString() ?? "n/a");
        await Run("Timeouts", () => PowerControl.GetTimeout(PowerControl.Timeout.ScreenOff, true), t => $"screen-off AC={t}s");
        await Run("Audio", () => AudioControl.Volume(), v => v is null ? "no playback device" : $"{v}%");
        await Run("Battery conservation", () => BatteryControl.ConservationEnabled(), c => c is null ? "not supported" : $"on={c}");
        await Run("Startup apps", StartupApps.List, a => $"{a.Count} apps, {a.Count(x => x.Enabled)} enabled");
        if (radios is not null)
        {
            var w = Stopwatch.StartNew();
            try
            {
                var r = await radios.GetAsync();
                checks.Add(new Check("Radios", true, w.ElapsedMilliseconds, string.Join(", ", r.Select(x => $"{x.Kind}={(x.IsOn ? "on" : "off")}"))));
            }
            catch (Exception ex) { checks.Add(new Check("Radios", false, w.ElapsedMilliseconds, ex.Message)); }
        }
        await Run("Benchmark (quick CPU)", () => Benchmark.CpuSingle(TimeSpan.FromMilliseconds(500)), s => $"{s:0} pts");

        int failed = checks.Count(c => !c.Ok);
        var report = new
        {
            version = typeof(SelfTest).Assembly.GetName().Version?.ToString(),
            when = DateTime.Now,
            os = Environment.OSVersion.ToString(),
            failed,
            checks,
            facts,
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(AppPaths.Writable("selftest.json"), json);
        Log.Info($"Self-test finished: {checks.Count - failed}/{checks.Count} passed");
        return failed;
    }

    private static SmBios EmptySmBios() =>
        SmBios.Parse(new byte[] { 0, 3, 6, 0, 6, 0, 0, 0, 127, 4, 0, 0, 0, 0 }) ?? throw new InvalidOperationException("SMBIOS parse");

    private static SensorReading R(string g, string kind, string name, string type, double v) => new($"/{g}/{name}/{type}", g, g, kind, name, type, v, v, v);

    private static string SyntheticDesktop()
    {
        var spec = new SystemSpec { SmBios = EmptySmBios(), MemorySlots = 4, TotalRamBytes = 32UL << 30 };
        spec.Cpu = new CpuSpec { Name = "Intel Core i7-13700K", Vendor = "GenuineIntel", Family = 6, Model = 0xB7, Cores = 16, Threads = 24, PerformanceCores = 8, EfficiencyCores = 8, Socket = "LGA1700" };
        spec.Board = new BoardSpec { SystemVendor = "To Be Filled By O.E.M.", BoardVendor = "ASUSTeK COMPUTER INC.", BoardModel = "PRIME Z790-P", FirmwareType = "UEFI", SecureBoot = true };
        for (int i = 0; i < 2; i++)
            spec.Modules.Add(new MemoryModule { Slot = $"DIMM_A{i + 1}", CapacityBytes = 16UL << 30, RatedMts = 4800, ConfiguredMts = 4800, PartNumberMts = 6000, TypeName = "DDR5", FormFactor = "DIMM", PartNumber = "F5-6000J3038F16G" });
        spec.Gpus.Add(new GpuSpec { Name = "NVIDIA GeForce RTX 4070", Vendor = "NVIDIA", VramBytes = 12UL << 30, DriverDate = DateTime.Now.AddMonths(-1) });
        spec.Disks.Add(new DiskSpec { Name = "Samsung SSD 990 PRO 2TB", MediaType = "SSD", BusType = "NVMe", SizeBytes = 2_000_000_000_000, IsBoot = true });
        spec.Disks.Add(new DiskSpec { Name = "ST2000DM008", MediaType = "HDD", BusType = "SATA", SizeBytes = 2_000_000_000_000 });
        spec.Volumes.Add(new VolumeSpec { Name = "C:", TotalBytes = 2_000_000_000_000, FreeBytes = 50_000_000_000 });
        spec.Os = new OsSpec { Name = "Microsoft Windows 10 Pro", Build = "19045" };
        var s = new List<SensorReading>
        {
            R("i7", "Cpu", "CPU Total", "Load", 35), R("i7", "Cpu", "CPU Package", "Temperature", 62), R("i7", "Cpu", "P-Core #1", "Clock", 5300),
            R("UHD", "GpuIntel", "D3D 3D", "Load", 3), R("RTX", "GpuNvidia", "D3D 3D", "Load", 96), R("RTX", "GpuNvidia", "GPU Core", "Temperature", 71),
            R("SIO", "SuperIO", "Fan #1", "Fan", 900),
        };
        var engine = new InsightEngine();
        for (int i = 0; i < 20; i++) engine.Observe(s);
        var insights = engine.Evaluate(spec);
        var upgrade = UpgradeAdvisor.Build(spec);
        if (upgrade.IsLaptop) throw new InvalidOperationException("desktop classified as laptop");
        if (insights.Any(i => i.Category.Contains("Battery", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("battery insight without a battery");
        if (SensorPick.GpuLoad(s) != 96 || SensorPick.GpuTemperature(s) != 71) throw new InvalidOperationException("dGPU not preferred");
        var html = Report.ToHtml(spec, s, insights);
        return $"{insights.Count} insights; upgrade: {string.Join(", ", upgrade.Items.Select(i => $"{i.Component}={i.StatusText}"))}; report {html.Length / 1024} KB";
    }

    private static string SyntheticEmpty()
    {
        var spec = new SystemSpec { SmBios = EmptySmBios() };
        var engine = new InsightEngine();
        engine.Observe(Array.Empty<SensorReading>());
        var insights = engine.Evaluate(spec);
        var upgrade = UpgradeAdvisor.Build(spec);
        Report.ToHtml(spec, Array.Empty<SensorReading>(), insights);
        Report.ToText(spec);
        return $"{insights.Count} insights, {upgrade.Items.Count} upgrade items, no exceptions";
    }

    private static void Expect(List<Check> checks, string name, bool condition, string detail) =>
        checks.Add(new Check(name, condition, 0, detail));
}
