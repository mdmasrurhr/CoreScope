using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CoreScope.Core.Fixes;
using CoreScope.Core.Network;

namespace CoreScope.Core.Hardware;

public enum Severity { Critical = 0, Warning = 1, Info = 2, Good = 3 }

/// <summary>
/// A finding plus its evidence: what was measured, which rule fired, and where the data came from.
/// The evidence powers the "Why?" panel so every conclusion is auditable.
/// </summary>
public sealed record Insight(Severity Severity, string Category, string Title, string Detail, string? Action = null)
{
    public string SeverityName => Severity.ToString();
    public string? Measured { get; init; }
    public string? Rule { get; init; }
    public string? Source { get; init; }
    public bool HasEvidence => Measured is not null || Rule is not null || Source is not null;
    /// <summary>Optional one-click network fix (see Network.NetworkFixes); shown as a button next to the finding.</summary>
    public string? FixKey { get; init; }
    public string FixText => FixKey is null ? "" : Network.NetworkFixes.ButtonText(FixKey);
    public bool HasFix => FixKey is not null;

    /// <summary>What can be done about this finding: CoreScope fixes it, or opens exactly where to fix it, or walks you through it.</summary>
    public IReadOnlyList<FixAction> Fixes { get; init; } = Array.Empty<FixAction>();

    /// <summary>The network fix (if any) followed by <see cref="Fixes"/>; this is what the buttons show.</summary>
    public IReadOnlyList<FixAction> AllFixes => FixKey is null
        ? Fixes
        : new[] { new FixAction(FixKey, FixText, FixKind.Command, "", null, NetworkFixes.NeedsAdmin(FixKey), true) }.Concat(Fixes).ToList();

    public bool HasFixes => FixKey is not null || Fixes.Count > 0;

    /// <summary>The fix buttons bound to this finding (what the card shows).</summary>
    public IReadOnlyList<FixButton> Buttons => AllFixes.Select(a => new FixButton(a, this)).ToList();

    /// <summary>Progress and outcome of the last fix attempt. Shared by copies of this finding so the card keeps showing it.</summary>
    public FixState State { get; } = new();
}

/// <summary>Live state of a finding's fix buttons.</summary>
public sealed class FixState : ObservableObject
{
    private bool _isBusy;
    private string _result = "";
    private bool _resultOk;

    public bool IsBusy { get => _isBusy; set { if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(IsIdle)); } }
    public bool IsIdle => !_isBusy;
    public string Result { get => _result; set { if (Set(ref _result, value)) OnPropertyChanged(nameof(HasResult)); } }
    public bool HasResult => _result.Length > 0;
    public bool ResultOk { get => _resultOk; set => Set(ref _resultOk, value); }
}

/// <summary>
/// Turns raw facts and live sensor history into plain-English findings. Static checks run against
/// the startup <see cref="SystemSpec"/>; live checks use a short rolling window of sensor samples
/// so a single spike never triggers a warning.
/// </summary>
public sealed partial class InsightEngine
{
    // ── Thresholds (one place, so the "Why?" text and the logic can never disagree) ──
    private const int WindowSize = 15;
    private const double CpuTempCritical = 95, CpuTempWarning = 85;
    private const double ThrottleLoadPercent = 80, ThrottleClockFraction = 0.75, ThrottleSampleFraction = 0.8;
    private const int XmpToleranceMts = 100;
    private const double RamPressurePercent = 90;
    private const int SsdWearCritical = 90, SsdWearWarning = 70;
    private const int SsdTempWarning = 70;
    private const double DiskFreeCritical = 0.05, DiskFreeWarning = 0.12;
    private const int GpuDriverMaxAgeDays = 365;
    private const double GpuTempWarning = 88;
    private const int BiosMaxAgeDays = 365 * 2;
    private const double BatteryWearWarning = 40, BatteryWearInfo = 20;
    private const int UptimeMaxDays = 14;

    private const string LiveSource = "LibreHardwareMonitor live sensors";

    private readonly Queue<(double? Load, double? Clock, double? Temp)> _cpuWindow = new();
    private double _peakCpuTemp;
    private double _peakGpuTemp;
    private bool _sawSensors;
    private bool _sawCpuTemperature;

