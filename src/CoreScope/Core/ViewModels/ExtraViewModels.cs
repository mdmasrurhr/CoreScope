using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CoreScope.Core.Hardware;

namespace CoreScope.Core.ViewModels;

public sealed record BenchmarkRow(string Metric, string Value, string Previous, string Verdict);

/// <summary>Benchmarks page: run, compare with your previous run, keep a history.</summary>
public sealed class BenchmarkViewModel : PageViewModel
{
    private static string HistoryPath => Path.Combine(AppSettings.DataFolder, "benchmarks.json");
    private readonly Func<string> _diskFolder;
    private bool _isRunning;
    private string _status = "Takes about 25 seconds. Close other apps for consistent results.";
    private BenchmarkResult? _last;

    public BenchmarkViewModel(Func<string> diskFolder) : base("Benchmark", "")
    {
        _diskFolder = diskFolder;
        RunCommand = new RelayCommand(async () => await RunAsync());
        foreach (var r in LoadHistory()) History.Insert(0, r);
        if (History.Count > 0) Show(History[0], History.Count > 1 ? History[1] : null);
    }

    public RelayCommand RunCommand { get; }
    public bool IsRunning { get => _isRunning; private set { Set(ref _isRunning, value); OnPropertyChanged(nameof(CanRun)); } }
    public bool CanRun => !IsRunning;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public ObservableCollection<BenchmarkRow> Rows { get; } = new();
    public ObservableCollection<BenchmarkResult> History { get; } = new();
    public BenchmarkResult? Last => _last;

    private async Task RunAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        try
        {
            var progress = new Progress<string>(s => Status = s);
            var result = await Benchmark.RunAllAsync(_diskFolder(), progress);
            var previous = History.FirstOrDefault();
            History.Insert(0, result);
            SaveHistory();
            Show(result, previous);
            Status = $"Finished at {result.When:HH:mm}.";
        }
        catch (Exception ex)
        {
            Log.Error("Benchmark", ex);
            Status = $"Benchmark failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void Show(BenchmarkResult r, BenchmarkResult? prev)
    {
        _last = r;
        Rows.Clear();
        string P(double? v, string fmt) => v is { } x ? x.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture) : "—";
        Rows.Add(new("CPU single-core", $"{r.CpuSingle:0} pts", P(prev?.CpuSingle, "0") , "Snappiness: app launches, browsing, most games. Reference: Ryzen 5 7520U core ≈ 1000."));
        Rows.Add(new("CPU all cores", $"{r.CpuMulti:0} pts", P(prev?.CpuMulti, "0"), "Heavy work: compiling, rendering, video export."));
        Rows.Add(new("Memory copy", $"{r.MemoryCopyGBs:0.0} GB/s", P(prev?.MemoryCopyGBs, "0.0"), r.MemoryCopyGBs switch
        {
            >= 40 => "Excellent (fast DDR5 / LPDDR5X)",
            >= 20 => "Good (dual-channel DDR4 / LPDDR5 class)",
            >= 11 => "Moderate (single-channel or slower memory)",
            _ => "Low: check for single-channel memory",
        }));
        Rows.Add(new("Memory latency", $"{r.MemoryLatencyNs:0} ns", P(prev?.MemoryLatencyNs, "0"), r.MemoryLatencyNs switch
        {
            // Measured with normal 4 KB pages, so it includes page-table walks: ~30–60 ns above AIDA64-style figures.
            <= 110 => "Excellent", <= 150 => "Typical desktop DDR4/DDR5", <= 220 => "Typical laptop / LPDDR memory", _ => "High: check memory configuration",
        }));
        Rows.Add(new("Disk sequential read", $"{r.DiskSeqReadMBs:0} MB/s", P(prev?.DiskSeqReadMBs, "0"), DiskVerdict(r.DiskSeqReadMBs)));
        Rows.Add(new("Disk sequential write", $"{r.DiskSeqWriteMBs:0} MB/s", P(prev?.DiskSeqWriteMBs, "0"), "Large file copies, game installs."));
        Rows.Add(new("Disk 4K random read (QD1)", $"{r.Disk4kReadMBs:0.0} MB/s", P(prev?.Disk4kReadMBs, "0.0"), r.Disk4kReadMBs switch
        {
            >= 40 => "Excellent: everything loads instantly", >= 20 => "Good SSD", >= 5 => "Entry-level SSD", _ => "Hard-disk class: an SSD would transform this PC",
        }));
        OnPropertyChanged(nameof(Last));
    }

    private static string DiskVerdict(double mbs) => mbs switch
    {
        >= 5500 => "PCIe 4.0/5.0 NVMe class",
        >= 2500 => "Fast NVMe (PCIe 3.0 x4 / 4.0)",
        >= 1200 => "NVMe (PCIe 3.0 x2 or DRAM-less)",
        >= 700 => "Entry NVMe",
        >= 400 => "SATA SSD class",
        _ => "Hard-disk class",
    };

