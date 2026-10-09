using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using CoreScope.Core.Control;
using CoreScope.Core.Hardware;
using CoreScope.Core.Network;

namespace CoreScope.Core.Fixes;

/// <summary>Runs a command-line tool. Replaceable so the fixes can be tested without touching the machine.</summary>
public interface IToolRunner
{
    (int ExitCode, string Output) Run(string file, string arguments, int timeoutMs = 20000);
}

public sealed class ToolRunner : IToolRunner
{
    public (int ExitCode, string Output) Run(string file, string arguments, int timeoutMs = 20000)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            });
            if (process is null) return (-1, "Windows couldn't start the tool.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs)) { try { process.Kill(true); } catch (InvalidOperationException) { } return (-2, "The tool took too long and was stopped."); }
            return (process.ExitCode, (output.Result + error.Result).Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Running {file}", ex);
            return (-1, "Windows couldn't run the tool.");
        }
    }
}

/// <summary>
/// Carries out the buttons on findings. Opening a settings page, a web page or a CoreScope page is immediate; anything that
/// changes or deletes something first asks the user to confirm, and anything needing administrator access offers to unlock it.
/// </summary>
public static class FixRunner
{
    public static IToolRunner Tools { get; set; } = new ToolRunner();

    private static readonly Guid BalancedPlan = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    public static async Task<FixResult> RunAsync(FixAction action, IFixHost host)
    {
        switch (action.Kind)
        {
            case FixKind.Settings:
                Shell.Open(action.Target);
                return FixResult.Success("Opened.");
            case FixKind.Url:
                Shell.Open(action.Target);
                return FixResult.Success("Opened in your browser.");
            case FixKind.Page:
                return host.Navigate(action.Target) ? FixResult.Success("") : FixResult.Failure("That page isn't available.");
            case FixKind.Guide:
                if (action.Guide is null) return FixResult.Failure("No steps available.");
                host.ShowGuide(action.Guide);
                return FixResult.Success("");
        }

        if (action.Id == FixIds.Unlock) { host.UnlockFullAccess(); return FixResult.Success("Restarting with full access…"); }

        if (action.NeedsAdmin && !Native.IsAdministrator())
        {
            if (host.Confirm("This needs full access",
                    "CoreScope has to restart with administrator rights to do this (Windows asks you once). After it reopens, press the button again. Restart CoreScope now?", "Unlock now"))
                host.UnlockFullAccess();
            return FixResult.Failure("This fix needs full access. Use \"Unlock full access\", then try again.");
        }
        if (FixPreviews.Question(action) is { } question && !host.Confirm(action.Label, question, action.Label, FixPreviews.For(action))) return FixResult.Cancelled;

        try
        {
            return await Task.Run(() => Execute(action)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Error($"Fix {action.Id}", ex);
            return FixResult.Failure("That didn't work: " + ex.Message);
        }
    }

    private static FixResult Execute(FixAction action)
    {
        Log.Info($"Fix: {action.Id} ({action.Label})");
        var id = action.Id;
        if (id.StartsWith("net.", StringComparison.Ordinal))
        {
            var (ok, message) = NetworkFixes.Apply(id);
            return new FixResult(ok, message);
        }

        return id switch
        {
            FixIds.BalancedPower => PowerControl.SetActivePlan(BalancedPlan)
                ? FixResult.Success("Power plan is now Balanced.") : FixResult.Failure("Windows wouldn't switch the power plan."),
            FixIds.Restart => Tool("shutdown", "/r /t 15 /c \"CoreScope: restarting as you asked\"", "Restarting in 15 seconds. To cancel, run: shutdown /a"),
            FixIds.RestartToUefi => Tool("shutdown", "/r /fw /t 15 /c \"CoreScope: restarting into BIOS / UEFI setup\"", "Restarting into BIOS / UEFI setup in 15 seconds. To cancel, run: shutdown /a"),
            FixIds.CleanTemp => CleanTemp(),
            FixIds.EmptyRecycleBin => EmptyRecycleBin(),
            FixIds.EnableTrim => Tool("fsutil", "behavior set DisableDeleteNotify 0", "TRIM is on. Your SSD can now clean up free blocks."),
            FixIds.HibernateOff => Tool("powercfg", "/hibernate off", "Hibernation is off and its file was removed."),
            FixIds.SyncClock => SyncClock(),
            FixIds.EnableFirewall => Tool("netsh", "advfirewall set allprofiles state on", "Windows Firewall is on for every network type."),
            FixIds.UpdateDefender => Tool(DefenderTool(), "-SignatureUpdate", "Virus definitions are up to date.", 120000),
            FixIds.StartWindowsUpdate => StartWindowsUpdate(),
            FixIds.EnableRestore => Tool("powershell", "-NoProfile -Command \"Enable-ComputerRestore -Drive ($env:SystemDrive + '\\')\"", "System Restore is on for your system drive.", 60000),
            FixIds.CreateRestorePoint => Tool("powershell", "-NoProfile -Command \"Checkpoint-Computer -Description 'CoreScope restore point' -RestorePointType MODIFY_SETTINGS\"",
                                              "Restore point created. (Windows normally allows one every 24 hours.)", 120000),
            FixIds.MaxRefreshRate => MaxRefreshRate(),
            FixIds.ScanHardware => Tool("pnputil", "/scan-devices", "Windows rescanned your hardware. Check the Devices page in a moment."),
            FixIds.DisableStartupApp => DisableStartupApp(action.Target),
            FixIds.StorageSenseOn => StorageSenseOn(),
            _ => FixResult.Failure("CoreScope doesn't know that fix."),
        };
    }

    private static FixResult Tool(string file, string args, string success, int timeoutMs = 20000)
    {
        var (code, output) = Tools.Run(file, args, timeoutMs);
        Log.Info($"  {file} {args} → {code} {output}");
        return code == 0 ? FixResult.Success(success) : FixResult.Failure(Describe(code, output));
    }

    private static string Describe(int code, string output)
    {
        if (code == -2) return output;
        var text = string.IsNullOrWhiteSpace(output) ? "" : " " + output.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length > 240) text = text[..240] + "…";
        return $"Windows reported a problem (code {code}).{text}";
    }