    /// <summary>Feed every sensor snapshot (called on the UI thread).</summary>
    public void Observe(IReadOnlyList<SensorReading> s)
    {
        if (s.Count == 0) return;
        _sawSensors = true;
        var temp = SensorPick.CpuTemperature(s);
        if (temp is { } t)
        {
            _sawCpuTemperature = true;
            _peakCpuTemp = Math.Max(_peakCpuTemp, t);
        }
        if (SensorPick.GpuTemperature(s) is { } g) _peakGpuTemp = Math.Max(_peakGpuTemp, g);

        _cpuWindow.Enqueue((SensorPick.CpuLoad(s), SensorPick.CpuClock(s), temp));
        while (_cpuWindow.Count > WindowSize) _cpuWindow.Dequeue();
    }

    public List<Insight> Evaluate(SystemSpec spec, HealthSnapshot? health = null)
    {
        var list = new List<Insight>();
        EvaluateSensorsAvailability(spec, list);
        EvaluateCpu(spec, list);
        EvaluateMemory(spec, list);
        EvaluateStorage(spec, list);
        EvaluateGpu(spec, list);
        EvaluateFirmware(spec, list);
        EvaluateBattery(spec, list);
        EvaluateOs(spec, list);
        EvaluateDevices(spec, list);
        EvaluateHealth(spec, health, list);
        AttachFixes(spec, health, list);
        return list.OrderBy(i => i.Severity).ToList();
    }

    private void EvaluateSensorsAvailability(SystemSpec spec, List<Insight> list)
    {
        if (!spec.Os.IsAdmin)
        {
            list.Add(new Insight(Severity.Info, "CoreScope", "Limited mode: some sensors are hidden",
                "Drive health (SMART), the TPM and a few Control Center actions need administrator access. Everything else, including temperatures, works without it.",
                "Click 'Unlock full sensors' at the top of the window (Windows asks for permission once).")
            {
                Measured = "Process token is not in the Administrators role",
                Rule = "Warn whenever CoreScope runs without elevation",
                Source = "WindowsPrincipal.IsInRole(Administrator)",
            });
        }
        else if (_sawSensors && !_sawCpuTemperature)
        {
            list.Add(new Insight(Severity.Info, "CoreScope", "This PC doesn't report a CPU temperature to Windows",
                "CoreScope reads temperatures without installing any driver: from the processor's built-in graphics on AMD APUs, or from the firmware's ACPI thermal zones. This PC exposes neither, so CPU temperature isn't shown. Everything else works normally.",
                null)
            {
                Measured = "No thermal zone or APU temperature reported",
                Rule = "Explain when sensors run but no CPU temperature source exists",
                Source = @"Windows performance counters \Thermal Zone Information, AMD display driver",
            });
        }
    }