    private static List<BenchmarkResult> LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryPath))
                return JsonSerializer.Deserialize<List<BenchmarkResult>>(File.ReadAllText(HistoryPath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { Log.Error("Loading benchmark history", ex); }
        return new();
    }

    private void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataFolder);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(History.Reverse().TakeLast(50).ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Saving benchmark history", ex); }
    }
}

public sealed record ChoiceOption<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

/// <summary>Settings & About.</summary>
public sealed class SettingsViewModel : PageViewModel
{
    private readonly IAppServices _services;
    private readonly AppSettings _s = AppSettings.Current;
    private bool _startWithWindows;

    public SettingsViewModel(IAppServices services, UpdateViewModel update) : base("Settings", "")
    {
        Update = update;
        _services = services;
        _startWithWindows = StartupRegistration.IsRegistered();
        OpenDataFolderCommand = new RelayCommand(() => Shell.Open(AppSettings.DataFolder));
        OpenLogCommand = new RelayCommand(() => Shell.Open(Path.GetDirectoryName(Log.Path)!));
        OpenNoticesCommand = new RelayCommand(() => Shell.Open(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt")));
        RunSelfTestCommand = new RelayCommand(RunSelfTest);
    }

    public UpdateViewModel Update { get; }
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "";
    public string LogPath => Log.Path;
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand OpenNoticesCommand { get; }
    public RelayCommand RunSelfTestCommand { get; }

    private void Save([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        _s.Save();
        OnPropertyChanged(name);
    }

    public bool CloseToTray { get => _s.CloseToTray; set { _s.CloseToTray = value; Save(); } }
    public bool StartMinimized { get => _s.StartMinimized; set { _s.StartMinimized = value; Save(); } }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (value == _startWithWindows) return;
            if (StartupRegistration.Set(value)) _startWithWindows = value;
            else _services.Notify("CoreScope", "Couldn't change the startup task. CoreScope needs to run as administrator for this.");
            OnPropertyChanged();
        }
    }

    public bool UseFahrenheit { get => _s.UseFahrenheit; set { _s.UseFahrenheit = value; Save(); } }

    public List<ChoiceOption<int>> RefreshOptions { get; } = new()
    {
        new("Every 0.5 seconds", 500), new("Every second", 1000), new("Every 2 seconds", 2000), new("Every 5 seconds (lightest)", 5000),
    };

    public ChoiceOption<int>? RefreshInterval
    {
        get => RefreshOptions.FirstOrDefault(o => o.Value == _s.RefreshIntervalMs) ?? RefreshOptions[1];
        set { if (value is null) return; _s.RefreshIntervalMs = value.Value; Save(); RefreshIntervalChanged?.Invoke(value.Value); }
    }

    public event Action<int>? RefreshIntervalChanged;

    public bool ShowOverlay
    {
        get => _s.ShowOverlay;
        set { _s.ShowOverlay = value; Save(); _services.SetOverlayVisible(value); }
    }

    public bool OverlayClickThrough { get => _s.OverlayClickThrough; set { _s.OverlayClickThrough = value; Save(); _services.RefreshOverlaySettings(); } }
    public double OverlayOpacity { get => _s.OverlayOpacity; set { _s.OverlayOpacity = Math.Clamp(value, 0.3, 1); Save(); _services.RefreshOverlaySettings(); } }

    public bool AlertsEnabled { get => _s.AlertsEnabled; set { _s.AlertsEnabled = value; Save(); } }
    public List<int> CpuTempChoices { get; } = new() { 75, 80, 85, 90, 95 };
    public List<int> GpuTempChoices { get; } = new() { 75, 80, 85, 90 };
    public List<int> DriveTempChoices { get; } = new() { 55, 60, 65, 70, 75 };
    public List<int> BatteryChoices { get; } = new() { 5, 10, 15, 20, 30 };
    public int AlertCpuTempC { get => _s.AlertCpuTempC; set { _s.AlertCpuTempC = value; Save(); } }
    public int AlertGpuTempC { get => _s.AlertGpuTempC; set { _s.AlertGpuTempC = value; Save(); } }
    public int AlertDriveTempC { get => _s.AlertDriveTempC; set { _s.AlertDriveTempC = value; Save(); } }
    public int AlertBatteryPercent { get => _s.AlertBatteryPercent; set { _s.AlertBatteryPercent = value; Save(); } }

    private void RunSelfTest()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--selftest") { UseShellExecute = false });
            _ = Task.Run(() =>
            {
                p?.WaitForExit(180_000);
                var json = AppPaths.Writable("selftest.json");
                if (File.Exists(json)) Shell.Open(json);
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Error("Starting self-test", ex); }
    }
}
