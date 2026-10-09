using System;
using System.Globalization;

namespace CoreScope.Core;

public static class Format
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Binary-unit size, labelled the way Windows does (1 GB = 1024³ bytes).</summary>
    public static string Bytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        string number = bytes >= 100 || unit == 0 ? bytes.ToString("0", Inv) : bytes.ToString("0.#", Inv);
        return $"{number} {units[unit]}";
    }

    /// <summary>Honours the °C / °F preference.</summary>
    public static string Temperature(double celsius, int decimals = 0)
    {
        var fmt = decimals == 0 ? "0" : "0.0";
        return AppSettings.Current.UseFahrenheit
            ? $"{(celsius * 9 / 5 + 32).ToString(fmt, Inv)} °F"
            : $"{celsius.ToString(fmt, Inv)} °C";
    }

    public static string Kb(long kb) => kb >= 1024 && kb % 1024 == 0 ? $"{kb / 1024} MB" : $"{kb} KB";

    public static string Mhz(double mhz) =>
        mhz >= 1000 ? $"{(mhz / 1000).ToString("0.00", Inv)} GHz" : $"{mhz.ToString("0", Inv)} MHz";

    /// <summary>Strips trademark noise: "(R)", "(TM)", "®", "™" and the "RadeonT" mojibake some drivers report.</summary>
    public static string CleanName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        var s = System.Text.RegularExpressions.Regex.Replace(name, @"\((R|TM|C)\)|®|™", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\bRadeonT\b", "Radeon");
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s{2,}", " ").Trim();
    }

    public static string Date(DateTime? date) => date is { } d ? d.ToString("MMM d, yyyy", Inv) : "";

    public static string Age(DateTime? date)
    {
        if (date is not { } d) return "";
        var days = (DateTime.Now - d).TotalDays;
        if (days < 1) return "today";
        if (days < 45) return $"{days:0} days ago";
        if (days < 365 * 1.5) return $"{days / 30.4:0} months ago";
        return $"{(days / 365.25).ToString("0.#", Inv)} years ago";
    }

    public static string Duration(TimeSpan span)
    {
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m";
        if (span.TotalHours >= 1) return $"{span.Hours}h {span.Minutes}m";
        return $"{span.Minutes}m";
    }

    public static string YesNo(bool? value, string yes = "Yes", string no = "No") =>
        value is null ? "Unknown" : value.Value ? yes : no;

    /// <summary>Formats a LibreHardwareMonitor sensor value using its sensor type name.</summary>
    public static string Sensor(string type, double? value)
    {
        if (value is not { } v || double.IsNaN(v)) return "—";
        return type switch
        {
            "Temperature" => Temperature(v, decimals: 1),
            "Load" or "Control" or "Level" or "Humidity" => $"{v.ToString("0.0", Inv)} %",
            "Clock" => $"{v.ToString("0", Inv)} MHz",
            "Frequency" => $"{v.ToString("0.0", Inv)} Hz",
            "Power" => $"{v.ToString(v < 10 ? "0.00" : "0.0", Inv)} W",
            "Voltage" => $"{v.ToString("0.000", Inv)} V",
            "Current" => $"{v.ToString("0.00", Inv)} A",
            "Fan" => $"{v.ToString("0", Inv)} RPM",
            "Flow" => $"{v.ToString("0.0", Inv)} L/h",
            "Data" => $"{v.ToString("0.0", Inv)} GB",
            "SmallData" => $"{v.ToString("0", Inv)} MB",
            "Throughput" => $"{Bytes(v)}/s",
            "Energy" => $"{v.ToString("0", Inv)} mWh",
            "TimeSpan" => Duration(TimeSpan.FromSeconds(v)),
            "Factor" => v.ToString("0.000", Inv),
            "Noise" => $"{v.ToString("0", Inv)} dBA",
            _ => v.ToString("0.##", Inv),
        };
    }
}