    private void EvaluateCpu(SystemSpec spec, List<Insight> list)
    {
        var cpu = spec.Cpu;
        var tempRule = $"≥ {CpuTempCritical:0} °C critical · ≥ {CpuTempWarning:0} °C warning · otherwise pass (uses the hottest reading since CoreScope started)";
        var tempMeasured = $"Peak CPU temperature {_peakCpuTemp:0.0} °C over {(_cpuWindow.Count < WindowSize ? _cpuWindow.Count : WindowSize)}+ s of monitoring";
        if (_peakCpuTemp >= CpuTempCritical)
        {
            list.Add(new Insight(Severity.Critical, "Processor", $"CPU hit {_peakCpuTemp:0} °C",
                "That is at or near the thermal limit, where the CPU protects itself by slowing down (thermal throttling).",
                "Check that fans spin freely, clean dust from vents/heatsink, and on desktops consider re-applying thermal paste.")
            { Measured = tempMeasured, Rule = tempRule, Source = LiveSource });
        }
        else if (_peakCpuTemp >= CpuTempWarning)
        {
            list.Add(new Insight(Severity.Warning, "Processor", $"CPU running hot (peak {_peakCpuTemp:0} °C)",
                "Safe for short bursts, but sustained temperatures this high often reduce boost clocks and fan noise goes up.",
                "Improve airflow; on laptops use a hard, flat surface and keep vents clear.")
            { Measured = tempMeasured, Rule = tempRule, Source = LiveSource });
        }
        else if (_sawCpuTemperature && _cpuWindow.Count >= 5)
        {
            list.Add(new Insight(Severity.Good, "Processor", $"CPU temperature is healthy (peak {_peakCpuTemp:0} °C so far)",
                "Cooling is keeping up with the current workload.")
            { Measured = tempMeasured, Rule = tempRule, Source = LiveSource });
        }

        // Sustained high load while clocks sit well below base frequency = power or thermal limits.
        if (_cpuWindow.Count == WindowSize && cpu.BaseClockMhz > 0)
        {
            var clockLimit = cpu.BaseClockMhz * ThrottleClockFraction;
            var throttled = _cpuWindow.Count(w => w.Load > ThrottleLoadPercent && w.Clock is { } c && c < clockLimit);
            var needed = (int)Math.Ceiling(WindowSize * ThrottleSampleFraction);
            if (throttled >= needed)
            {
                var avgLoad = _cpuWindow.Average(w => w.Load ?? 0);
                var avgClock = _cpuWindow.Average(w => w.Clock ?? 0);
                list.Add(new Insight(Severity.Warning, "Processor", "CPU is being held below its base clock under load",
                    $"Load is high but cores average about {avgClock:0} MHz versus a {cpu.BaseClockMhz} MHz base clock. That points to power limits (battery saver, power plan) or heat.",
                    "Plug in the charger, switch Windows power mode to Best performance, and check temperatures.")
                {
                    Measured = $"{throttled} of the last {WindowSize} samples throttled · average load {avgLoad:0}% · average clock {avgClock:0} MHz · base {cpu.BaseClockMhz} MHz",
                    Rule = $"Warn if ≥ {needed} of {WindowSize} one-second samples have load > {ThrottleLoadPercent:0}% AND clock < {ThrottleClockFraction:P0} of base ({clockLimit:0} MHz)",
                    Source = $"{LiveSource} (effective core clocks) + Win32_Processor.MaxClockSpeed",
                });
            }
        }

        if (cpu.VirtualizationFirmwareEnabled == false)
        {
            list.Add(new Insight(Severity.Info, "Processor", "Hardware virtualization is disabled in firmware",
                "VT-x / AMD-V is turned off. You need it for WSL2, Docker, Android emulators and virtual machines.",
                "Enable 'Intel Virtualization Technology' or 'SVM Mode' in your BIOS/UEFI setup.")
            {
                Measured = "VirtualizationFirmwareEnabled = False",
                Rule = "Tip whenever firmware reports virtualization disabled",
                Source = "WMI Win32_Processor",
            });
        }
    }

