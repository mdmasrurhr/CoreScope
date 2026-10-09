using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CoreScope.Core.Hardware;

/// <summary>
/// Driver-free sensors from Windows' own performance counters (PDH) — the same data Task Manager uses:
/// CPU utility and effective clock per core, ACPI thermal zones, the Energy Meter power rails (RAPL on Intel and
/// many AMD laptops) and GPU engine utilization. No kernel driver, no administrator rights.
/// Counters are added by their English names (PdhAddEnglishCounter), so it works in every Windows language.
/// </summary>
public sealed class WinSensors : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_MORE_DATA = 0x800007D2;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);

    private IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = new();
    private readonly string _cpuName;
    private readonly string _cpuGroup = "/win/cpu";
    private bool _primed;

    public WinSensors(string cpuName)
    {
        _cpuName = cpuName.Length > 0 ? Format.CleanName(cpuName) : "Processor";
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
        Add("utility", @"\Processor Information(*)\% Processor Utility");
        Add("performance", @"\Processor Information(*)\% Processor Performance");
        Add("frequency", @"\Processor Information(*)\Processor Frequency");
        Add("tz", @"\Thermal Zone Information(*)\High Precision Temperature");
        Add("tzk", @"\Thermal Zone Information(*)\Temperature");
        Add("energy", @"\Energy Meter(*)\Power");
        Add("gpu", @"\GPU Engine(*)\Utilization Percentage");
        Log.Info($"Windows counters: {string.Join(", ", _counters.Keys)}");
    }

    private void Add(string key, string path)
    {
        if (_query != IntPtr.Zero && PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter) == 0) _counters[key] = counter;
    }

    /// <summary>Instance name → value for a wildcard counter. Empty when the counter isn't available on this PC.</summary>
    private Dictionary<string, double> Values(string key)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!_counters.TryGetValue(key, out var counter)) return result;
        uint size = 0;
        if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero) != PDH_MORE_DATA || size == 0) return result;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out var count, buffer) != 0) return result;
            int stride = IntPtr.Size + 8 + 8; // name pointer, CStatus (+padding), double
            for (int i = 0; i < count; i++)
            {
                var item = buffer + i * stride;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                uint status = (uint)Marshal.ReadInt32(item + IntPtr.Size);
                if (status > 1) continue; // PDH_CSTATUS_VALID_DATA / NEW_DATA only
                double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + IntPtr.Size + 8));
                if (!double.IsNaN(value)) result[name] = value;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }

    private readonly Dictionary<string, (double Min, double Max)> _range = new();

    /// <summary>Builds a reading and keeps its session min/max (Windows counters don't track them).</summary>
    private SensorReading R(string groupId, string group, string kind, string name, string type, double value)
    {
        var id = $"{groupId}/{type}/{name}";
        var (min, max) = _range.TryGetValue(id, out var r) ? (Math.Min(r.Min, value), Math.Max(r.Max, value)) : (value, value);
        _range[id] = (min, max);
        return new SensorReading(id, groupId, group, kind, name, type, value, min, max);
    }

    /// <summary>One snapshot. Rate counters need two samples, so the first call primes and returns nothing.</summary>
    public List<SensorReading> Read()
    {
        var list = new List<SensorReading>();
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return list;
        if (!_primed) { _primed = true; return list; }

        // ── CPU: utility (Task Manager's CPU %) and effective clock = nominal × % performance.
        var utility = Values("utility");
        var performance = Values("performance");
        var frequency = Values("frequency");
        if (utility.TryGetValue("_Total", out var total))
            list.Add(R(_cpuGroup, _cpuName, "Cpu", "CPU Total", "Load", Math.Clamp(total, 0, 100)));
        var cores = utility.Keys.Where(k => Regex.IsMatch(k, @"^\d+,\d+$"))
                           .OrderBy(k => int.Parse(k.Split(',')[0], CultureInfo.InvariantCulture)).ThenBy(k => int.Parse(k.Split(',')[1], CultureInfo.InvariantCulture)).ToList();
        int n = 1;
        foreach (var core in cores)
        {
            list.Add(R(_cpuGroup, _cpuName, "Cpu", $"CPU Core #{n}", "Load", Math.Clamp(utility[core], 0, 100)));
            if (performance.TryGetValue(core, out var perf) && frequency.TryGetValue(core, out var mhz) && mhz > 0)
                list.Add(R(_cpuGroup, _cpuName, "Cpu", $"Core #{n} (Effective)", "Clock", mhz * perf / 100));
            n++;
        }
        if (performance.TryGetValue("_Total", out var perfTotal) && frequency.TryGetValue("_Total", out var mhzTotal) && mhzTotal > 0)
            list.Add(R(_cpuGroup, _cpuName, "Cpu", "Cores (Average Effective)", "Clock", mhzTotal * perfTotal / 100));

        // ── Thermal zones (ACPI). High-precision is tenths of a kelvin; the plain counter is whole kelvin.
        var zones = Values("tz").ToDictionary(kv => kv.Key, kv => kv.Value / 10 - 273.15);
        if (zones.Count == 0) zones = Values("tzk").ToDictionary(kv => kv.Key, kv => kv.Value - 273.15);
        foreach (var (zone, celsius) in zones.Where(z => z.Value is > 0 and < 125))
            list.Add(R("/win/thermal", "Thermal zones (ACPI)", "ThermalZone", ZoneName(zone), "Temperature", celsius));

        // ── Energy Meter rails (mW → W). Package / cores rails become the CPU power reading.
        foreach (var (rail, mw) in Values("energy").Where(e => e.Value >= 0))
        {
            var watts = mw / 1000;
            list.Add(R("/win/energy", "Power rails (Energy Meter)", "PowerRail", rail, "Power", watts));
            if (Regex.IsMatch(rail, @"PKG|PACKAGE|SOC|CPU", RegexOptions.IgnoreCase) && !Regex.IsMatch(rail, @"PP1|GPU|GFX|DRAM", RegexOptions.IgnoreCase))
                list.Add(R(_cpuGroup, _cpuName, "Cpu", Regex.IsMatch(rail, "PP0|CORE", RegexOptions.IgnoreCase) ? "CPU Cores" : "CPU Package", "Power", watts));
        }

        // ── GPU: per adapter, the busiest engine type (Task Manager's "GPU %").
        var engines = Values("gpu");
        foreach (var adapter in engines.GroupBy(e => Regex.Match(e.Key, @"luid_0x[0-9a-fA-F]+_0x[0-9a-fA-F]+").Value).Where(g => g.Key.Length > 0))
        {
            var byType = adapter.GroupBy(e => Regex.Match(e.Key, @"engtype_(.+)$").Groups[1].Value).ToDictionary(g => g.Key, g => g.Sum(x => x.Value));
            if (byType.Count == 0) continue;
            list.Add(R($"/win/gpu/{adapter.Key}", "GPU engines", "GpuEngines", "D3D busiest engine", "Load", Math.Min(100, byType.Values.Max())));
        }
        return list;
    }

    private static string ZoneName(string instance) =>
        instance.StartsWith(@"\_TZ.", StringComparison.OrdinalIgnoreCase) ? instance[5..] : instance;

    public void Dispose()
    {
        if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }
}

