using System;
using System.Diagnostics;
using System.IO;

namespace CoreScope.Core;

/// <summary>UI-platform services the view models need (implemented with WPF / WinForms in the app project).</summary>
public interface IAppServices
{
    /// <summary>Shows a Save dialog; returns the chosen path or null if cancelled.</summary>
    string? SaveFile(string defaultName, string filter, string content);
    void CopyText(string text);
    void Notify(string title, string message);
    void SetOverlayVisible(bool visible);
    void RefreshOverlaySettings();
    /// <summary>Asks a yes/no question with a named "yes" button; true when the user agrees.</summary>
    bool Confirm(string title, string message, string yes);
    /// <summary>Shows a step-by-step walkthrough; its buttons call <paramref name="run"/> and show the returned message.</summary>
    void ShowGuide(Fixes.Guide guide, Func<Fixes.FixAction, System.Threading.Tasks.Task<Fixes.FixResult>> run);
}

/// <summary>
/// "Start with Windows". Store (MSIX) installs use the package's StartupTask (hooks set by the app). Otherwise:
/// when running elevated, a Task Scheduler logon task with highest privileges (starts with full sensors, no UAC
/// prompt); when not elevated, the per-user Run key (starts in limited mode).
/// </summary>
public static class StartupRegistration
{
    private const string TaskName = "CoreScope";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Set by the app when packaged: query / enable-disable the MSIX StartupTask.</summary>
    public static Func<bool>? PackagedQuery;
    public static Func<bool, bool>? PackagedSet;

    private static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CoreScope.exe");

    public static bool IsRegistered()
    {
        if (PackagedQuery is not null) return PackagedQuery();
        if (Run($"/Query /TN \"{TaskName}\"") == 0) return true;
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(TaskName) is string;
    }

    public static bool Set(bool enable)
    {
        if (PackagedSet is not null) return PackagedSet(enable);
        bool ok = true;
        if (enable && Hardware.Native.IsAdministrator())
        {
            ok = Run($"/Create /TN \"{TaskName}\" /TR \"\\\"{Exe}\\\" --minimized\" /SC ONLOGON /RL HIGHEST /F") == 0;
            SetRunKey(false);
        }
        else if (enable)
        {
            ok = SetRunKey(true);
        }
        else
        {
            if (Run($"/Query /TN \"{TaskName}\"") == 0) ok = Run($"/Delete /TN \"{TaskName}\" /F") == 0; // may need elevation
            ok &= SetRunKey(false);
        }
        Log.Info($"Start with Windows → {enable}: {(ok ? "ok" : "failed")}");
        return ok;
    }

    private static bool SetRunKey(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable) key.SetValue(TaskName, $"\"{Exe}\" --minimized");
            else if (key.GetValue(TaskName) is not null) key.DeleteValue(TaskName);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Error("Run key", ex);
            return false;
        }
    }

    private static int Run(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p is null) return -1;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return p.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("schtasks", ex);
            return -1;
        }
    }
}