    private static void EvaluateMemory(SystemSpec spec, List<Insight> list)
    {
        var modules = spec.Modules;
        if (modules.Count == 0) return;

        var running = modules.Where(m => m.ConfiguredMts > 0).Select(m => m.ConfiguredMts).DefaultIfEmpty(0).Min();
        var kitModule = modules.Where(m => m.PartNumberMts is not null).OrderByDescending(m => m.PartNumberMts).FirstOrDefault();
        var kitSpeed = kitModule?.PartNumberMts ?? 0;
        bool soldered = modules.All(m => m.TypeName.StartsWith("LP", StringComparison.Ordinal));
        const string memSource = "WMI Win32_PhysicalMemory (ConfiguredClockSpeed, PartNumber)";
        var xmpRule = $"Warn if configured speed is more than {XmpToleranceMts} MT/s below the speed encoded in the part number (e.g. F4-3600C16, KF436C17, CMK…3200C16). Silent when the part number doesn't encode a speed.";

        if (running > 0 && kitSpeed > running + XmpToleranceMts)
        {
            list.Add(new Insight(Severity.Warning, "Memory", $"RAM running at {running} MT/s, but the kit is rated for {kitSpeed}",
                "Your memory's part number indicates a faster XMP/EXPO profile that isn't enabled. You're leaving free performance on the table, especially in games and on AMD Ryzen.",
                "Enter BIOS/UEFI and enable XMP (Intel) or EXPO/DOCP (AMD), then confirm stability.")
            {
                Measured = $"Configured {running} MT/s · part number {kitModule!.PartNumber} → {kitSpeed} MT/s",
                Rule = xmpRule,
                Source = memSource,
            });
        }
        else if (running > 0 && kitSpeed > 0)
        {
            list.Add(new Insight(Severity.Good, "Memory", $"RAM is running at its rated {running} MT/s", "The memory profile matches what the kit is built for.")
            {
                Measured = $"Configured {running} MT/s · part number {kitModule!.PartNumber} → {kitSpeed} MT/s",
                Rule = xmpRule,
                Source = memSource,
            });
        }

        if (modules.Count == 1 && !soldered)
        {
            var slotsHint = spec.MemorySlots > 1 ? $" You have {spec.MemorySlots - 1} free slot(s)." : "";
            list.Add(new Insight(Severity.Warning, "Memory", "Memory is running in single-channel mode",
                $"With one module the CPU only gets half its memory bandwidth, which hurts integrated graphics and games most.{slotsHint}",
                "Add a matching module (same size and speed) to enable dual-channel.")
            {
                Measured = $"1 module installed ({modules[0].TypeName} {modules[0].FormFactor}) · {spec.MemorySlots} slot(s) on board",
                Rule = "Warn when exactly one removable (non-LPDDR) module is installed",
                Source = "WMI Win32_PhysicalMemory + Win32_PhysicalMemoryArray",
            });
        }

        var sizes = modules.Select(m => m.CapacityBytes).Distinct().ToList();
        if (sizes.Count > 1)
        {
            list.Add(new Insight(Severity.Info, "Memory", "Mixed module sizes installed",
                "Unequal modules usually still run dual-channel for part of the memory (flex mode), but matched pairs perform best.")
            {
                Measured = string.Join(" + ", modules.Select(m => Format.Bytes(m.CapacityBytes))),
                Rule = "Tip when installed modules differ in capacity",
                Source = "WMI Win32_PhysicalMemory.Capacity",
            });
        }

        if (Native.GetMemoryStatus() is { } status && status.Total > 0)
        {
            var load = 100.0 * (status.Total - status.Available) / status.Total;
            if (load > RamPressurePercent)
            {
                list.Add(new Insight(Severity.Warning, "Memory", $"Memory is {load:0}% full",
                    "Windows is close to running out of RAM and will start paging to disk, which makes everything feel slow.",
                    "Close unused apps/browser tabs, or consider more RAM.")
                {
                    Measured = $"{Format.Bytes(status.Total - status.Available)} used of {Format.Bytes(status.Total)} ({load:0.0}%)",
                    Rule = $"Warn when physical memory use exceeds {RamPressurePercent:0}%",
                    Source = "Windows GlobalMemoryStatusEx",
                });
            }
        }
    }

