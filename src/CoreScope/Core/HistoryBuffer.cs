using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreScope.Core;

public enum HistoryRange { OneMinute, FiveMinutes, OneHour, OneDay }

/// <summary>
/// Keeps a metric's history at three resolutions with fixed memory: every sample for 5 minutes,
/// 10-second averages for an hour, and 4-minute averages for 24 hours (about 1,000 numbers per metric).
/// </summary>
public sealed class HistoryBuffer
{
    private readonly Queue<(DateTime T, double V)> _raw = new();
    private readonly Bucketed _tenSeconds = new(TimeSpan.FromSeconds(10), 361);
    private readonly Bucketed _fourMinutes = new(TimeSpan.FromMinutes(4), 361);

    public void Add(double value) => Add(DateTime.UtcNow, value);

    public void Add(DateTime utc, double value)
    {
        _raw.Enqueue((utc, value));
        while (_raw.Count > 0 && utc - _raw.Peek().T > TimeSpan.FromMinutes(5)) _raw.Dequeue();
        _tenSeconds.Add(utc, value);
        _fourMinutes.Add(utc, value);
    }

    public static TimeSpan Window(HistoryRange range) => range switch
    {
        HistoryRange.OneMinute => TimeSpan.FromMinutes(1),
        HistoryRange.FiveMinutes => TimeSpan.FromMinutes(5),
        HistoryRange.OneHour => TimeSpan.FromHours(1),
        _ => TimeSpan.FromHours(24),
    };

    /// <summary>How many points a full window holds (so a young history is drawn right-aligned, not stretched).</summary>
    public static int Capacity(HistoryRange range, int sampleIntervalMs) => range switch
    {
        HistoryRange.OneMinute => Math.Max(2, 60_000 / Math.Max(250, sampleIntervalMs)),
        HistoryRange.FiveMinutes => Math.Max(2, 300_000 / Math.Max(250, sampleIntervalMs)),
        HistoryRange.OneHour => 360,
        _ => 360,
    };

    public double[] Get(HistoryRange range)
    {
        var now = DateTime.UtcNow;
        return range switch
        {
            HistoryRange.OneMinute => _raw.Where(x => now - x.T <= TimeSpan.FromMinutes(1)).Select(x => x.V).ToArray(),
            HistoryRange.FiveMinutes => _raw.Select(x => x.V).ToArray(),
            HistoryRange.OneHour => _tenSeconds.Values(),
            _ => _fourMinutes.Values(),
        };
    }

    private sealed class Bucketed
    {
        private readonly long _ticks;
        private readonly int _keep;
        private readonly LinkedList<(long Index, double Sum, int Count)> _buckets = new();

        public Bucketed(TimeSpan size, int keep)
        {
            _ticks = size.Ticks;
            _keep = keep;
        }

        public void Add(DateTime utc, double value)
        {
            long index = utc.Ticks / _ticks;
            if (_buckets.Last is { } last && last.Value.Index == index)
                last.Value = (index, last.Value.Sum + value, last.Value.Count + 1);
            else
                _buckets.AddLast((index, value, 1));
            while (_buckets.Count > _keep) _buckets.RemoveFirst();
        }

        public double[] Values() => _buckets.Select(b => b.Sum / b.Count).ToArray();
    }
}