/// <summary>
/// Picks the most accurate driver-free CPU temperature available and says where it came from.
/// AMD APUs: the integrated Radeon reports the shared die temperature through AMD's driver (no extra driver needed).
/// Otherwise: the ACPI thermal zone the firmware links to the CPU, or the hottest believable zone.
/// </summary>
public static class CpuTemperatureSource
{
    private static readonly Regex AmdIntegrated = new(
        @"^AMD Radeon(\(TM\))?\s*(Graphics|\d{3}M\b|Vega)|Radeon Vega \d+|Radeon\(TM\) Graphics",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Adds a "CPU die" temperature to the CPU group when a better source than a thermal zone exists.</summary>
    public static void Augment(List<SensorReading> readings, bool cpuIsAmd, string cpuGroupId = "/win/cpu", string cpuName = "Processor")
    {
        if (readings.Any(r => r.Kind == "Cpu" && r.Type == "Temperature")) return;

        if (cpuIsAmd)
        {
            var apu = readings.Where(r => r.Kind == "GpuAmd" && !r.GroupName.Contains(" RX ", StringComparison.OrdinalIgnoreCase)
                                          && AmdIntegrated.IsMatch(r.GroupName) && r.Type == "Temperature" && r.Value is > 15 and < 125).ToList();
            var die = apu.FirstOrDefault(r => r.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                      ?? apu.FirstOrDefault(r => r.Name.Contains("SoC", StringComparison.OrdinalIgnoreCase))
                      ?? apu.OrderByDescending(r => r.Value).FirstOrDefault();
            if (die is not null)
            {
                readings.Add(new SensorReading($"{cpuGroupId}/Temperature/die", cpuGroupId, cpuName, "Cpu", "CPU die (shared APU sensor)", "Temperature", die.Value, null, null));
                return;
            }
        }

        var zones = readings.Where(r => r.Kind == "ThermalZone" && r.Value is > 15 and < 125).ToList();
        var cpuZone = zones.FirstOrDefault(z => Regex.IsMatch(z.Name, "CPU|PROC|TCPU", RegexOptions.IgnoreCase))
                      ?? zones.OrderByDescending(z => z.Value).FirstOrDefault();
        if (cpuZone is not null)
            readings.Add(new SensorReading($"{cpuGroupId}/Temperature/zone", cpuGroupId, cpuName, "Cpu", $"CPU area (thermal zone {cpuZone.Name})", "Temperature", cpuZone.Value, null, null));
    }

    /// <summary>Short note for the CPU temperature tile.</summary>
    public static string? Describe(IReadOnlyList<SensorReading> readings)
    {
        var t = readings.FirstOrDefault(r => r.Kind == "Cpu" && r.Type == "Temperature");
        return t?.Name switch
        {
            null => null,
            var n when n.StartsWith("CPU die", StringComparison.Ordinal) => "die sensor",
            var n when n.StartsWith("CPU area", StringComparison.Ordinal) => "approx.",
            _ => null,
        };
    }
}