    private static void EvaluateStorage(SystemSpec spec, List<Insight> list)
    {
        const string reliabilitySource = "Windows Storage API: MSFT_PhysicalDisk + MSFT_StorageReliabilityCounter";
        var wearRule = $"Life left = 100 − Wear. Wear ≥ {SsdWearCritical}% critical · ≥ {SsdWearWarning}% warning · otherwise pass (healthy drives only)";

        foreach (var d in spec.Disks)
        {
            if (d.Health is "Warning" or "Unhealthy")
            {
                list.Add(new Insight(Severity.Critical, "Storage", $"{d.Name} reports '{d.Health}' health",
                    "Windows' storage health check flagged this drive. Failure risk is elevated.",
                    "Back up important data now, then check the drive vendor's tool.")
                {
                    Measured = $"HealthStatus = {d.Health}",
                    Rule = "Critical whenever Windows reports Warning or Unhealthy",
                    Source = reliabilitySource,
                });
            }

            if (d.WearPercent is { } wear)
            {
                var measured = $"Wear (rated endurance used) = {wear}% → {100 - wear}% life left"
                               + (d.PowerOnHours is { } h ? $" · {h:N0} power-on hours" : "");
                if (wear >= SsdWearCritical)
                {
                    list.Add(new Insight(Severity.Critical, "Storage", $"{d.Name} is near the end of its rated life ({100 - wear}% left)",
                        "The SSD has used most of its rated write endurance.", "Back up and plan a replacement.")
                    { Measured = measured, Rule = wearRule, Source = reliabilitySource });
                }
                else if (wear >= SsdWearWarning)
                {
                    list.Add(new Insight(Severity.Warning, "Storage", $"{d.Name} has {100 - wear}% life left",
                        "The SSD has used most of its write endurance. It may still last a long time, but keep backups current.")
                    { Measured = measured, Rule = wearRule, Source = reliabilitySource });
                }
                else if (d.MediaType == "SSD" && d.Health == "Healthy")
                {
                    list.Add(new Insight(Severity.Good, "Storage", $"{d.Name}: healthy, {100 - wear}% life left", "No storage issues detected.")
                    { Measured = measured, Rule = wearRule, Source = reliabilitySource });
                }
            }

            if (d.TemperatureC is { } t && t >= SsdTempWarning)
            {
                list.Add(new Insight(Severity.Warning, "Storage", $"{d.Name} is hot ({t} °C)",
                    "NVMe SSDs throttle around 70–80 °C, slowing transfers.",
                    "Add a heatsink to the M.2 drive or improve case airflow.")
                {
                    Measured = $"Drive temperature {t} °C" + (d.TemperatureMaxC is { } tm ? $" (drive's rated max {tm} °C)" : ""),
                    Rule = $"Warn at ≥ {SsdTempWarning} °C",
                    Source = reliabilitySource,
                });
            }

            if (d.MediaType == "HDD" && spec.Disks.Count == 1)
            {
                list.Add(new Insight(Severity.Info, "Storage", "Windows is on a hard disk drive",
                    "A spinning HDD is the single biggest cause of slow boots and app launches.",
                    "Upgrading to any SSD makes the whole PC feel several times faster.")
                {
                    Measured = $"Only drive: {d.Name} (MediaType = HDD)",
                    Rule = "Tip when the system's only drive is a hard disk",
                    Source = "MSFT_PhysicalDisk.MediaType",
                });
            }
        }

        var diskRule = $"Free space < {DiskFreeCritical:P0} critical · < {DiskFreeWarning:P0} warning";
        foreach (var v in spec.Volumes)
        {
            if (v.TotalBytes <= 0) continue;
            var measured = $"{Format.Bytes(v.FreeBytes)} free of {Format.Bytes(v.TotalBytes)} ({v.FreeFraction:P1}) at startup";
            if (v.FreeFraction < DiskFreeCritical)
            {
                list.Add(new Insight(Severity.Critical, "Storage", $"Drive {v.Name} is almost full ({v.FreeFraction:P0} free)",
                    $"Only {Format.Bytes(v.FreeBytes)} left. Windows updates can fail and SSDs slow down when nearly full.",
                    "Run Disk Cleanup / Storage Sense, or move large files elsewhere.")
                { Measured = measured, Rule = diskRule, Source = "System.IO.DriveInfo" });
            }
            else if (v.FreeFraction < DiskFreeWarning)
            {
                list.Add(new Insight(Severity.Warning, "Storage", $"Drive {v.Name} is getting full ({v.FreeFraction:P0} free)",
                    $"{Format.Bytes(v.FreeBytes)} remaining.", "Free up some space soon.")
                { Measured = measured, Rule = diskRule, Source = "System.IO.DriveInfo" });
            }
        }
    }

    private void EvaluateGpu(SystemSpec spec, List<Insight> list)
    {
        foreach (var g in spec.Gpus.Where(g => !g.IsIntegrated && g.Vendor is "NVIDIA" or "AMD"))
        {
            if (g.DriverDate is { } date && (DateTime.Now - date).TotalDays > GpuDriverMaxAgeDays)
            {
                list.Add(new Insight(Severity.Info, "Graphics", $"{g.Name} driver is {Format.Age(date).Replace(" ago", "")} old",
                    "Newer drivers bring performance fixes for recent games and apps.",
                    g.Vendor == "NVIDIA" ? "Update via the NVIDIA App or nvidia.com/drivers." : "Update via AMD Software: Adrenalin Edition.")
                {
                    Measured = $"Driver {g.DriverVersion} dated {Format.Date(date)} ({(DateTime.Now - date).TotalDays:0} days)",
                    Rule = $"Tip when a discrete NVIDIA/AMD driver is older than {GpuDriverMaxAgeDays} days (integrated GPUs skipped — OEMs often pin those)",
                    Source = "WMI Win32_VideoController.DriverDate",
                });
            }
        }
        if (_peakGpuTemp >= GpuTempWarning)
        {
            list.Add(new Insight(Severity.Warning, "Graphics", $"GPU reached {_peakGpuTemp:0} °C",
                "High GPU temperatures reduce boost clocks and increase fan noise.",
                "Clean dust from the graphics card and check case airflow.")
            {
                Measured = $"Peak GPU temperature {_peakGpuTemp:0.0} °C since CoreScope started",
                Rule = $"Warn at ≥ {GpuTempWarning:0} °C",
                Source = LiveSource,
            });
        }
    }

