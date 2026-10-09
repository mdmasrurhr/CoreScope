using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CoreScope.Core.Control;

public sealed record StartupApp(
    string Name,
    string Command,
    string Publisher,
    string Location,
    bool Enabled,
    RegistryHive ApprovedHive,
    string ApprovedKey);

/// <summary>
/// Desktop apps that launch at sign-in (Run keys + Startup folders). Enabling/disabling writes the same
/// "StartupApproved" flag Task Manager and Settings use, so nothing is deleted and it's fully reversible.
/// (Store apps' startup tasks are managed in Settings › Apps › Startup.)
/// </summary>
public static class StartupApps
{
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupApp> List()
    {
        var apps = new List<StartupApp>();
        ReadRun(apps, RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "Your account");
        ReadRun(apps, RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Run", "All users");
        ReadRun(apps, RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Run32", "All users (32-bit)");
        ReadFolder(apps, Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser, "Your Startup folder");
        ReadFolder(apps, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine, "All users' Startup folder");
        return apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ReadRun(List<StartupApp> apps, RegistryHive hive, RegistryView view, string path, string approvedKey, string location)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(path);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                if (name.Length == 0 || key.GetValue(name) is not string command || command.Length == 0) continue;
                apps.Add(new StartupApp(name, command, Publisher(command), location,
                    IsEnabled(hive, approvedKey, name), hive, approvedKey));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Error($"Reading {hive}\\{path}", ex);
        }
    }

    private static void ReadFolder(List<StartupApp> apps, string folder, RegistryHive hive, string location)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            foreach (var file in Directory.GetFiles(folder))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                apps.Add(new StartupApp(Path.GetFileNameWithoutExtension(file), file, Publisher(file), location,
                    IsEnabled(hive, "StartupFolder", fileName), hive, "StartupFolder"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Reading {folder}", ex);
        }
    }

    /// <summary>StartupApproved value: first byte even (0x02/0x06) = enabled, odd (0x03/0x07) = disabled; missing = enabled.</summary>
    private static bool IsEnabled(RegistryHive hive, string approvedKey, string valueName)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(Approved + approvedKey);
            return key?.GetValue(valueName) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return true; }
    }

    public static bool SetEnabled(StartupApp app, bool enable)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(app.ApprovedHive, RegistryView.Registry64).CreateSubKey(Approved + app.ApprovedKey, writable: true);
            var valueName = app.ApprovedKey == "StartupFolder" ? Path.GetFileName(app.Command) : app.Name;
            var data = new byte[12];
            data[0] = enable ? (byte)0x02 : (byte)0x03;
            if (!enable) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            key.SetValue(valueName, data, RegistryValueKind.Binary);
            Log.Info($"Control: startup app '{app.Name}' → {(enable ? "enabled" : "disabled")}");
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Error($"Toggling startup app {app.Name}", ex);
            return false;
        }
    }

    private static string Publisher(string command)
    {
        var path = ExecutablePath(command);
        if (path is null) return "";
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.CompanyName?.Trim() ?? "";
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException) { return ""; }
    }

    /// <summary>Extracts the executable from a Run command such as "\"C:\\x\\app.exe\" --minimized".</summary>
    public static string? ExecutablePath(string command)
    {
        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        var quoted = Regex.Match(expanded, "^\"([^\"]+)\"");
        var candidate = quoted.Success ? quoted.Groups[1].Value : Regex.Match(expanded, @"^(.+?\.(exe|lnk|bat|cmd))", RegexOptions.IgnoreCase).Groups[1].Value;
        return candidate.Length > 0 && File.Exists(candidate) ? candidate : null;
    }
}