    private static string DefenderTool()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        return File.Exists(path) ? path : "MpCmdRun.exe";
    }

    private static FixResult SyncClock()
    {
        Tools.Run("sc", "config w32time start= auto");
        Tools.Run("net", "start w32time");
        return Tool("w32tm", "/resync /force", "Your clock was synchronised with the internet time server.");
    }

    private static FixResult StartWindowsUpdate()
    {
        Tools.Run("sc", "config wuauserv start= demand");
        return Tool("net", "start wuauserv", "The Windows Update service is running. Open Windows Update and check for updates.");
    }

    private static FixResult MaxRefreshRate()
    {
        var display = DisplayControl.Displays().FirstOrDefault(d => d.IsPrimary) ?? DisplayControl.Displays().FirstOrDefault();
        if (display is null) return FixResult.Failure("Windows didn't report a display.");
        var best = display.Modes.Where(m => m.Width == display.Current.Width && m.Height == display.Current.Height).MaxBy(m => m.RefreshHz);
        if (best is null || best.RefreshHz <= display.Current.RefreshHz) return FixResult.Failure("This display is already at its highest refresh rate.");
        return DisplayControl.SetMode(display.DeviceName, best)
            ? FixResult.Success($"Refresh rate is now {best.RefreshHz} Hz.")
            : FixResult.Failure("Windows (or the display) refused that refresh rate. Try it in Display settings.");
    }

    private static FixResult DisableStartupApp(string name)
    {
        var app = StartupApps.List().FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (app is null) return FixResult.Failure($"{name} isn't in the startup list any more.");
        return StartupApps.SetEnabled(app, false)
            ? FixResult.Success($"{app.Name} won't start with Windows any more. You can turn it back on in Control Center.")
            : FixResult.Failure($"Couldn't change {app.Name}. Try Windows startup settings, or unlock full access.");
    }

    private static FixResult StorageSenseOn()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy");
            key.SetValue("01", 1, Microsoft.Win32.RegistryValueKind.DWord);
            return FixResult.Success("Storage Sense is on. Windows will tidy temporary files for you when space runs low.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return FixResult.Failure("Windows wouldn't let CoreScope change that. Open Storage settings instead.");
        }
    }

    // ───────────── Temporary files ─────────────

    private static FixResult CleanTemp()
    {
        var (bytes, files, skipped) = TempFiles.Clean();
        if (files == 0 && skipped == 0) return FixResult.Success("There were no old temporary files to remove.");
        var extra = skipped > 0 ? $" ({skipped} file{(skipped == 1 ? " was" : "s were")} in use and kept.)" : "";
        return FixResult.Success($"Removed {files} temporary file{(files == 1 ? "" : "s")} and freed {Format.Bytes(bytes)}.{extra}");
    }

    // ───────────── Recycle Bin ─────────────

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ShQueryRbInfo { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBin(string? root, ref ShQueryRbInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    public static (long Bytes, long Items) RecycleBinSize()
    {
        try
        {
            var info = new ShQueryRbInfo { cbSize = Marshal.SizeOf<ShQueryRbInfo>() };
            return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return (0, 0); }
    }

    private static FixResult EmptyRecycleBin()
    {
        var (bytes, items) = RecycleBinSize();
        const uint noConfirm = 0x1, noProgress = 0x2, noSound = 0x4;
        var result = SHEmptyRecycleBin(IntPtr.Zero, null, noConfirm | noProgress | noSound);
        // 0 = ok; the "bin already empty" HRESULT is also fine.
        return result == 0 || result == unchecked((int)0x8000FFFF) || items == 0
            ? FixResult.Success($"Recycle Bin emptied ({Format.Bytes(bytes)} freed).")
            : FixResult.Failure("Windows couldn't empty the Recycle Bin.");
    }
}

/// <summary>The user's and Windows' temporary folders: measure what is safe to remove, and remove it.</summary>
public static class TempFiles
{
    /// <summary>Only files untouched for this long are removed; anything newer may still be in use.</summary>
    public static readonly TimeSpan MinAge = TimeSpan.FromHours(24);

    private static string[] Folders() => new[]
    {
        Path.GetTempPath(),
        Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "Temp"),
    }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Size of what <see cref="Clean"/> would remove. Gives up after <paramref name="budget"/> and reports a lower bound.</summary>
    public static (long Bytes, bool Complete) Measure(TimeSpan? budget = null)
    {
        var deadline = DateTime.UtcNow + (budget ?? TimeSpan.FromSeconds(3));
        long total = 0;
        foreach (var folder in Folders())
        {
            foreach (var file in Enumerate(folder))
            {
                if (DateTime.UtcNow > deadline) return (total, false);
                try
                {
                    var info = new FileInfo(file);
                    if (DateTime.UtcNow - info.LastWriteTimeUtc > MinAge) total += info.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* skip */ }
            }
        }
        return (total, true);
    }

    /// <summary>Deletes old files, then empty folders. Returns bytes freed, files removed and files that were in use.</summary>
    public static (long Bytes, int Files, int Skipped) Clean()
    {
        long bytes = 0;
        int files = 0, skipped = 0;
        foreach (var folder in Folders())
        {
            foreach (var file in Enumerate(folder).ToList())
            {
                try
                {
                    var info = new FileInfo(file);
                    if (DateTime.UtcNow - info.LastWriteTimeUtc <= MinAge) continue;
                    var length = info.Length;
                    info.Delete();
                    bytes += length;
                    files++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
            }
            RemoveEmptyDirectories(folder);
        }
        Log.Info($"Temp clean: {files} files, {bytes} bytes freed, {skipped} skipped");
        return (bytes, files, skipped);
    }

    private static System.Collections.Generic.IEnumerable<string> Enumerate(string folder)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        try { return Directory.EnumerateFiles(folder, "*", options); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static void RemoveEmptyDirectories(string root)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                         .OrderByDescending(d => d.Length))
            {
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* skip */ }
    }
}
