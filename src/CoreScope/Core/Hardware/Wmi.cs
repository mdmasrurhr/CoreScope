using System;
using System.Collections.Generic;
using System.Management;

namespace CoreScope.Core.Hardware;

/// <summary>Defensive WMI helpers: a failing class or property yields empty values, never an exception.</summary>
public static class Wmi
{
    public const string Cimv2 = @"root\cimv2";
    public const string RootWmi = @"root\wmi";
    public const string Storage = @"root\Microsoft\Windows\Storage";
    public const string Tpm = @"root\CIMV2\Security\MicrosoftTpm";

    public static List<ManagementBaseObject> Query(string wql, string scope = Cimv2)
    {
        var results = new List<ManagementBaseObject>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            searcher.Options.Timeout = TimeSpan.FromSeconds(10);
            foreach (var obj in searcher.Get()) results.Add(obj);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Log.Error($"WMI '{wql}' in {scope}", ex);
        }
        return results;
    }

    public static ManagementBaseObject? First(string wql, string scope = Cimv2)
    {
        var all = Query(wql, scope);
        return all.Count > 0 ? all[0] : null;
    }

    private static object? Raw(ManagementBaseObject? obj, string property)
    {
        if (obj is null) return null;
        try { return obj[property]; }
        catch (ManagementException) { return null; }
    }

    public static string Str(this ManagementBaseObject? obj, string property) =>
        Raw(obj, property)?.ToString()?.Trim() ?? "";

    public static ulong ULong(this ManagementBaseObject? obj, string property)
    {
        var value = Raw(obj, property);
        if (value is null) return 0;
        try { return Convert.ToUInt64(value); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return 0; }
    }

    public static int Int(this ManagementBaseObject? obj, string property)
    {
        var value = obj.ULong(property);
        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    public static bool? Bool(this ManagementBaseObject? obj, string property) =>
        Raw(obj, property) is bool b ? b : null;

    public static DateTime? Date(this ManagementBaseObject? obj, string property)
    {
        var text = obj.Str(property);
        if (text.Length == 0) return null;
        try { return ManagementDateTimeConverter.ToDateTime(text); }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or FormatException or ArgumentException) { return null; }
    }

    /// <summary>Decodes WMI arrays of UTF-16 code units (e.g. WmiMonitorID.UserFriendlyName).</summary>
    public static string CharArray(this ManagementBaseObject? obj, string property)
    {
        if (Raw(obj, property) is not Array array) return "";
        var chars = new List<char>();
        foreach (var item in array)
        {
            var code = Convert.ToInt32(item);
            if (code == 0) break;
            chars.Add((char)code);
        }
        return new string(chars.ToArray()).Trim();
    }
}
