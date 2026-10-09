using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using CoreScope.Core.Control;
using CoreScope.Core.Fixes;
using Microsoft.Win32;

namespace CoreScope.Core.Hardware;

/// <summary>
/// How healthy the PC is beyond its hardware: security, updates, maintenance, startup load, display and a few Windows settings.
/// Every field is null when it could not be read, and the checks then stay silent instead of guessing.
/// </summary>
public sealed class HealthSnapshot
{
    public DateTime Taken = DateTime.Now;

    // Security
    public bool? AntivirusActive;
    public string? AntivirusName;
    public bool? RealTimeProtection;
    public int? SignatureAgeDays;
    public bool? FirewallDomain, FirewallPrivate, FirewallPublic;
    public bool? UacEnabled;
    public bool? SystemDriveEncrypted;       // only readable with administrator access
    public bool? RemoteDesktopOn;
    public bool? AutoSignIn;

    // Updates and recovery
    public bool? PendingReboot;
    public int? DaysSinceUpdate;
    public bool? UpdateServiceDisabled;
    public bool? SystemRestoreOn;
    public int? DaysSinceRestorePoint;       // null = unknown, int.MaxValue = none
    public bool? ClockServiceRunning;

    // Maintenance
    public long TempBytes;
    public bool TempMeasureComplete = true;
    public long RecycleBinBytes;
    public long HibernationFileBytes;
    public bool? StorageSenseOn;
    public bool? PageFileMissing;

    // Startup load
    public List<string> StartupApps = new();

    // Display
    public int? RefreshHz;
    public int? MaxRefreshHz;
}

public static class HealthCollector
{
    /// <summary>Reads everything in parallel with an overall time limit. Slow or failing parts are left as unknown.</summary>
    public static HealthSnapshot Collect()
    {
        var h = new HealthSnapshot();
        var tasks = new[]
        {
            Task.Run(() => Guard("antivirus", () => Antivirus(h))),
            Task.Run(() => Guard("firewall", () => Firewall(h))),
            Task.Run(() => Guard("windows settings", () => WindowsSettings(h))),
            Task.Run(() => Guard("updates", () => Updates(h))),
            Task.Run(() => Guard("restore", () => Restore(h))),
            Task.Run(() => Guard("encryption", () => Encryption(h))),
            Task.Run(() => Guard("services", () => Services(h))),
            Task.Run(() => Guard("files", () => Files(h))),
            Task.Run(() => Guard("startup", () => Startup(h))),
            Task.Run(() => Guard("display", () => Display(h))),
        };
        Task.WaitAll(tasks, TimeSpan.FromSeconds(12));
        h.Taken = DateTime.Now;
        return h;
    }

