using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreScope.Core.Hardware;

/// <summary>JEDEC timings read from a module's SPD EEPROM, already converted from nanoseconds to clock cycles.</summary>
public sealed record SpdModuleTimings(
    int SmBusAddress,
    string Type,
    string PartNumber,
    string ModuleMaker,
    string DramMaker,
    DateTime? Manufactured,
    int JedecMts,
    int Cl,
    int Trcd,
    int Trp,
    int Tras,
    int Trc,
    IReadOnlyList<int> SupportedCl,
    double? TemperatureC)
{
    /// <summary>Secondary timings in clock cycles (0 = not provided by this SPD).</summary>
    public int Trfc { get; init; }
    public int Twr { get; init; }
    public int Tfaw { get; init; }
    public int TrrdS { get; init; }
    public int TrrdL { get; init; }
    public int TccdL { get; init; }
    public double TckNs { get; init; }
    public double CapacityGb { get; init; }
    public string Serial { get; init; } = "";

    public string Primary => $"{Cl}-{Trcd}-{Trp}-{Tras}";

    /// <summary>True first-word latency in ns = CL × 2000 / MT/s.</summary>
    public double FirstWordNs => JedecMts > 0 ? Cl * 2000.0 / JedecMts : 0;
}

/// <summary>
/// Memory module helpers. CoreScope ships no kernel driver, so SPD chips (on the SMBus) aren't read; the rated
/// CAS latency is decoded from the module's part number instead. <see cref="SpdModuleTimings"/> stays so a future
/// driver-free source (e.g. vendor WMI) can fill the same timings table.
/// </summary>
public static class SpdReader
{
    /// <summary>
    /// XMP/EXPO kits usually print their rated CAS latency in the part number
    /// (F4-3600C16, CMK32GX4M2E3200C16, KF436C17, BL8G32C16U4B). Returns null when it can't tell.
    /// </summary>
    public static int? ClFromPartNumber(string part)
    {
        if (string.IsNullOrWhiteSpace(part)) return null;
        var p = part.Trim().ToUpperInvariant();
        var m = System.Text.RegularExpressions.Regex.Match(p, @"(?:\d{4}|^KF\d{3}|^BL\d+G\d{2})C(\d{2})(?!\d)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var cl) && cl is >= 9 and <= 60 ? cl : null;
    }
}
