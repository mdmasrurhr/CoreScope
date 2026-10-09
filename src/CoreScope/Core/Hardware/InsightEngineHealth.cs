using System;
using System.Collections.Generic;
using System.Linq;
using CoreScope.Core.Fixes;

namespace CoreScope.Core.Hardware;

/// <summary>
/// The wider health checks (security, updates, recovery, maintenance, startup, display) and the fix buttons for every finding.
/// Every check reports both ways: a problem with a way to fix it, or a "Good" result so you can see it was checked.
/// </summary>
public sealed partial class InsightEngine
{
    // ── Thresholds for the health checks ──
    private const int SignatureWarnDays = 3, SignatureCriticalDays = 14;
    private const int UpdateWarnDays = 60, UpdateCriticalDays = 120;
    private const long TempNoteBytes = 500L << 20, RecycleNoteBytes = 1L << 30, HibernateNoteBytes = 6L << 30;
    private const int StartupWarn = 12, StartupNote = 8;
    private const int RestorePointWarnDays = 60;
    private const int SupportEndSoonDays = 120;

    private void EvaluateHealth(SystemSpec spec, HealthSnapshot? h, List<Insight> list)
    {
        EvaluateWindowsSupport(spec, list);
        if (h is null) return;
        EvaluateSecurity(spec, h, list);
        EvaluateUpdates(h, list);
        EvaluateRecovery(spec, h, list);
        EvaluateMaintenance(spec, h, list);
        EvaluateStartup(h, list);
        EvaluateDisplay(h, list);
    }

    // ───────────────────────── Windows version support ─────────────────────────

    private sealed record Lifecycle(int Build, string Name, DateTime EndHomePro, DateTime EndWorkplace);

    private static readonly Lifecycle[] Lifecycles =
    {
        new(19044, "Windows 10 21H2", new(2023, 6, 13), new(2024, 6, 11)),
        new(19045, "Windows 10 22H2", new(2025, 10, 14), new(2025, 10, 14)),
        new(22000, "Windows 11 21H2", new(2023, 10, 10), new(2024, 10, 8)),
        new(22621, "Windows 11 22H2", new(2024, 10, 8), new(2025, 10, 14)),
        new(22631, "Windows 11 23H2", new(2025, 11, 11), new(2026, 11, 10)),
        new(26100, "Windows 11 24H2", new(2026, 10, 13), new(2027, 10, 12)),
        new(26200, "Windows 11 25H2", new(2027, 10, 12), new(2028, 10, 10)),
    };

