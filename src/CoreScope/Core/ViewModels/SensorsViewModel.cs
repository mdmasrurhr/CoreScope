using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CoreScope.Core.Hardware;

namespace CoreScope.Core.ViewModels;

public sealed class SensorRowViewModel : ObservableObject
{
    private const int Capacity = 60;
    private readonly Queue<double> _history = new();
    private string _value = "—";
    private string _min = "—";
    private string _max = "—";
    private double[] _data = Array.Empty<double>();
    private bool _isVisible = true;

    public SensorRowViewModel(SensorReading reading)
    {
        Id = reading.Id;
        Name = reading.Name;
        Type = reading.Type;
    }

    public string Id { get; }
    public string Name { get; }
    public string Type { get; }
    public string Value { get => _value; private set => Set(ref _value, value); }
    public string Min { get => _min; private set => Set(ref _min, value); }
    public string Max { get => _max; private set => Set(ref _max, value); }
    public double[] Data { get => _data; private set => Set(ref _data, value); }
    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }

    /// <summary>Load-style sensors get a fixed 0–100 graph scale; everything else auto-scales.</summary>
    public double ScaleMax => Type is "Load" or "Level" or "Control" ? 100 : double.NaN;
    public double ScaleMin => Type is "Load" or "Level" or "Control" ? 0 : double.NaN;

    public void Update(SensorReading reading)
    {
        Value = Format.Sensor(Type, reading.Value);
        Min = Format.Sensor(Type, reading.Min);
        Max = Format.Sensor(Type, reading.Max);
        if (reading.Value is { } v && !double.IsNaN(v))
        {
            _history.Enqueue(v);
            while (_history.Count > Capacity) _history.Dequeue();
            Data = _history.ToArray();
        }
    }
}

public sealed class SensorGroupViewModel : ObservableObject
{
    private bool _isVisible = true;

    public SensorGroupViewModel(SensorReading first)
    {
        Id = first.GroupId;
        Name = first.GroupName;
        Kind = first.Kind;
        Glyph = first.Kind switch
        {
            "Cpu" => "",
            "Memory" => "",
            "Motherboard" or "SuperIO" => "",
            "Storage" => "",
            "Battery" => "",
            "Network" => "",
            _ when first.Kind.StartsWith("Gpu", StringComparison.Ordinal) => "",
            _ => "",
        };
    }

    public string Id { get; }
    public string Name { get; }
    public string Kind { get; }
    public string Glyph { get; }
    public ObservableCollection<SensorRowViewModel> Sensors { get; } = new();
    public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }
}

public sealed class SensorsViewModel : PageViewModel
{
    private readonly Dictionary<string, SensorGroupViewModel> _groups = new();
    private readonly Dictionary<string, SensorRowViewModel> _rows = new();
    private string _filter = "";
    private string _status = "Starting sensor engine…";

    private bool _hasNoMatches;

    private SensorRecorder? _recorder;
    private string _recordingText = "";

    public SensorsViewModel() : base("Sensors", "\uE9D9")
    {
        ClearFilterCommand = new RelayCommand(() => Filter = "");
        RecordCommand = new RelayCommand(ToggleRecording);
        OpenRecordingsCommand = new RelayCommand(() =>
        {
            System.IO.Directory.CreateDirectory(SensorRecorder.DefaultFolder);
            Shell.Open(SensorRecorder.DefaultFolder);
        });
    }

    public RelayCommand RecordCommand { get; }
    public RelayCommand OpenRecordingsCommand { get; }
    public bool IsRecording => _recorder is not null;
    public string RecordButtonText => IsRecording ? "Stop recording" : "Record to CSV";
    /// <summary>"Recording · 42 rows · sensors-20261004-0130.csv" while active; last file name afterwards.</summary>
    public string RecordingText { get => _recordingText; private set => Set(ref _recordingText, value); }
    public int IntervalMs { get; set; } = 1000;

    private void ToggleRecording()
    {
        if (_recorder is null)
        {
            try
            {
                _recorder = new SensorRecorder(SensorRecorder.NewFilePath());
                RecordingText = $"Recording to {System.IO.Path.GetFileName(_recorder.Path)}…";
                Log.Info($"Sensor recording started: {_recorder.Path}");
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                Log.Error("Starting sensor recording", ex);
                RecordingText = "Couldn't create the file in Documents\\CoreScope. Check folder permissions.";
            }
        }
        else StopRecording();
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(RecordButtonText));
    }

    public void StopRecording()
    {
        if (_recorder is null) return;
        var r = _recorder;
        _recorder = null;
        r.Dispose();
        RecordingText = $"Saved {r.Rows} rows to Documents\\CoreScope\\{System.IO.Path.GetFileName(r.Path)}";
        Log.Info($"Sensor recording stopped: {r.Rows} rows");
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(RecordButtonText));
    }

    public ObservableCollection<SensorGroupViewModel> Groups { get; } = new();
    public string Status { get => _status; set => Set(ref _status, value); }
    public RelayCommand ClearFilterCommand { get; }

    /// <summary>A filter is typed but matches nothing (drives the empty state).</summary>
    public bool HasNoMatches { get => _hasNoMatches; private set => Set(ref _hasNoMatches, value); }

    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value)) ApplyFilter();
        }
    }

    public void Update(IReadOnlyList<SensorReading> readings)
    {
        bool added = false;
        foreach (var r in readings)
        {
            if (!_rows.TryGetValue(r.Id, out var row))
            {
                if (!_groups.TryGetValue(r.GroupId, out var group))
                {
                    group = new SensorGroupViewModel(r);
                    _groups[r.GroupId] = group;
                    Groups.Add(group);
                }
                row = new SensorRowViewModel(r);
                _rows[r.Id] = row;
                group.Sensors.Add(row);
                added = true;
            }
            row.Update(r);
        }
        if (added) ApplyFilter();
        if (_recorder is not null)
        {
            try
            {
                _recorder.Write(readings);
                var elapsed = DateTime.Now - _recorder.Started;
                RecordingText = $"Recording · {_recorder.Rows} rows · {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00} · {System.IO.Path.GetFileName(_recorder.Path)}";
            }
            catch (System.IO.IOException ex)
            {
                Log.Error("Writing sensor recording", ex);
                StopRecording();
                RecordingText = "Recording stopped: the disk is full or the file was locked.";
            }
        }
        var every = IntervalMs <= 1000 ? "every second" : $"every {IntervalMs / 1000.0:0.#} seconds";
        Status = readings.Count == 0 ? "No sensors reported yet." : $"{readings.Count} sensors · {Groups.Count} devices · updating {every}";
    }

    private void ApplyFilter()
    {
        var f = _filter.Trim();
        foreach (var group in Groups)
        {
            bool groupMatch = f.Length == 0 || group.Name.Contains(f, StringComparison.OrdinalIgnoreCase);
            bool any = false;
            foreach (var row in group.Sensors)
            {
                row.IsVisible = groupMatch
                                || row.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                                || row.Type.Contains(f, StringComparison.OrdinalIgnoreCase);
                any |= row.IsVisible;
            }
            group.IsVisible = any;
        }
        HasNoMatches = f.Length > 0 && Groups.Count > 0 && !Groups.Any(g => g.IsVisible);
    }
}