    private static void EvaluateFirmware(SystemSpec spec, List<Insight> list)
    {
        var b = spec.Board;
        if (b.BiosDate is { } date && (DateTime.Now - date).TotalDays > BiosMaxAgeDays)
        {
            list.Add(new Insight(Severity.Info, "Motherboard", $"BIOS/UEFI is {Format.Age(date).Replace(" ago", "")} old",
                "Firmware updates often add CPU microcode security fixes, stability and memory-compatibility improvements.",
                $"Check {(b.SystemVendor.Length > 0 ? b.SystemVendor : b.BoardVendor)}'s support page for a version newer than {b.BiosVersion}.")
            {
                Measured = $"Version {b.BiosVersion} released {Format.Date(date)} ({(DateTime.Now - date).TotalDays:0} days ago)",
                Rule = $"Tip when firmware is older than {BiosMaxAgeDays / 365} years",
                Source = "WMI Win32_BIOS.ReleaseDate",
            });
        }

        const string secureBootSource = @"Registry HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State\UEFISecureBootEnabled";
        if (b.SecureBoot == false)
        {
            list.Add(new Insight(Severity.Warning, "Security", "Secure Boot is off",
                "Secure Boot blocks bootkits and is required by some games' anti-cheat systems.",
                "Enable Secure Boot in BIOS/UEFI (system must be in UEFI mode).")
            { Measured = $"UEFISecureBootEnabled = 0 · firmware type {b.FirmwareType}", Rule = "Warn when Secure Boot is disabled", Source = secureBootSource });
        }
        else if (b.SecureBoot == true)
        {
            list.Add(new Insight(Severity.Good, "Security", "Secure Boot is on", "Your boot chain is protected.")
            { Measured = "UEFISecureBootEnabled = 1", Rule = "Pass when Secure Boot is enabled", Source = secureBootSource });
        }

        if (b.TpmPresent == false)
        {
            list.Add(new Insight(Severity.Warning, "Security", "No TPM detected",
                "A TPM 2.0 is required for Windows 11 and used by BitLocker and Windows Hello.",
                "Enable fTPM (AMD) or PTT (Intel) in BIOS/UEFI.")
            { Measured = "Win32_Tpm returned no device (running as administrator)", Rule = "Warn when no TPM is visible", Source = @"WMI root\CIMV2\Security\MicrosoftTpm\Win32_Tpm" });
        }
        else if (b.TpmPresent == true)
        {
            list.Add(new Insight(Severity.Good, "Security", $"TPM {b.TpmVersion} is active", "Hardware security features are available.")
            {
                Measured = $"SpecVersion {b.TpmVersion}" + (b.TpmManufacturer.Length > 0 ? $" · manufacturer {b.TpmManufacturer}" : ""),
                Rule = "Pass when a TPM is present and enabled",
                Source = @"WMI root\CIMV2\Security\MicrosoftTpm\Win32_Tpm",
            });
        }
    }