    private static void Guard(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Security.SecurityException or IOException
                                      or InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            Log.Error($"Health check: {what}", ex);
        }
    }

    // ───────────── Security ─────────────

    private static void Antivirus(HealthSnapshot h)
    {
        // Windows Defender, when it is the active product.
        try
        {
            using var search = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender", "SELECT AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureAge FROM MSFT_MpComputerStatus");
            foreach (ManagementBaseObject o in search.Get())
            {
                h.RealTimeProtection = o["RealTimeProtectionEnabled"] as bool?;
                h.AntivirusActive = o["AntivirusEnabled"] as bool?;
                if (o["AntivirusSignatureAge"] is { } age) h.SignatureAgeDays = Convert.ToInt32(age);
                h.AntivirusName = "Microsoft Defender";
            }
        }
        catch (ManagementException) { /* Defender isn't the active product: look at Security Center */ }

        // Whatever product Windows Security reports (covers third-party antivirus too).
        using var center = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntivirusProduct");
        var products = new List<(string Name, bool On, bool Current)>();
        foreach (ManagementBaseObject o in center.Get())
        {
            var state = Convert.ToInt64(o["productState"] ?? 0);
            products.Add((o["displayName"] as string ?? "Antivirus", (state & 0x1000) != 0, (state & 0x10) == 0));
        }
        if (products.Count == 0) { h.AntivirusActive ??= false; return; }
        var active = products.FirstOrDefault(p => p.On);
        if (active.Name is not null && active.On)
        {
            h.AntivirusActive = true;
            h.AntivirusName ??= active.Name;
            if (active.Name != "Windows Defender" && h.AntivirusName == active.Name) h.RealTimeProtection ??= true;
            if (!active.Current && h.SignatureAgeDays is null) h.SignatureAgeDays = 7;   // product says out of date; exact age unknown
        }
        else
        {
            h.AntivirusActive = false;
            h.AntivirusName ??= products[0].Name;
        }
    }

    private static void Firewall(HealthSnapshot h)
    {
        const string root = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\";
        h.FirewallDomain = ReadDword(Registry.LocalMachine, root + "DomainProfile", "EnableFirewall") is { } d ? d != 0 : null;
        h.FirewallPrivate = ReadDword(Registry.LocalMachine, root + "StandardProfile", "EnableFirewall") is { } p ? p != 0 : null;
        h.FirewallPublic = ReadDword(Registry.LocalMachine, root + "PublicProfile", "EnableFirewall") is { } u ? u != 0 : null;
    }

    private static void WindowsSettings(HealthSnapshot h)
    {
        h.UacEnabled = ReadDword(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA") is { } uac ? uac != 0 : null;
        h.RemoteDesktopOn = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections") is { } rdp ? rdp == 0 : null;
        using (var logon = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"))
            h.AutoSignIn = logon?.GetValue("AutoAdminLogon") is string auto ? auto == "1" : null;
        h.StorageSenseOn = ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01") is { } ss ? ss != 0 : null;
        h.UpdateServiceDisabled = ReadDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\wuauserv", "Start") is { } start ? start == 4 : null;
    }

    private static void Encryption(HealthSnapshot h)
    {
        if (!Native.IsAdministrator()) return;
        var drive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        using var search = new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftVolumeEncryption", $"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter='{drive}'");
        foreach (ManagementBaseObject o in search.Get()) h.SystemDriveEncrypted = Convert.ToInt32(o["ProtectionStatus"]) == 1;
    }

    // ───────────── Updates and recovery ─────────────

    private static void Updates(HealthSnapshot h)
    {
        h.PendingReboot =
            KeyExists(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")
            || KeyExists(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")
            || HasPendingRenames();

        DateTime? last = null;
        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Install"))
            if (key?.GetValue("LastSuccessTime") is string text && DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var utc))
                last = utc.ToLocalTime();
        // The registry value is often missing on current builds, so also look at the installed-update list; use the newest.
        using (var search = new ManagementObjectSearcher("SELECT InstalledOn FROM Win32_QuickFixEngineering"))
            foreach (ManagementBaseObject o in search.Get())
                if (o["InstalledOn"] is string s && DateTime.TryParse(s, out var when) && (last is null || when > last)) last = when;
        if (last is { } date) h.DaysSinceUpdate = Math.Max(0, (int)(DateTime.Now - date).TotalDays);
    }

    private static bool HasPendingRenames()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
        return key?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 } list && list.Any(s => !string.IsNullOrWhiteSpace(s));
    }

    private static void Restore(HealthSnapshot h)
    {
        var disabledByPolicy = ReadDword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore", "DisableSR") == 1;
        var interval = ReadDword(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "RPSessionInterval");
        if (disabledByPolicy) h.SystemRestoreOn = false;
        else if (interval is { } i) h.SystemRestoreOn = i != 0;
        if (h.SystemRestoreOn != true || !Native.IsAdministrator()) return;

        using var search = new ManagementObjectSearcher(@"root\default", "SELECT CreationTime FROM SystemRestore");
        DateTime? newest = null;
        foreach (ManagementBaseObject o in search.Get())
            if (o["CreationTime"] is string stamp && ManagementDateTimeConverter.ToDateTime(stamp) is var when && (newest is null || when > newest)) newest = when;
        h.DaysSinceRestorePoint = newest is { } n ? (int)(DateTime.Now - n).TotalDays : int.MaxValue;
    }

    private static void Services(HealthSnapshot h)
    {
        using var search = new ManagementObjectSearcher("SELECT State FROM Win32_Service WHERE Name='w32time'");
        foreach (ManagementBaseObject o in search.Get()) h.ClockServiceRunning = string.Equals(o["State"] as string, "Running", StringComparison.OrdinalIgnoreCase);

        using var computer = new ManagementObjectSearcher("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem");
        foreach (ManagementBaseObject o in computer.Get())
        {
            if (o["AutomaticManagedPagefile"] is true) { h.PageFileMissing = false; continue; }
            using var pf = new ManagementObjectSearcher("SELECT Name FROM Win32_PageFileSetting");
            h.PageFileMissing = pf.Get().Count == 0;
        }
    }

    // ───────────── Files ─────────────

    private static void Files(HealthSnapshot h)
    {
        var (bytes, complete) = TempFiles.Measure(TimeSpan.FromSeconds(4));
        h.TempBytes = bytes;
        h.TempMeasureComplete = complete;
        h.RecycleBinBytes = FixRunner.RecycleBinSize().Bytes;
        var hiberfil = new FileInfo(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "hiberfil.sys"));
        h.HibernationFileBytes = hiberfil.Exists ? hiberfil.Length : 0;
    }

    private static void Startup(HealthSnapshot h) =>
        h.StartupApps = StartupApps.List().Where(a => a.Enabled).Select(a => a.Name).ToList();

    private static void Display(HealthSnapshot h)
    {
        var display = DisplayControl.Displays().FirstOrDefault(d => d.IsPrimary) ?? DisplayControl.Displays().FirstOrDefault();
        if (display is null) return;
        h.RefreshHz = display.Current.RefreshHz;
        h.MaxRefreshHz = display.Modes.Where(m => m.Width == display.Current.Width && m.Height == display.Current.Height).Select(m => m.RefreshHz).DefaultIfEmpty(display.Current.RefreshHz).Max();
    }

    // ───────────── Registry helpers ─────────────

    private static int? ReadDword(RegistryKey hive, string path, string name)
    {
        using var key = hive.OpenSubKey(path);
        return key?.GetValue(name) is int value ? value : null;
    }

    private static bool KeyExists(RegistryKey hive, string path)
    {
        using var key = hive.OpenSubKey(path);
        return key is not null;
    }
}
