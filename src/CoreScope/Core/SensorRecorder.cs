using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CoreScope.Core.Hardware;

namespace CoreScope.Core;

/// <summary>
/// Records every sensor to a CSV file (one row per refresh) so a session — a game, a render, a stress
/// test — can be graphed later in Excel. Columns are fixed by the first snapshot; values are raw
/// (°C, MHz, W, %, …) in invariant culture so the file opens the same everywhere.
/// </summary>
public sealed class SensorRecorder : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly List<string> _ids = new();
    private readonly StringBuilder _line = new();
    private bool _headerWritten;

    public SensorRecorder(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(true)) { AutoFlush = false };
        Started = DateTime.Now;
    }

    public string Path { get; }
    public DateTime Started { get; }
    public int Rows { get; private set; }

    public static string DefaultFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CoreScope");

    public static string NewFilePath() =>
        System.IO.Path.Combine(DefaultFolder, $"sensors-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

    public void Write(IReadOnlyList<SensorReading> readings)
    {
        if (readings.Count == 0) return;
        if (!_headerWritten)
        {
            _ids.AddRange(readings.Select(r => r.Id));
            _line.Clear().Append("Time");
            foreach (var r in readings)
                _line.Append(',').Append(Quote($"{r.GroupName} / {r.Name} [{Unit(r.Type)}]"));
            _writer.WriteLine(_line.ToString());
            _headerWritten = true;
        }

        var byId = new Dictionary<string, double?>(readings.Count);
        foreach (var r in readings) byId[r.Id] = r.Value;

        _line.Clear().Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        foreach (var id in _ids)
        {
            _line.Append(',');
            if (byId.TryGetValue(id, out var v) && v is { } x && !double.IsNaN(x))
                _line.Append(x.ToString("0.###", CultureInfo.InvariantCulture));
        }
        _writer.WriteLine(_line.ToString());
        Rows++;
        if (Rows % 10 == 0) _writer.Flush();
    }

    public static string Unit(string type) => type switch
    {
        "Temperature" => "°C",
        "Clock" => "MHz",
        "Power" => "W",
        "Voltage" => "V",
        "Current" => "A",
        "Load" or "Level" or "Control" => "%",
        "Fan" => "RPM",
        "Data" => "GB",
        "SmallData" => "MB",
        "Throughput" => "B/s",
        "Energy" => "mWh",
        "Frequency" => "Hz",
        "TimeSpan" => "s",
        _ => type,
    };

    private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    public void Dispose()
    {
        try { _writer.Flush(); _writer.Dispose(); }
        catch (IOException ex) { Log.Error("Closing sensor recording", ex); }
    }
}
