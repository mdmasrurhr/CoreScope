using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CoreScope.Core.Hardware;

namespace CoreScope.Core;

public sealed record AlertEvent(string Key, string Title, string Message, DateTime When);

/// <summary>
/// Raises an alert when a reading stays past its threshold for 10 consecutive samples, then stays quiet
/// for 10 minutes for that same alert, so you get one notification instead of a flood.
/// </summary>
public sealed class AlertMonitor
{
    private const int SustainSamples = 10;
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, int> _over = new();
    private readonly Dictionary<string, DateTime> _lastFired = new();

    public event Action<AlertEvent>? Raised;

    public void Observe(IReadOnlyList<SensorReading> s, AppSettings cfg)
    {
        if (!cfg.AlertsEnabled || s.Count == 0) return;

        Check("cpu-temp", SensorPick.CpuTemperature(s), cfg.AlertCpuTempC,
            v => ("CPU is running hot", $"CPU has been at {Format.Temperature(v)} for 10+ seconds (your alert is {Format.Temperature(cfg.AlertCpuTempC)}). Check airflow and what's running."));
        Check("gpu-temp", SensorPick.GpuTemperature(s), cfg.AlertGpuTempC,
            v => ("GPU is running hot", $"GPU has been at {Format.Temperature(v)} for 10+ seconds (alert at {Format.Temperature(cfg.AlertGpuTempC)})."));
        var drive = s.Where(r => r.Kind == "Storage" && r.Type == "Temperature" && r.Value is > 0 and < 120
                                 && !r.Name.Contains("Warning", StringComparison.OrdinalIgnoreCase)
                                 && !r.Name.Contains("Critical", StringComparison.OrdinalIgnoreCase))
                     .Select(r => r.Value).DefaultIfEmpty(null).Max();
        Check("drive-temp", drive, cfg.AlertDriveTempC,
            v => ("Drive is running hot", $"A drive has been at {Format.Temperature(v)} for 10+ seconds. SSDs slow down to protect themselves around 70–80 °C."));

        if (Native.GetPowerStatus() is { PluggedIn: false } power)
            Check("battery-low", 100 - power.Percent, 100 - cfg.AlertBatteryPercent,
                _ => ("Battery is low", $"Battery is at {power.Percent}%. Plug in soon."));
        else
            _over["battery-low"] = 0;
    }

    private void Check(string key, double? value, double threshold, Func<double, (string Title, string Message)> describe)
    {
        if (value is not { } v || v < threshold)
        {
            _over[key] = 0;
            return;
        }
        _over[key] = _over.GetValueOrDefault(key) + 1;
        if (_over[key] < SustainSamples) return;
        if (_lastFired.TryGetValue(key, out var last) && DateTime.Now - last < Cooldown) return;

        _lastFired[key] = DateTime.Now;
        var (title, message) = describe(v);
        Log.Info($"Alert: {title} — {message}");
        Raised?.Invoke(new AlertEvent(key, title, message, DateTime.Now));
    }
}

public sealed record ProcessUsage(string Name, double CpuPercent, long MemoryBytes, int Count)
{
    public string CpuText => $"{CpuPercent:0.0}%";
    public string MemoryText => Format.Bytes(MemoryBytes);
}

/// <summary>"What's using my PC": per-app CPU % and memory, summing all processes of the same app (e.g. every chrome.exe).</summary>
public sealed class ProcessMonitor
{
    private Dictionary<int, TimeSpan> _lastCpu = new();
    private DateTime _lastSample = DateTime.MinValue;

    public long LastSampleMs { get; private set; }

    public (List<ProcessUsage> ByCpu, List<ProcessUsage> ByMemory) Sample(int top = 5)
    {
        var timer = Stopwatch.StartNew();
        try { return SampleCore(top); }
        finally { LastSampleMs = timer.ElapsedMilliseconds; }
    }

    private (List<ProcessUsage> ByCpu, List<ProcessUsage> ByMemory) SampleCore(int top)
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastSample).TotalMilliseconds;
        var cpuNow = new Dictionary<int, TimeSpan>();
        var usage = new Dictionary<string, (double Cpu, long Mem, int Count)>(StringComparer.OrdinalIgnoreCase);
        int cores = Environment.ProcessorCount;

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == 0) continue; // System Idle Process
                    var name = p.ProcessName;
                    long mem = p.WorkingSet64;
                    double cpu = 0;
                    try
                    {
                        var total = p.TotalProcessorTime;
                        cpuNow[p.Id] = total;
                        if (_lastCpu.TryGetValue(p.Id, out var before) && elapsed > 0)
                            cpu = (total - before).TotalMilliseconds / elapsed / cores * 100;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                    var acc = usage.GetValueOrDefault(name);
                    usage[name] = (acc.Cpu + cpu, acc.Mem + mem, acc.Count + 1);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }

        bool firstSample = _lastSample == DateTime.MinValue;
        _lastCpu = cpuNow;
        _lastSample = now;

        var all = usage.Select(kv => new ProcessUsage(kv.Key, Math.Min(100, kv.Value.Cpu), kv.Value.Mem, kv.Value.Count)).ToList();
        var byCpu = firstSample ? new List<ProcessUsage>() : all.Where(u => u.CpuPercent >= 0.1 && u.Name != "Idle").OrderByDescending(u => u.CpuPercent).Take(top).ToList();
        var byMem = all.OrderByDescending(u => u.MemoryBytes).Take(top).ToList();
        return (byCpu, byMem);
    }
}
