using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using LibreHardwareMonitor.Hardware;

namespace CoreScope.Core.Hardware;

/// <summary>Immutable snapshot of one sensor at one instant.</summary>
public sealed record SensorReading(
    string Id,
    string GroupId,
    string GroupName,
    string Kind,
    string Name,
    string Type,
    double? Value,
    double? Min,
    double? Max);

/// <summary>
/// Owns the LibreHardwareMonitor <see cref="Computer"/> on a dedicated background thread and
/// publishes a full snapshot of every sensor on each tick. LHM objects never leave this thread.
/// </summary>
public sealed class SensorHub : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private Computer? _computer;
    private bool _dumped;

    public SensorHub()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "CoreScope.SensorHub" };
    }

    /// <summary>Raised on the background thread after every poll.</summary>
    public event Action<IReadOnlyList<SensorReading>>? Updated;

    /// <summary>Raised once (on the sensor thread) after SPD has been read — empty list when unavailable.</summary>
    public event Action<IReadOnlyList<SpdModuleTimings>>? SpdRead;

    public int IntervalMs { get; set; } = 1000;

    // ───────── Fan control (desktop Super I/O fans exposed by LibreHardwareMonitor) ─────────

    public enum FanPolicy { Auto, Fixed, Curve }

    /// <summary>Controllable fans found on the last poll (empty on most laptops: their fans belong to the EC).</summary>
    public IReadOnlyList<string> ControllableFans { get; private set; } = Array.Empty<string>();

    private volatile FanPolicy _fanPolicy = FanPolicy.Auto;
    private volatile int _fanPercent = 50;
    private bool _fansTouched;

    /// <summary>
    /// Fan curve on CPU temperature: below 40 °C → 30 %, 60 °C → 45 %, 75 °C → 70 %, 85 °C+ → 100 %.
    /// Deliberately conservative: never quieter than the BIOS would be at high temperatures.
    /// </summary>
    public static double CurvePercent(double cpuTempC)
    {
        (double T, double P)[] points = { (40, 30), (60, 45), (75, 70), (85, 100) };
        if (cpuTempC <= points[0].T) return points[0].P;
        for (int i = 1; i < points.Length; i++)
        {
            if (cpuTempC <= points[i].T)
            {
                var (t0, p0) = points[i - 1];
                var (t1, p1) = points[i];
                return p0 + (p1 - p0) * (cpuTempC - t0) / (t1 - t0);
            }
        }
        return 100;
    }

    public void SetFanPolicy(FanPolicy policy, int percent)
    {
        _fanPercent = Math.Clamp(percent, 20, 100); // never allow a stalled fan
        _fanPolicy = policy;
        Log.Info($"Fans: policy {policy} {_fanPercent}%");
    }

    /// <summary>Runs on the sensor thread after each poll.</summary>
    private void ApplyFans(IReadOnlyList<SensorReading> readings)
    {
        if (_computer is null) return;
        var controls = new List<IControl>();
        void Find(IHardware h)
        {
            foreach (var sensor in h.Sensors)
                if (sensor.SensorType == SensorType.Control && sensor.Control is { } c) controls.Add(c);
            foreach (var sub in h.SubHardware) Find(sub);
        }
        foreach (var h in _computer.Hardware) Find(h);
        ControllableFans = controls.Select(c => c.Sensor.Name).ToList();
        if (controls.Count == 0) return;

        var policy = _fanPolicy;
        if (policy == FanPolicy.Auto)
        {
            if (!_fansTouched) return;
            foreach (var c in controls) c.SetDefault();
            _fansTouched = false;
            return;
        }

        double target = policy == FanPolicy.Fixed
            ? _fanPercent
            : SensorPick.CpuTemperature(readings) is { } t ? CurvePercent(t) : 100; // no temperature → full speed, never guess low
        foreach (var c in controls)
        {
            var value = (float)Math.Clamp(target, Math.Max(c.MinSoftwareValue, 20), c.MaxSoftwareValue);
            c.SetSoftware(value);
        }
        _fansTouched = true;
    }

    /// <summary>
    /// Last-resort reset from any thread (process exit / unhandled crash). Super I/O chips keep the last
    /// software speed after the process dies, so this must run even when the sensor thread can't.
    /// </summary>
    public void EmergencyRestoreFans()
    {
        if (!_fansTouched) return;
        _fanPolicy = FanPolicy.Auto;
        RestoreFans();
    }

    private void RestoreFans()
    {
        if (!_fansTouched || _computer is null) return;
        void Reset(IHardware h)
        {
            foreach (var sensor in h.Sensors) sensor.Control?.SetDefault();
            foreach (var sub in h.SubHardware) Reset(sub);
        }
        try
        {
            foreach (var h in _computer.Hardware) Reset(h);
            _fansTouched = false;
            Log.Info("Fans: restored to BIOS control");
        }
        catch (Exception ex) { Log.Error("Restoring fans", ex); }
    }
    public string? LastError { get; private set; }

    public void Start() => _thread.Start();

    /// <summary>Only one CoreScope copy (installed, portable or test build) drives the sensor hardware at a time.</summary>
    private const string EngineMutexName = @"Local\CoreScope.SensorEngine";

    /// <summary>Slow-changing hardware is polled less often: SMART every 15 s, battery every 5 s.</summary>
    private static TimeSpan PollEvery(HardwareType type) => type switch
    {
        HardwareType.Storage => TimeSpan.FromSeconds(15),
        HardwareType.Battery => TimeSpan.FromSeconds(5),
        _ => TimeSpan.Zero,
    };

    /// <summary>
    /// After the charger is plugged in or pulled (or the PC resumes), graphics and storage drivers switch power
    /// states for a few seconds. Querying them mid-transition is a known trigger for driver hangs on some laptops,
    /// so all hardware polling pauses for this long.
    /// </summary>
    private static readonly TimeSpan PowerChangeQuietTime = TimeSpan.FromSeconds(20);

    public bool IsPausedForPowerChange { get; private set; }

    private WinSensors? _win;

    private static readonly string CpuName = ReadCpuName();
    private static bool CpuIsAmd => CpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) || CpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase);

    private static string ReadCpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return ""; }
    }

    private void Loop()
    {
        using var engineMutex = new Mutex(false, EngineMutexName);
        bool owned = false;
        try { owned = engineMutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        while (!owned && !_stop.IsSet)
        {
            LastError = "Another CoreScope window is already reading the sensors. Close it to see live readings here.";
            Updated?.Invoke(Array.Empty<SensorReading>());
            _stop.Wait(5000);
            try { owned = engineMutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }
        }
        if (_stop.IsSet) { if (owned) engineMutex.ReleaseMutex(); return; }
        LastError = null;
        try
        {
            RunEngine();
        }
        finally
        {
            try { engineMutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
    }

    private void RunEngine()
    {
        try
        {
            // Driver-free by design: CPU, motherboard (Super I/O) and memory (SMBus) sensors would need a kernel
            // driver, so those come from Windows' own counters instead (WinSensors). GPU (vendor driver APIs),
            // storage (Windows SMART/NVMe IOCTLs), battery, network and USB controllers need no extra driver.
            _computer = new Computer
            {
                IsCpuEnabled = false,
                IsGpuEnabled = true,
                IsMemoryEnabled = false,
                IsMotherboardEnabled = false,
                IsControllerEnabled = true,
                IsStorageEnabled = true,
                IsBatteryEnabled = true,
                IsNetworkEnabled = true,
            };
            _computer.Open();
            Log.Info($"LibreHardwareMonitor opened (no kernel driver): {string.Join(", ", _computer.Hardware.Select(h => $"{h.HardwareType}:{h.Name}"))}");
            _win = new WinSensors(CpuName);

            // Memory timings would need SMBus access through a driver: report "none read" so pages show the fallback.
            try { SpdRead?.Invoke(new List<SpdModuleTimings>()); }
            catch (Exception ex) { Log.Error("SPD subscriber", ex); }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error("Opening LibreHardwareMonitor", ex);
            Updated?.Invoke(Array.Empty<SensorReading>());
            return;
        }

        var pollWatch = new Stopwatch();
        var perHardware = new Dictionary<string, long>();
        int polls = 0;
        long pollTotalMs = 0;
        var self = Process.GetCurrentProcess();
        var selfCpuStart = self.TotalProcessorTime;
        var wallStart = DateTime.UtcNow;

        var lastPoll = new Dictionary<IHardware, DateTime>();
        var lastPower = Native.GetPowerStatus()?.PluggedIn;
        var quietUntil = DateTime.MinValue;
        var lastLoop = DateTime.UtcNow;
        List<SensorReading>? lastReadings = null;

        while (!_stop.IsSet)
        {
            // Charger plugged/unplugged, or a long gap (sleep/resume): let the drivers settle before touching them.
            var power = Native.GetPowerStatus()?.PluggedIn;
            var now = DateTime.UtcNow;
            if (power != lastPower || now - lastLoop > TimeSpan.FromSeconds(30))
            {
                quietUntil = now + PowerChangeQuietTime;
                Log.Info(power != lastPower ? $"Power source changed (plugged in: {power}); pausing hardware polling {PowerChangeQuietTime.TotalSeconds:0} s" : "Resumed after a pause; letting drivers settle");
                lastPower = power;
            }
            lastLoop = now;
            IsPausedForPowerChange = now < quietUntil;
            if (IsPausedForPowerChange)
            {
                if (lastReadings is not null) { try { Updated?.Invoke(lastReadings); } catch (Exception ex) { Log.Error("Sensor subscriber", ex); } }
                _stop.Wait(1000);
                continue;
            }

            var readings = new List<SensorReading>(256);
            pollWatch.Restart();
            try
            {
                foreach (var hardware in _computer.Hardware)
                {
                    long before = pollWatch.ElapsedMilliseconds;
                    var every = PollEvery(hardware.HardwareType);
                    bool due = every == TimeSpan.Zero || !lastPoll.TryGetValue(hardware, out var last) || now - last >= every;
                    if (due) lastPoll[hardware] = now;
                    Collect(hardware, readings, update: due);
                    var key = hardware.HardwareType.ToString();
                    perHardware[key] = perHardware.GetValueOrDefault(key) + pollWatch.ElapsedMilliseconds - before;
                }
                long winBefore = pollWatch.ElapsedMilliseconds;
                if (_win is not null) readings.AddRange(_win.Read());
                CpuTemperatureSource.Augment(readings, CpuIsAmd, cpuName: Format.CleanName(CpuName));
                perHardware["Windows"] = perHardware.GetValueOrDefault("Windows") + pollWatch.ElapsedMilliseconds - winBefore;
                LastError = null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Error("Polling sensors", ex);
            }

            try { ApplyFans(readings); }
            catch (Exception ex) { Log.Error("Fan control", ex); _fanPolicy = FanPolicy.Auto; }

            if (!_dumped && readings.Count > 0)
            {
                _dumped = true;
                DumpSensors(readings);
            }

            lastReadings = readings;
            try { Updated?.Invoke(readings); }
            catch (Exception ex) { Log.Error("Sensor subscriber", ex); }

            // Performance self-check once a minute: how much this app costs, and which hardware is slow to poll.
            pollTotalMs += pollWatch.ElapsedMilliseconds;
            if (++polls % 60 == 0)
            {
                self.Refresh();
                var cpu = (self.TotalProcessorTime - selfCpuStart).TotalMilliseconds / (DateTime.UtcNow - wallStart).TotalMilliseconds / Environment.ProcessorCount * 100;
                Log.Info($"Perf: poll avg {pollTotalMs / (double)polls:0} ms ({string.Join(", ", perHardware.Select(kv => $"{kv.Key} {kv.Value / polls}"))}) · CoreScope CPU {cpu:0.0}% of machine · {readings.Count} sensors · working set {self.WorkingSet64 / 1048576} MB");
                polls = 0;
                pollTotalMs = 0;
                perHardware.Clear();
                selfCpuStart = self.TotalProcessorTime;
                wallStart = DateTime.UtcNow;
            }

            _stop.Wait(Math.Max(250, IntervalMs));
        }
        RestoreFans(); // always hand fans back to the BIOS before the engine stops
    }

    /// <summary>Windows exposes every NDIS filter (QoS, WFP, Native WiFi…) as an "adapter"; they duplicate the real one's traffic.</summary>
    private static readonly System.Text.RegularExpressions.Regex NetworkNoise = new(
        @"Filter|QoS|WFP|Scheduler|Kernel Debugger|Local Area Connection\*|Miniport|Teredo|isatap|Loopback|Bluetooth Network",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static void Collect(IHardware hardware, List<SensorReading> readings, bool update = true)
    {
        if (hardware.HardwareType == HardwareType.Network && NetworkNoise.IsMatch(hardware.Name)) return;
        if (update) hardware.Update(); // otherwise re-report the last values (slow-polled hardware)
        var groupId = hardware.Identifier.ToString();
        var kind = hardware.HardwareType.ToString();
        foreach (var sensor in hardware.Sensors)
        {
            readings.Add(new SensorReading(
                sensor.Identifier.ToString(),
                groupId,
                Format.CleanName(hardware.Name),
                kind,
                sensor.Name,
                sensor.SensorType.ToString(),
                sensor.Value,
                sensor.Min,
                sensor.Max));
        }
        foreach (var sub in hardware.SubHardware) Collect(sub, readings, update);
    }

    /// <summary>Writes the first snapshot to sensors.txt — invaluable for tuning sensor matching on new hardware.</summary>
    private static void DumpSensors(IReadOnlyList<SensorReading> readings)
    {
        var text = new StringBuilder();
        foreach (var group in readings.GroupBy(r => r.GroupId))
        {
            var first = group.First();
            text.AppendLine(CultureInfo.InvariantCulture, $"[{first.Kind}] {first.GroupName}  ({first.GroupId})");
            foreach (var r in group) text.AppendLine(CultureInfo.InvariantCulture, $"    {r.Type,-12} {r.Name,-36} {r.Value}");
        }
        Log.WriteFile("sensors.txt", text.ToString());
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread.IsAlive) _thread.Join(3000);
        _win?.Dispose();
        try { _computer?.Close(); }
        catch (Exception ex) { Log.Error("Closing LibreHardwareMonitor", ex); }
        _stop.Dispose();
    }
}