    private static void EvaluateWindowsSupport(SystemSpec spec, List<Insight> list)
    {
        if (!int.TryParse(spec.Os.Build, out var build)) return;
        var entry = Lifecycles.LastOrDefault(l => l.Build <= build && (build - l.Build) < 1000);
        if (entry is null)
        {
            if (build < 19044) list.Add(Make(Severity.Critical, "Windows", "This version of Windows is no longer supported",
                $"Build {build} stopped receiving security updates years ago, so new threats are never patched.",
                "Upgrade to the current version of Windows.", $"Windows build {build}", "Critical when the build is older than any supported release", "Microsoft Windows lifecycle"));
            return;
        }
        var workplace = spec.Os.Name.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) || spec.Os.Name.Contains("Education", StringComparison.OrdinalIgnoreCase);
        var end = workplace ? entry.EndWorkplace : entry.EndHomePro;
        var days = (int)(end.Date - DateTime.Today).TotalDays;
        const string rule = "Critical once support has ended · warning in the last 120 days · otherwise pass";
        const string source = "Microsoft Windows lifecycle dates (Home/Pro or Enterprise/Education, by edition)";
        var measured = $"{entry.Name} ({(workplace ? "Enterprise/Education" : "Home/Pro")}) support ends {end:MMM d, yyyy}";
        if (days < 0)
            list.Add(Make(Severity.Critical, "Windows", $"{entry.Name} no longer gets security updates",
                $"Microsoft ended support for this version on {end:MMMM d, yyyy}. New security holes are no longer fixed on this PC (unless you are enrolled in Extended Security Updates).",
                "Update to the newest version of Windows.", measured, rule, source));
        else if (days <= SupportEndSoonDays)
            list.Add(Make(Severity.Warning, "Windows", $"{entry.Name} support ends in {days} day{(days == 1 ? "" : "s")}",
                $"Microsoft stops sending security updates for this version on {end:MMMM d, yyyy}.",
                "Update to the newest version of Windows before then.", measured, rule, source));
        else
            list.Add(Make(Severity.Good, "Windows", $"{entry.Name} is supported until {end:MMMM yyyy}",
                "Your version of Windows still receives security updates.", null, measured, rule, source));
    }

    // ───────────────────────── Security ─────────────────────────

    private static void EvaluateSecurity(SystemSpec spec, HealthSnapshot h, List<Insight> list)
    {
        // Antivirus
        var name = h.AntivirusName ?? "antivirus";
        const string avSource = "Windows Security Center (WMI AntivirusProduct) and Microsoft Defender status";
        if (h.AntivirusActive == false)
            list.Add(Make(Severity.Critical, "Security", "No antivirus is protecting this PC",
                "Nothing is scanning files or blocking malware in real time.",
                "Turn on Microsoft Defender (built into Windows) or install an antivirus you trust.",
                "No antivirus product reports as active", "Critical when Windows Security Center lists no active antivirus", avSource));
        else if (h.AntivirusActive == true && h.RealTimeProtection == false)
            list.Add(Make(Severity.Warning, "Security", $"Real-time protection is off in {name}",
                "Files are not scanned as they are opened or downloaded.",
                "Turn real-time protection back on in Windows Security.",
                "Real-time protection = off", "Warn when real-time protection is off", avSource));
        else if (h.AntivirusActive == true)
        {
            if (h.SignatureAgeDays is { } age && age >= SignatureWarnDays)
                list.Add(Make(age >= SignatureCriticalDays ? Severity.Critical : Severity.Warning, "Security", $"{name} virus definitions are {age} days old",
                    "Old definitions miss recent malware.", "Update the definitions now.",
                    $"Definitions {age} days old", $"≥ {SignatureWarnDays} days warning · ≥ {SignatureCriticalDays} days critical", avSource));
            else
                list.Add(Make(Severity.Good, "Security", $"{name} is on and up to date",
                    h.SignatureAgeDays is { } fresh ? $"Virus definitions are {fresh} day{(fresh == 1 ? "" : "s")} old." : "Real-time protection is active.",
                    null, "Antivirus active, definitions current", $"Pass when active and definitions < {SignatureWarnDays} days old", avSource));
        }

        // Firewall
        var off = new[] { ("Public", h.FirewallPublic), ("Private", h.FirewallPrivate), ("Domain", h.FirewallDomain) }.Where(p => p.Item2 == false).Select(p => p.Item1).ToList();
        const string fwSource = @"Registry HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";
        if (off.Count > 0)
            list.Add(Make(Severity.Warning, "Security", $"Windows Firewall is off for {string.Join(" and ", off)} networks",
                "Connections from other devices on those networks aren't filtered. This matters most on public Wi-Fi.",
                "Turn the firewall back on.", $"Firewall disabled for: {string.Join(", ", off)}", "Warn when any network profile has the firewall off", fwSource));
        else if (h.FirewallPublic == true || h.FirewallPrivate == true)
            list.Add(Make(Severity.Good, "Security", "Windows Firewall is on", "All network types are filtered.", null,
                "EnableFirewall = 1 on every profile", "Pass when the firewall is on for every profile", fwSource));

        // UAC
        if (h.UacEnabled == false)
            list.Add(Make(Severity.Warning, "Security", "User Account Control is off",
                "Programs can change system settings without asking, which lets malware do more damage.",
                "Set the User Account Control slider back to its default.", "EnableLUA = 0", "Warn when UAC is disabled",
                @"Registry HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"));
        else if (h.UacEnabled == true)
            list.Add(Make(Severity.Good, "Security", "User Account Control is on", "Windows asks before programs make system changes.", null,
                "EnableLUA = 1", "Pass when UAC is enabled", @"Registry HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"));

        // Disk encryption (laptops, where theft is the risk)
        if (h.SystemDriveEncrypted == false && spec.Battery is not null)
            list.Add(Make(Severity.Info, "Security", "This laptop's drive isn't encrypted",
                "If the laptop is lost or stolen, anyone can read the files by removing the drive. Device encryption / BitLocker prevents that.",
                "Turn on device encryption (keep your recovery key safe).", "BitLocker protection status = off", "Tip for laptops whose system drive isn't encrypted",
                "WMI Win32_EncryptableVolume.ProtectionStatus"));
        else if (h.SystemDriveEncrypted == true)
            list.Add(Make(Severity.Good, "Security", "The system drive is encrypted", "Your files are protected if the drive is removed.", null,
                "BitLocker protection status = on", "Pass when the system drive is encrypted", "WMI Win32_EncryptableVolume.ProtectionStatus"));

        // Remote access and sign-in
        if (h.RemoteDesktopOn == true)
            list.Add(Make(Severity.Info, "Security", "Remote Desktop is turned on",
                "Other computers can try to sign in to this PC over the network. Fine if you use it; a risk if you don't.",
                "Turn it off unless you need it.", "fDenyTSConnections = 0", "Tip when Remote Desktop connections are allowed", @"Registry HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server"));
        else if (h.RemoteDesktopOn == false)
            list.Add(Make(Severity.Good, "Security", "Remote Desktop is off", "Nobody can connect to this PC remotely.", null,
                "fDenyTSConnections = 1", "Pass when Remote Desktop is off", @"Registry HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server"));
        if (h.AutoSignIn == true)
            list.Add(Make(Severity.Info, "Security", "Windows signs in automatically",
                "Anyone who switches the PC on gets straight into your account and files.",
                "Require a password at startup unless the PC never leaves a secure place.", "AutoAdminLogon = 1", "Tip when automatic sign-in is configured",
                @"Registry HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"));
    }

    // ───────────────────────── Updates ─────────────────────────

    private static void EvaluateUpdates(HealthSnapshot h, List<Insight> list)
    {
        const string source = "Windows Update install history (registry, WMI Win32_QuickFixEngineering)";
        if (h.UpdateServiceDisabled == true)
            list.Add(Make(Severity.Warning, "Windows", "The Windows Update service is disabled",
                "This PC can't get security fixes until the service is allowed to run.",
                "Re-enable it, then check for updates.", "wuauserv start type = Disabled", "Warn when the Windows Update service is disabled",
                @"Registry HKLM\SYSTEM\CurrentControlSet\Services\wuauserv"));
        if (h.DaysSinceUpdate is { } days)
        {
            if (days >= UpdateWarnDays)
                list.Add(Make(days >= UpdateCriticalDays ? Severity.Critical : Severity.Warning, "Windows", $"No Windows update was installed in {days} days",
                    "Windows normally patches itself every month. A long gap means updates are failing, paused or blocked.",
                    "Open Windows Update and install what is waiting.", $"Last successful update {days} days ago",
                    $"≥ {UpdateWarnDays} days warning · ≥ {UpdateCriticalDays} days critical", source));
            else
                list.Add(Make(Severity.Good, "Windows", "Windows updates are current", $"The last update was installed {days} day{(days == 1 ? "" : "s")} ago.", null,
                    $"Last successful update {days} days ago", $"Pass when the last update is under {UpdateWarnDays} days old", source));
        }
        if (h.PendingReboot == true)
            list.Add(Make(Severity.Warning, "Windows", "Windows is waiting for a restart",
                "An update or installer finished but needs a restart to take effect, so the old, possibly vulnerable files are still in use.",
                "Restart when you are ready.", "A pending-restart flag is set", "Warn when Windows reports a pending restart",
                "Registry: Component Based Servicing, Windows Update, Session Manager"));
        else if (h.PendingReboot == false)
            list.Add(Make(Severity.Good, "Windows", "No restart is pending", "Nothing is waiting for a restart.", null,
                "No pending-restart flag", "Pass when no pending restart is flagged", "Registry: Component Based Servicing, Windows Update, Session Manager"));
    }

    // ───────────────────────── Recovery and time ─────────────────────────

    private static void EvaluateRecovery(SystemSpec spec, HealthSnapshot h, List<Insight> list)
    {
        const string source = @"Registry HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore, WMI SystemRestore";
        if (h.SystemRestoreOn == false)
            list.Add(Make(Severity.Info, "Windows", "System Restore is off",
                "If a bad driver or update breaks Windows, you can't roll back to a working state.",
                "Turn on System Restore for your system drive.", "System Restore disabled", "Tip when System Restore is off", source));
        else if (h.SystemRestoreOn == true)
        {
            if (h.DaysSinceRestorePoint is { } d && d >= RestorePointWarnDays)
                list.Add(Make(Severity.Info, "Windows", d == int.MaxValue ? "There is no restore point to go back to" : $"The newest restore point is {d} days old",
                    "A fresh restore point lets you undo a bad change in a few clicks.", "Create one now, before big changes.",
                    d == int.MaxValue ? "No restore points found" : $"Newest restore point {d} days old", $"Tip when the newest restore point is older than {RestorePointWarnDays} days", source));
            else
                list.Add(Make(Severity.Good, "Windows", "System Restore is on", h.DaysSinceRestorePoint is { } fresh && fresh != int.MaxValue ? $"The newest restore point is {fresh} days old." : "You can roll Windows back if an update goes wrong.", null,
                    "System Restore enabled", "Pass when System Restore is on", source));
        }

        if (h.ClockServiceRunning == false)
            list.Add(Make(Severity.Warning, "Windows", "The clock isn't syncing with the internet",
                "The Windows Time service is stopped. A drifting clock breaks secure websites, sign-ins and file timestamps.",
                "Start the service and sync the clock now.", "w32time service = stopped", "Warn when the Windows Time service is stopped", "WMI Win32_Service w32time"));
        else if (h.ClockServiceRunning == true)
            list.Add(Make(Severity.Good, "Windows", "The clock keeps itself in sync", "The Windows Time service is running.", null,
                "w32time service = running", "Pass when the Windows Time service is running", "WMI Win32_Service w32time"));
    }

    // ───────────────────────── Maintenance ─────────────────────────

    private static void EvaluateMaintenance(SystemSpec spec, HealthSnapshot h, List<Insight> list)
    {
        const string tempSource = @"Your temp folder and C:\Windows\Temp (files untouched for 24 hours)";
        if (h.TempBytes >= TempNoteBytes)
            list.Add(Make(Severity.Info, "Storage", $"{Format.Bytes(h.TempBytes)}{(h.TempMeasureComplete ? "" : "+")} of old temporary files",
                "Installers and apps leave temporary files behind. They are safe to remove once they are a day old.",
                "Clean them up. CoreScope removes only files untouched for 24 hours and skips anything in use.",
                $"{Format.Bytes(h.TempBytes)} removable", $"Tip when ≥ {Format.Bytes(TempNoteBytes)} of old temporary files exist", tempSource));
        else
            list.Add(Make(Severity.Good, "Storage", "Temporary files are under control", $"Only {Format.Bytes(h.TempBytes)} of old temporary files.", null,
                $"{Format.Bytes(h.TempBytes)} removable", $"Pass when under {Format.Bytes(TempNoteBytes)}", tempSource));

        if (h.RecycleBinBytes >= RecycleNoteBytes)
            list.Add(Make(Severity.Info, "Storage", $"The Recycle Bin holds {Format.Bytes(h.RecycleBinBytes)}",
                "Deleted files still use disk space until the Recycle Bin is emptied.", "Empty it once you are sure you don't need anything in it.",
                $"{Format.Bytes(h.RecycleBinBytes)} in the Recycle Bin", $"Tip when ≥ {Format.Bytes(RecycleNoteBytes)}", "Shell SHQueryRecycleBin"));
        else
            list.Add(Make(Severity.Good, "Storage", "The Recycle Bin is small", $"It holds {Format.Bytes(h.RecycleBinBytes)}.", null,
                $"{Format.Bytes(h.RecycleBinBytes)} in the Recycle Bin", $"Pass when under {Format.Bytes(RecycleNoteBytes)}", "Shell SHQueryRecycleBin"));

        var systemFree = spec.Volumes.FirstOrDefault(v => v.Name.StartsWith(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", StringComparison.OrdinalIgnoreCase));
        if (h.HibernationFileBytes >= HibernateNoteBytes && systemFree is { FreeFraction: < 0.3 })
            list.Add(Make(Severity.Info, "Storage", $"The hibernation file takes {Format.Bytes(h.HibernationFileBytes)}",
                "Windows reserves this space for hibernation and Fast Startup. On a drive that is filling up you may prefer the space back.",
                "Turn hibernation off to free it (this also turns off Fast Startup).", $"hiberfil.sys = {Format.Bytes(h.HibernationFileBytes)}",
                $"Tip when hiberfil.sys ≥ {Format.Bytes(HibernateNoteBytes)} and the system drive is under 30% free", @"C:\hiberfil.sys"));

        if (h.StorageSenseOn == false && spec.Volumes.Any(v => v.FreeFraction < 0.2))
            list.Add(Make(Severity.Info, "Storage", "Storage Sense is off",
                "Storage Sense clears temporary files and the Recycle Bin automatically so you don't have to.",
                "Turn it on.", "Storage Sense = off", "Tip when Storage Sense is off and a drive is under 20% free",
                @"Registry HKCU\Software\Microsoft\Windows\CurrentVersion\StorageSense"));

        if (h.PageFileMissing == true)
            list.Add(Make(Severity.Warning, "Performance", "Windows has no page file",
                "Without a page file, programs can crash when memory runs low, even if there is plenty of free RAM.",
                "Let Windows manage the page file size automatically.", "No page file configured", "Warn when no page file exists", "WMI Win32_PageFileSetting"));
    }

    // ───────────────────────── Startup load ─────────────────────────

    private static void EvaluateStartup(HealthSnapshot h, List<Insight> list)
    {
        var count = h.StartupApps.Count;
        var names = string.Join(", ", h.StartupApps.Take(6)) + (count > 6 ? $" and {count - 6} more" : "");
        const string source = @"Registry Run keys and Startup folders (the same list as Task Manager's Startup tab)";
        if (count >= StartupNote)
            list.Add(Make(count >= StartupWarn ? Severity.Warning : Severity.Info, "Startup", $"{count} programs start with Windows",
                $"Every one adds to boot time and uses memory in the background. Starting with Windows: {names}.",
                "Turn off the ones you don't need straight away.", $"{count} enabled startup programs",
                $"≥ {StartupWarn} warning · ≥ {StartupNote} tip", source));
        else
            list.Add(Make(Severity.Good, "Startup", "Startup load is light", count == 0 ? "No programs start with Windows." : $"{count} program{(count == 1 ? "" : "s")} start with Windows: {names}.", null,
                $"{count} enabled startup programs", $"Pass when under {StartupNote}", source));
    }

    // ───────────────────────── Display ─────────────────────────

    private static void EvaluateDisplay(HealthSnapshot h, List<Insight> list)
    {
        if (h.RefreshHz is not { } now || h.MaxRefreshHz is not { } max) return;
        const string source = "Windows display modes (EnumDisplaySettings)";
        if (max - now >= 20)
            list.Add(Make(Severity.Info, "Display", $"Your screen runs at {now} Hz but supports {max} Hz",
                "A higher refresh rate makes scrolling, mouse movement and games look smoother.", "Switch to the highest rate.",
                $"Now {now} Hz · supports {max} Hz at this resolution", "Tip when the screen supports at least 20 Hz more than it uses", source));
        else
            list.Add(Make(Severity.Good, "Display", $"The screen runs at its best refresh rate ({now} Hz)", "Nothing to change.", null,
                $"Now {now} Hz · supports {max} Hz", "Pass when within 20 Hz of the maximum", source));
    }

    // ───────────────────────── Helper ─────────────────────────

    private static Insight Make(Severity severity, string category, string title, string detail, string? action, string measured, string rule, string source) =>
        new(severity, category, title, detail, action) { Measured = measured, Rule = rule, Source = source };

    // ───────────────────────── Fix buttons ─────────────────────────

    /// <summary>
    /// Gives every finding the buttons that fix it or take you to the right place. Matching is by title in this one place, so the
    /// set of fixes can be audited (and is unit tested: no warning or critical finding is left without a way forward).
    /// </summary>
    private static void AttachFixes(SystemSpec spec, HealthSnapshot? h, List<Insight> list)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var insight = list[i];
            if (insight.Fixes.Count > 0 || insight.FixKey is not null) continue;
            var fixes = FixesFor(insight, spec, h);
            if (fixes.Count > 0) list[i] = insight with { Fixes = fixes };
        }
    }

    private static List<FixAction> FixesFor(Insight i, SystemSpec spec, HealthSnapshot? h)
    {
        var t = i.Title;
        var support = spec.Upgrade?.Links.FirstOrDefault()?.Url;
        bool Has(string s) => t.Contains(s, StringComparison.OrdinalIgnoreCase);
        bool Starts(string s) => t.StartsWith(s, StringComparison.OrdinalIgnoreCase);
        var amd = spec.Cpu.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase);

        // Good results have nothing to fix.
        if (i.Severity == Severity.Good) return new();

        // ── CoreScope itself ──
        if (Starts("Limited mode")) return new() { FixButtons.Unlock() };

        // ── Processor / graphics / memory ──
        if (Starts("CPU hit") || Starts("CPU running hot") || Starts("GPU reached"))
            return new() { FixButtons.TaskManager(), FixButtons.PowerSettings(), FixButtons.ControlCenter("Fan and power controls", false, "Power & performance") };
        if (Has("held below its base clock"))
            return new() { spec.Os.PowerPlan == "Power saver" ? FixButtons.Do(FixIds.BalancedPower, "Switch to Balanced power") : FixButtons.PowerSettings(true), FixButtons.TaskManager() };
        if (Has("virtualization is disabled"))
            return new() { FixButtons.Steps("Show me how", FixButtons.EnableVirtualization(support, amd), true) };
        if (Starts("RAM running at"))
        {
            var rated = System.Text.RegularExpressions.Regex.Matches(t, @"\d+").Select(m => int.Parse(m.Value)).ToList();
            return new() { FixButtons.Steps("Show me how", FixButtons.EnableMemoryProfile(support, rated.Count > 1 ? rated[1] : 0, rated.Count > 0 ? rated[0] : 0), true) };
        }
        if (Has("single-channel")) return new() { FixButtons.Steps("Show me how", FixButtons.FixSingleChannel(support), true) };
        if (Has("Mixed module sizes")) return new() { FixButtons.Page("See upgrade advice", "Upgrade", true) };
        if (Starts("Memory is") && Has("full"))
            return new() { FixButtons.TaskManager() with { Primary = true }, FixButtons.ControlCenter("Manage startup programs", false, "Startup apps"), FixButtons.Page("More memory", "Upgrade") };

        // ── Storage ──
        if (Has("reports '") || Has("end of its rated life"))
            return new() { FixButtons.Steps("Protect my files", FixButtons.FailingDrive(), true), FixButtons.Page("Replacement drives", "Upgrade") };
        if (Has("life left") || Has(" is hot ("))
            return new() { FixButtons.Page("Open Storage details", "Storage", true), FixButtons.Page("Replacement drives", "Upgrade"), FixButtons.Open("Resource Monitor", "resmon") };
        if (Has("hard disk drive")) return new() { FixButtons.Page("See SSD upgrade options", "Upgrade", true) };
        if ((Starts("Drive ") && (Has("almost full") || Has("getting full"))))
        {
            var fixes = new List<FixAction>();
            if (h is { TempBytes: > 50L << 20 }) fixes.Add(FixButtons.Do(FixIds.CleanTemp, $"Clean {Format.Bytes(h.TempBytes)} of temp files",
                "CoreScope will delete temporary files that have not been touched for 24 hours (your temp folder and Windows' temp folder). Files in use are skipped. Continue?"));
            if (h is { RecycleBinBytes: > 50L << 20 }) fixes.Add(FixButtons.Do(FixIds.EmptyRecycleBin, $"Empty Recycle Bin ({Format.Bytes(h.RecycleBinBytes)})",
                "This permanently deletes everything in the Recycle Bin. It cannot be undone. Empty it?", primary: false));
            fixes.Add(FixButtons.StorageSettings(fixes.Count == 0));
            fixes.Add(FixButtons.Open("Uninstall apps", "ms-settings:appsfeatures"));
            fixes.Add(FixButtons.DiskCleanup());
            return fixes;
        }
        if (Has("TRIM is disabled")) return new() { FixButtons.Do(FixIds.EnableTrim, "Turn TRIM on", admin: true) };

        // ── Graphics / firmware / security hardware ──
        if (Has("driver is") && spec.Gpus.Count > 0)
        {
            var gpu = spec.Gpus.FirstOrDefault(g => t.Contains(g.Name, StringComparison.OrdinalIgnoreCase)) ?? spec.Gpus[0];
            var vendor = (gpu.Vendor + " " + gpu.Name).ToUpperInvariant();
            var (label, url) = vendor.Contains("NVIDIA") ? ("NVIDIA drivers", "https://www.nvidia.com/Download/index.aspx")
                : vendor.Contains("AMD") || vendor.Contains("RADEON") ? ("AMD drivers", "https://www.amd.com/en/support")
                : vendor.Contains("INTEL") ? ("Intel drivers", "https://www.intel.com/content/www/us/en/download-center/home.html")
                : ("Your PC maker's drivers", support ?? "https://www.microsoft.com/windows");
            return new() { FixButtons.Web(label, url, true), FixButtons.OptionalUpdates(), FixButtons.DeviceManager() };
        }
        if (Starts("BIOS/UEFI is")) return new() { FixButtons.Steps("Show me how", FixButtons.UpdateBios(support, spec.Board.BiosVersion), true) };
        if (Starts("Secure Boot is off")) return new() { FixButtons.Steps("Show me how", FixButtons.EnableSecureBoot(support), true) };
        if (Starts("No TPM")) return new() { FixButtons.Steps("Show me how", FixButtons.EnableTpm(support, amd), true) };

        // ── Battery ──
        if (i.Category == "Battery")
            return new() { FixButtons.Page("Replacement battery", "Upgrade", true), FixButtons.ControlCenter("Battery care settings", false, "Battery care"), FixButtons.Open("Battery settings", "ms-settings:batterysaver") };

        // ── Devices ──
        if (i.Category == "Devices")
            return new() { FixButtons.DeviceManager(true), FixButtons.OptionalUpdates(), FixButtons.Do(FixIds.ScanHardware, "Scan for hardware changes", admin: true, primary: false), FixButtons.Page("Open Devices page", "Devices") };

        // ── Windows ──
        if (Starts("Up for ")) return new() { FixButtons.Restart(true), FixButtons.WindowsUpdate(false) };
        if (Starts("Power saver plan")) return new() { FixButtons.Do(FixIds.BalancedPower, "Switch to Balanced"), FixButtons.PowerSettings() };
        if (Has("no longer gets security updates") || Has("support ends in") || Has("no longer supported"))
            return new() { FixButtons.WindowsUpdate(), FixButtons.Web("Microsoft lifecycle page", "https://learn.microsoft.com/lifecycle/products/") };
        if (Has("update service is disabled")) return new() { FixButtons.Do(FixIds.StartWindowsUpdate, "Re-enable updates", admin: true), FixButtons.WindowsUpdate(false) };
        if (Has("No Windows update was installed")) return new() { FixButtons.WindowsUpdate(), FixButtons.Do(FixIds.StartWindowsUpdate, "Start the update service", admin: true, primary: false) };
        if (Has("waiting for a restart")) return new() { FixButtons.Restart(true), FixButtons.WindowsUpdate(false) };
        if (Has("System Restore is off"))
            return new() { FixButtons.Do(FixIds.EnableRestore, "Turn on System Restore", admin: true), FixButtons.Open("System protection settings", "SystemPropertiesProtection") };
        if (Has("restore point"))
            return new() { FixButtons.Do(FixIds.CreateRestorePoint, "Create a restore point now", "CoreScope will create a System Restore point now. It takes a moment. Continue?", admin: true), FixButtons.Open("System protection settings", "SystemPropertiesProtection") };
        if (Has("clock isn't syncing")) return new() { FixButtons.Do(FixIds.SyncClock, "Fix the clock", admin: true), FixButtons.Open("Date & time settings", "ms-settings:dateandtime") };

        // ── Security ──
        if (Has("No antivirus") || Has("Real-time protection is off")) return new() { FixButtons.WindowsSecurity("Open Windows Security", "windowsdefender://threatsettings") };
        if (Has("virus definitions"))
            return new() { FixButtons.Do(FixIds.UpdateDefender, "Update definitions now", admin: true), FixButtons.WindowsSecurity("Open Windows Security", "windowsdefender://threat", false) };
        if (Starts("Windows Firewall is off"))
            return new() { FixButtons.Do(FixIds.EnableFirewall, "Turn the firewall on", "CoreScope will turn Windows Firewall on for all network types. Continue?", admin: true),
                           FixButtons.WindowsSecurity("Firewall settings", "windowsdefender://network", false) };
        if (Has("User Account Control is off")) return new() { FixButtons.Open("Open UAC settings", "UserAccountControlSettings", true) };
        if (Has("drive isn't encrypted")) return new() { FixButtons.Open("Device encryption settings", "ms-settings:deviceencryption", true), FixButtons.WindowsSecurity("Device security", "windowsdefender://devicesecurity", false) };
        if (Has("Remote Desktop is turned on")) return new() { FixButtons.Open("Remote Desktop settings", "ms-settings:remotedesktop", true) };
        if (Has("signs in automatically")) return new() { FixButtons.Open("Open sign-in settings", "netplwiz", true), FixButtons.Open("Sign-in options", "ms-settings:signinoptions") };

        // ── Maintenance ──
        if (Has("temporary files"))
            return new() { FixButtons.Do(FixIds.CleanTemp, "Clean up now",
                               "CoreScope will delete temporary files that have not been touched for 24 hours (your temp folder and Windows' temp folder). Files in use are skipped. Continue?"),
                           FixButtons.DiskCleanup() };
        if (Has("Recycle Bin holds"))
            return new() { FixButtons.Do(FixIds.EmptyRecycleBin, "Empty Recycle Bin", "This permanently deletes everything in the Recycle Bin. It cannot be undone. Empty it?") };
        if (Has("hibernation file"))
            return new() { FixButtons.Do(FixIds.HibernateOff, "Turn hibernation off",
                               "This turns hibernation and Fast Startup off and deletes the hibernation file. You can turn it on again later with 'powercfg /hibernate on'. Continue?", admin: true),
                           FixButtons.PowerSettings() };
        if (Has("Storage Sense is off")) return new() { FixButtons.Do(FixIds.StorageSenseOn, "Turn on Storage Sense"), FixButtons.StorageSettings() };
        if (Has("no page file")) return new() { FixButtons.Open("Performance options", "SystemPropertiesPerformance", true) };

        // ── Startup ──
        if (Has("start with Windows"))
        {
            var fixes = new List<FixAction> { FixButtons.ControlCenter("Choose what starts", true, "Startup apps"), FixButtons.StartupSettings() };
            if (h is not null && h.StartupApps.Count > 0) fixes.Add(FixButtons.Open("Task Manager startup", "taskmgr"));
            return fixes;
        }

        // ── Display ──
        if (Has("Hz but supports")) return new() { FixButtons.Do(FixIds.MaxRefreshRate, "Use the highest refresh rate",
                                                     "The screen may go blank for a second while it switches. If it doesn't work, Windows switches back by itself. Continue?"),
                                                    FixButtons.Open("Display settings", "ms-settings:display-advanced", false) };

        return new();
    }
}