    private static void EvaluateBattery(SystemSpec spec, List<Insight> list)
    {
        if (spec.Battery is not { WearPercent: { } wear } battery) return;
        var cycles = battery.Cycles is { } c ? $" after {c} charge cycles" : "";
        var full = battery.FullChargeMwh / 1000.0;
        var design = battery.DesignMwh / 1000.0;
        var measured = $"Wear = 1 − {full:0.00} Wh ÷ {design:0.00} Wh = {wear:0.0}%" + (battery.Cycles is { } cc ? $" · {cc} cycles" : "");
        var rule = $"Wear ≥ {BatteryWearWarning:0}% warning · ≥ {BatteryWearInfo:0}% tip · otherwise pass";
        const string source = @"WMI root\wmi: BatteryStaticData.DesignedCapacity, BatteryFullChargedCapacity";

        if (wear >= BatteryWearWarning)
        {
            list.Add(new Insight(Severity.Warning, "Battery", $"Battery has lost {wear:0}% of its capacity",
                $"It now holds {full:0.0} Wh of its original {design:0.0} Wh{cycles}.",
                "Consider a battery replacement if runtime matters to you.")
            { Measured = measured, Rule = rule, Source = source });
        }
        else if (wear >= BatteryWearInfo)
        {
            list.Add(new Insight(Severity.Info, "Battery", $"Battery wear is {wear:0}%",
                $"Normal aging{cycles}. Holds {full:0.0} of {design:0.0} Wh.",
                "Many laptops offer a charge limit (e.g. 80%) in the vendor app, which slows further wear.")
            { Measured = measured, Rule = rule, Source = source });
        }
        else
        {
            list.Add(new Insight(Severity.Good, "Battery", $"Battery is in good shape ({100 - wear:0}% of design capacity)", $"Wear is minimal{cycles}.")
            { Measured = measured, Rule = rule, Source = source });
        }
    }

    private static void EvaluateDevices(SystemSpec spec, List<Insight> list)
    {
        var broken = spec.Devices.FirstOrDefault(c => c.Title == "Needs attention")?.Devices ?? new List<DeviceEntry>();
        if (broken.Count > 0)
        {
            list.Add(new Insight(Severity.Warning, "Devices",
                broken.Count == 1 ? $"{broken[0].Name} isn't working" : $"{broken.Count} devices aren't working",
                string.Join("; ", broken.Take(3).Select(d => $"{d.Name}: {d.Problem}")) + (broken.Count > 3 ? "; …" : "."),
                "Open the Devices page for details, then update or reinstall the driver from the PC or device maker's website.")
            {
                Measured = string.Join(" · ", broken.Select(d => $"{d.Name} = code {d.ErrorCode}")),
                Rule = "Warn for any device whose problem code isn't 0 (working), 22 (disabled by you) or 45 (unplugged)",
                Source = "WMI Win32_PnPEntity.ConfigManagerErrorCode",
            });
        }
        if (spec.Os.TrimEnabled == false && spec.Disks.Any(d => d.MediaType == "SSD"))
        {
            list.Add(new Insight(Severity.Warning, "Storage", "TRIM is disabled",
                "Without TRIM, SSDs gradually slow down and wear faster because they can't tell which blocks are free.",
                "Run 'fsutil behavior set DisableDeleteNotify 0' in an administrator terminal.")
            {
                Measured = "NTFS DisableDeleteNotify = 1",
                Rule = "Warn when an SSD is present and TRIM is off",
                Source = "fsutil behavior query DisableDeleteNotify",
            });
        }
    }

    private static void EvaluateOs(SystemSpec spec, List<Insight> list)
    {
        if (spec.Os.LastBoot is { } boot && (DateTime.Now - boot).TotalDays > UptimeMaxDays)
        {
            var days = (DateTime.Now - boot).TotalDays;
            list.Add(new Insight(Severity.Info, "Windows", $"Up for {days:0} days without a restart",
                "Long uptimes leave pending updates uninstalled and let memory leaks accumulate. (Shut Down with Fast Startup doesn't count as a restart.)",
                "Restart when convenient.")
            {
                Measured = $"Last boot {boot:MMM d, HH:mm} ({days:0.0} days ago)",
                Rule = $"Tip when uptime exceeds {UptimeMaxDays} days",
                Source = "WMI Win32_OperatingSystem.LastBootUpTime",
            });
        }
        if (spec.Os.PowerPlan == "Power saver")
        {
            list.Add(new Insight(Severity.Info, "Windows", "Power saver plan is active",
                "CPU and GPU performance is capped to save energy.",
                "Switch to Balanced or Best performance when you need speed.")
            {
                Measured = "Active power scheme = Power saver (a1841308-…)",
                Rule = "Tip when the Power saver plan is active",
                Source = @"Registry HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes",
            });
        }
    }
}
