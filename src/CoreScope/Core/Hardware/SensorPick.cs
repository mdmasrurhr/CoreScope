using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreScope.Core.Hardware;

/// <summary>
/// Picks the headline sensors (CPU temp, GPU load, …) out of a snapshot. Names differ by vendor and
/// LHM version, so every pick is a priority list with a sensible fallback.
/// </summary>
public static class SensorPick
{
    private static bool IsCpu(SensorReading r) => r.Kind == "Cpu";
    private static bool IsGpu(SensorReading r) => r.Kind.StartsWith("Gpu", StringComparison.Ordinal);

    private static double? ByName(IEnumerable<SensorReading> source, params string[] names)
    {
        var list = source as IList<SensorReading> ?? source.ToList();
        foreach (var name in names)
        {
            var hit = list.FirstOrDefault(r => r.Value.HasValue && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit.Value;
        }
        return null;
    }

    public static double? CpuLoad(IReadOnlyList<SensorReading> s) =>
        ByName(s.Where(r => IsCpu(r) && r.Type == "Load"), "CPU Total");

    public static double? CpuTemperature(IReadOnlyList<SensorReading> s)
    {
        var temps = s.Where(r => IsCpu(r) && r.Type == "Temperature" && r.Value is > 0 and < 150).ToList();
        return ByName(temps, "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Core (Tdie)", "Tctl/Tdie", "Package", "Core Max", "Core Average")
               ?? (temps.Count > 0 ? temps.Max(r => r.Value) : null);
    }

    /// <summary>Highest believable core clock; some mobile chips report garbage (e.g. 15,969 MHz) on raw clocks.</summary>
    private const double MaxPlausibleClockMhz = 7000;

    /// <summary>
    /// Average core clock. Prefers AMD's "effective" clocks (what the cores actually ran at), then the
    /// plain per-core clocks, discarding implausible values.
    /// </summary>
    public static double? CpuClock(IReadOnlyList<SensorReading> s)
    {
        var clocks = s.Where(r => IsCpu(r) && r.Type == "Clock" && r.Value is > 0 and < MaxPlausibleClockMhz
                                  && r.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)
                                  && !r.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase))
                      .ToList();

        var averageEffective = clocks.FirstOrDefault(r => r.Name.Equals("Cores (Average Effective)", StringComparison.OrdinalIgnoreCase));
        if (averageEffective is not null) return averageEffective.Value;

        var effective = clocks.Where(r => r.Name.Contains("Effective", StringComparison.OrdinalIgnoreCase)).ToList();
        var source = effective.Count > 0
            ? effective
            : clocks.Where(r => !r.Name.Contains("Average", StringComparison.OrdinalIgnoreCase)).ToList();
        return source.Count > 0 ? source.Average(r => r.Value!.Value) : null;
    }

    public static double? CpuPower(IReadOnlyList<SensorReading> s) =>
        ByName(s.Where(r => IsCpu(r) && r.Type == "Power"), "CPU Package", "Package", "Socket");

    /// <summary>Prefers the discrete GPU (NVIDIA, then AMD) over integrated graphics.</summary>
    private static List<SensorReading> PrimaryGpu(IReadOnlyList<SensorReading> s)
    {
        foreach (var kind in new[] { "GpuNvidia", "GpuAmd", "GpuIntel" })
        {
            var group = s.Where(r => r.Kind == kind).ToList();
            if (group.Count > 0)
            {
                var firstGroup = group[0].GroupId;
                return group.Where(r => r.GroupId == firstGroup).ToList();
            }
        }
        return s.Where(IsGpu).ToList();
    }

    public static string? PrimaryGpuName(IReadOnlyList<SensorReading> s) => PrimaryGpu(s).FirstOrDefault()?.GroupName;

    /// <summary>
    /// Integrated GPUs often report 0% on "GPU Core" while Windows' D3D 3D engine counter shows the
    /// real activity, so take the larger of the two.
    /// </summary>
    public static double? GpuLoad(IReadOnlyList<SensorReading> s)
    {
        var loads = PrimaryGpu(s).Where(r => r.Type == "Load" && r.Value.HasValue).ToList();
        // Task Manager's "GPU %" is the busiest Windows D3D engine. It's consistent across vendors,
        // whereas APU "GPU Core" counters are often stuck at 0 or 100.
        var d3d = loads.Where(r => r.Name.StartsWith("D3D ", StringComparison.OrdinalIgnoreCase)).Select(r => r.Value!.Value).ToList();
        if (d3d.Count > 0) return Math.Min(100, d3d.Max());
        return ByName(loads, "GPU Core", "GPU");
    }

    public static double? GpuTemperature(IReadOnlyList<SensorReading> s)
    {
        var temps = PrimaryGpu(s).Where(r => r.Type == "Temperature" && r.Value is > 0 and < 150).ToList();
        return ByName(temps, "GPU Core", "GPU Hot Spot", "GPU")
               ?? (temps.Count > 0 ? temps.Max(r => r.Value) : null);
    }

    public static double? GpuClock(IReadOnlyList<SensorReading> s) =>
        ByName(PrimaryGpu(s).Where(r => r.Type == "Clock"), "GPU Core");

    public static double? GpuPower(IReadOnlyList<SensorReading> s) =>
        ByName(PrimaryGpu(s).Where(r => r.Type == "Power"), "GPU Package", "GPU Power", "GPU Core");

    public static double? GpuMemoryUsedMb(IReadOnlyList<SensorReading> s) =>
        ByName(PrimaryGpu(s).Where(r => r.Type == "SmallData"), "GPU Memory Used", "D3D Dedicated Memory Used");

    /// <summary>RAM load from the OS directly — always available, even if LHM fails.</summary>
    public static double? MemoryLoad() => Native.GetMemoryStatus() is { } m && m.Total > 0
        ? 100.0 * (m.Total - m.Available) / m.Total
        : null;

    public static double? MemoryUsedGb() => Native.GetMemoryStatus() is { } m
        ? (m.Total - m.Available) / 1024.0 / 1024 / 1024
        : null;

    public static bool HasCpuTemperature(IReadOnlyList<SensorReading> s) =>
        s.Any(r => IsCpu(r) && r.Type == "Temperature" && r.Value is > 0);

    /// <summary>Highest temperature reported for a storage device whose name matches.</summary>
    public static double? StorageTemperature(IReadOnlyList<SensorReading> s, string diskName) =>
        s.Where(r => r.Kind == "Storage" && r.Type == "Temperature" && r.Value is > 0
                     && (r.GroupName.Contains(diskName, StringComparison.OrdinalIgnoreCase)
                         || diskName.Contains(r.GroupName, StringComparison.OrdinalIgnoreCase)))
         .Select(r => r.Value)
         .DefaultIfEmpty(null)
         .Max();
}
