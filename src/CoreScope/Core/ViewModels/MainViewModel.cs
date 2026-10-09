using System;
using CoreScope.Core.Fixes;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreScope.Core.Hardware;

namespace CoreScope.Core.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{

    private readonly SynchronizationContext _ui;
    private readonly SensorHub _hub = new();
    private readonly InsightEngine _engine = new();
    private IReadOnlyList<SensorReading> _latest = Array.Empty<SensorReading>();
    private int _uiUpdatePending;
    private DateTime _lastInsightRun = DateTime.MinValue;
    private SystemSpec? _spec;
    private bool _firstSnapshotSeen;
    private IReadOnlyList<SpdModuleTimings>? _spd;
    private bool _lifetimeAdded;

    private PageViewModel _selectedPage;
    private bool _isBusy = true;
    private string _statusText = "Scanning hardware…";
    private bool _showBanner;
    private string _bannerText = "";
    private string _bannerAction = "";
    private string? _bannerLink;

    public MainViewModel(SynchronizationContext ui, Action<string> copyToClipboard, Core.Control.IRadioService radios, IAppServices services)
    {
        _ui = ui;
        _services = services;
        _hub.IntervalMs = AppSettings.Current.RefreshIntervalMs;

        Overview = new OverviewViewModel();
        Insights = new InsightsViewModel();
        Upgrade = new UpgradeViewModel(copyToClipboard);
        Control = new ControlCenterViewModel(radios, _hub);
        Benchmark = new BenchmarkViewModel(() => System.IO.Path.GetTempPath());
        Sensors = new SensorsViewModel { IntervalMs = AppSettings.Current.RefreshIntervalMs };
        Appearance = new AppearanceViewModel(() => Pages?.Select(page => page.Title) ?? Enumerable.Empty<string>());
        Settings = new SettingsViewModel(services, Update);
        Settings.RefreshIntervalChanged += ms => { _hub.IntervalMs = ms; Sensors.IntervalMs = ms; };
        Processor = new SpecPageViewModel("Processor", "");
        Memory = new SpecPageViewModel("Memory", "");
        Graphics = new SpecPageViewModel("Graphics", "");
        Storage = new SpecPageViewModel("Storage", "");
        Motherboard = new SpecPageViewModel("Motherboard", "");
        SystemPage = new SpecPageViewModel("System", "");
        Devices = new DevicesViewModel();
        Network = new NetworkViewModel(ui);
        Network.FindingsChanged += RunInsights;
        Insights.DismissedChanged += RefreshOverviewInsights;
        Overview.RangeChanged += range => { foreach (var tile in _allTiles) tile.Range = range; };

        Pages = new ObservableCollection<PageViewModel>
        {
            Overview, Insights, Upgrade, Control, Network, Benchmark, Processor, Memory, Graphics, Storage, Motherboard, Devices, SystemPage, Sensors, Appearance, Settings,
        };
        _selectedPage = Overview;
        foreach (var p in new PageViewModel[] { Upgrade, Control, Network, Benchmark }) p.Group = "Tools";
        foreach (var p in new PageViewModel[] { Processor, Memory, Graphics, Storage, Motherboard, Devices, SystemPage }) p.Group = "Hardware";
        Sensors.Group = "Live";
        Appearance.Group = "_settings";
        Settings.Group = "_settings";
        Search = new SearchViewModel(this);

        _fixes = new FixCoordinator(new MainFixHost(this, services), ui, () => _ = RefreshHealthAsync(), Insights.Announce);
        InsightActions.Coordinator = _fixes;
        Insights.CheckupCommand = new RelayCommand(() => _ = RefreshHealthAsync());

        Overview.OpenInsightsCommand = new RelayCommand(() => SelectedPage = Insights);
        Overview.ExportReportCommand = new RelayCommand(ExportReport);
        Overview.CopySummaryCommand = new RelayCommand(() =>
        {
            if (_spec is null) return;
            _services.CopyText(Report.ToText(_spec));
            Overview.Toast("Summary copied to clipboard");
        });
        _alerts.Raised += a => _ui.Post(_ => _services.Notify(a.Title, a.Message), null);
        BannerCommand = new RelayCommand(OpenBannerLink);
        BuildTiles();
    }

    private readonly IAppServices _services;
    private readonly FixCoordinator _fixes;
    private HealthSnapshot? _health;
    private DateTime _lastHealth = DateTime.MinValue;
    private int _healthRunning;
    private readonly AlertMonitor _alerts = new();
    private readonly ProcessMonitor _processes = new();
    private bool _processSampling;
    private int _tick;
    private IReadOnlyList<Insight> _lastInsights = Array.Empty<Insight>();

    public BenchmarkViewModel Benchmark { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>Compact live metrics for the always-on-top overlay.</summary>
    public ObservableCollection<LiveTileViewModel> OverlayTiles { get; } = new();

    public void EmergencyRestoreFans() => _hub.EmergencyRestoreFans();

    private void ExportReport()
    {
        if (_spec is null) return;
        try
        {
            var html = Report.ToHtml(_spec, Volatile.Read(ref _latest), _lastInsights, Benchmark.Last);
            var name = $"CoreScope report {Environment.MachineName} {DateTime.Now:yyyy-MM-dd}.html";
            var path = _services.SaveFile(name, "Web page (*.html)|*.html", html);
            if (path is not null)
            {
                Overview.Toast("Report saved");
                Shell.Open(path);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Exporting report", ex);
            _services.Notify("CoreScope", $"Couldn't save the report: {ex.Message}");
        }
    }

    public ObservableCollection<PageViewModel> Pages { get; }
    public OverviewViewModel Overview { get; }
    public InsightsViewModel Insights { get; }
    public UpgradeViewModel Upgrade { get; }
    public ControlCenterViewModel Control { get; }
    public SpecPageViewModel Processor { get; }
    public SpecPageViewModel Memory { get; }
    public SpecPageViewModel Graphics { get; }
    public SpecPageViewModel Storage { get; }
    public SpecPageViewModel Motherboard { get; }
    public SpecPageViewModel SystemPage { get; }
    public SensorsViewModel Sensors { get; }
    public AppearanceViewModel Appearance { get; }
    public DevicesViewModel Devices { get; }
    public NetworkViewModel Network { get; }
    public UpdateViewModel Update { get; } = new();
    public SearchViewModel Search { get; }
    public RelayCommand BannerCommand { get; }

    public PageViewModel SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (value is null || !Set(ref _selectedPage, value)) return;
            if (value == Network) _ = Network.ScanAsync(); // refresh when the page opens
        }
    }

    public bool SelectPage(string title)
    {
        var page = Pages.FirstOrDefault(p => p.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
        if (page is null) return false;
        SelectedPage = page;
        return true;
    }

    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public bool ShowBanner { get => _showBanner; private set => Set(ref _showBanner, value); }
    public string BannerText { get => _bannerText; private set => Set(ref _bannerText, value); }
    public string BannerAction { get => _bannerAction; private set => Set(ref _bannerAction, value); }

    // ───────────────────────────── Startup ─────────────────────────────

    public async Task InitializeAsync()
    {
        _hub.Updated += OnSensorsUpdated;
        _hub.SpdRead += spd => _ui.Post(_ =>
        {
            _spd = spd;
            ApplyTimings();
        }, null);
        _hub.Start();
        _ = Network.ScanAsync();
        _ = Update.CheckOnStartupAsync();

        bool fromCache = ShowCachedScan();

        _spec = await Task.Run(SpecCollector.Collect);
        try
        {
            Populate(_spec);
        }
        catch (Exception ex)
        {
            Log.Error("Populating pages", ex);
        }
        ApplyTimings();
        SaveScanCache();
        if (fromCache) Log.Info("Fresh scan replaced the cached pages");
        IsBusy = false;
        StatusText = "Live";
        await Control.LoadAsync(_spec);
        UpdateBanner();
        RunInsights();
        _ = RefreshHealthAsync();
    }

    /// <summary>Re-reads the security, update, maintenance and startup state (a few seconds, off the UI thread) and re-evaluates every finding.</summary>
    public async Task RefreshHealthAsync()
    {
        if (Interlocked.Exchange(ref _healthRunning, 1) == 1) return;
        Insights.IsChecking = true;
        try
        {
            _health = await Task.Run(HealthCollector.Collect);
            _lastHealth = DateTime.Now;
            Insights.LastChecked = _lastHealth;
            RunInsights();
        }
        catch (Exception ex)
        {
            Log.Error("Health checkup", ex);
        }
        finally
        {
            Insights.IsChecking = false;
            Interlocked.Exchange(ref _healthRunning, 0);
        }
    }

    /// <summary>What fix buttons need from the app: dialogs, page navigation and unlocking full access.</summary>
    private sealed class MainFixHost : IFixHost
    {
        private readonly MainViewModel _main;
        private readonly IAppServices _services;

        public MainFixHost(MainViewModel main, IAppServices services)
        {
            _main = main;
            _services = services;
        }

        public bool Confirm(string title, string message, string yes) => _services.Confirm(title, message, yes);
        public void ShowGuide(Guide guide) => _services.ShowGuide(guide, _main._fixes.RunAction);
        public bool Navigate(string page)
        {
            var parts = page.Split('|', 2);
            if (!_main.SelectPage(parts[0])) return false;
            if (parts.Length > 1) _main.AnchorRequested?.Invoke(parts[1]);
            return true;
        }
        public void UnlockFullAccess()
        {
            if (_main._spec is { Os.IsAdmin: false }) _main.RestartElevatedRequested?.Invoke(_main.SelectedPage.Title);
        }
    }

    // ───────────────────────────── Live tiles ─────────────────────────────

    private LiveTileViewModel _cpuLoad = null!, _cpuTemp = null!, _cpuClock = null!, _cpuPower = null!;
    private LiveTileViewModel _network = null!, _battery = null!;
    private LiveTileViewModel _gpuLoad = null!, _gpuTemp = null!, _gpuClock = null!, _gpuVram = null!;
    private LiveTileViewModel _ramLoad = null!, _ramUsed = null!;
    private readonly List<LiveTileViewModel> _allTiles = new();

    private void BuildTiles()
    {
        LiveTileViewModel Tile(string label, Func<IReadOnlyList<SensorReading>, double?> pick, Func<double, string> format, double min = double.NaN, double max = double.NaN,
                               Func<string?>? detail = null, string? shortLabel = null)
        {
            var tile = new LiveTileViewModel(label, pick, format, min, max, detail, shortLabel);
            _allTiles.Add(tile);
            return tile;
        }

        _cpuLoad = Tile("CPU load", SensorPick.CpuLoad, v => $"{v:0} %", 0, 100, shortLabel: "CPU");
        _cpuTemp = Tile("CPU temperature", SensorPick.CpuTemperature, v => Format.Temperature(v), shortLabel: "CPU temp",
            detail: () => CpuTemperatureSource.Describe(Volatile.Read(ref _latest)));
        _cpuClock = Tile("Average core clock", SensorPick.CpuClock, v => Format.Mhz(v));
        _cpuPower = Tile("CPU package power", SensorPick.CpuPower, v => $"{v:0.0} W");
        _gpuLoad = Tile("GPU load", SensorPick.GpuLoad, v => $"{v:0} %", 0, 100, shortLabel: "GPU");
        _gpuTemp = Tile("GPU temperature", SensorPick.GpuTemperature, v => Format.Temperature(v), shortLabel: "GPU temp");
        _gpuClock = Tile("GPU core clock", SensorPick.GpuClock, v => Format.Mhz(v));
        _gpuVram = Tile("Video memory used", SensorPick.GpuMemoryUsedMb, v => $"{v / 1024:0.0} GB");
        _ramLoad = Tile("Memory in use", _ => SensorPick.MemoryLoad(), v => $"{v:0} %", 0, 100, shortLabel: "RAM");
        _ramUsed = Tile("Memory used", _ => SensorPick.MemoryUsedGb(), v => $"{v:0.0} GB");

        _network = Tile("Network ↓", s =>
        {
            var down = s.Where(r => r.Kind == "Network" && r.Type == "Throughput" && r.Name.Contains("Download", StringComparison.OrdinalIgnoreCase) && r.Value.HasValue)
                        .Select(r => r.Value!.Value).ToList();
            return down.Count > 0 ? down.Sum() : null;
        }, v => $"{Format.Bytes(v)}/s");
        _battery = Tile("Battery", _ => Native.GetPowerStatus()?.Percent, v => $"{v:0} %", 0, 100, detail: () =>
        {
            var p = Native.GetPowerStatus();
            return p is null ? null : p.Value.PluggedIn ? "Plugged in" : p.Value.SecondsLeft > 0 ? $"{TimeSpan.FromSeconds(p.Value.SecondsLeft):h\\:mm} left" : "On battery";
        });

        foreach (var t in new[] { _cpuLoad, _cpuTemp, _gpuLoad, _ramLoad, _network }) Overview.Tiles.Add(t);
        foreach (var t in new[] { _cpuLoad, _cpuTemp, _gpuLoad, _gpuTemp, _ramLoad }) OverlayTiles.Add(t);
        foreach (var t in new[] { _cpuLoad, _cpuClock, _cpuTemp, _cpuPower }) Processor.Tiles.Add(t);
        foreach (var t in new[] { _gpuLoad, _gpuClock, _gpuTemp, _gpuVram }) Graphics.Tiles.Add(t);
        foreach (var t in new[] { _ramLoad, _ramUsed }) Memory.Tiles.Add(t);
    }

    /// <summary>Called on the sensor thread. Coalesces updates so a slow UI never builds a backlog.</summary>
    private void OnSensorsUpdated(IReadOnlyList<SensorReading> readings)
    {
        Volatile.Write(ref _latest, readings);
        if (Interlocked.CompareExchange(ref _uiUpdatePending, 1, 0) != 0) return;
        _ui.Post(_ =>
        {
            Interlocked.Exchange(ref _uiUpdatePending, 0);
            ApplySensors(Volatile.Read(ref _latest));
        }, null);
    }

    private void ApplySensors(IReadOnlyList<SensorReading> readings)
    {
        try
        {
            Sensors.Update(readings);
            foreach (var tile in _allTiles) tile.Update(readings);
            _engine.Observe(readings);
            _alerts.Observe(readings, AppSettings.Current);
            if (++_tick % 2 == 0 && !_processSampling) _ = SampleProcessesAsync();
            AddStorageLifetime(readings);

            if (!_firstSnapshotSeen)
            {
                _firstSnapshotSeen = true;
                UpdateBanner();
                if (_hub.LastError is { } error) Sensors.Status = $"Sensor engine error: {error}";
            }

            if (!IsBusy) StatusText = $"Live · {DateTime.Now:HH:mm:ss}";
            if ((DateTime.Now - _lastInsightRun).TotalSeconds >= 5) RunInsights();
            if ((DateTime.Now - _lastHealth).TotalMinutes >= 10) _ = RefreshHealthAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Applying sensor snapshot", ex);
        }
    }

    // ───────────────────────────── Insights & banner ─────────────────────────────

    private void RunInsights()
    {
        if (_spec is null) return;
        _lastInsightRun = DateTime.Now;
        try
        {
            // Hardware/system findings plus the network checks, most severe first (stable within a severity).
            var insights = _engine.Evaluate(_spec, _health)
                .Concat(Network.LatestFindings)
                .Select((insight, index) => (insight, index))
                .OrderBy(x => x.insight.Severity).ThenBy(x => x.index)
                .Select(x => x.insight).ToList();
            _lastInsights = insights;
            if (!Insights.Apply(insights)) return;
            RefreshOverviewInsights();
        }
        catch (Exception ex)
        {
            Log.Error("Evaluating insights", ex);
        }
    }

    private void RefreshOverviewInsights()
    {
        try
        {
            Overview.TopInsights.Clear();
            foreach (var i in Insights.Active.Where(i => i.Severity != Severity.Good).Take(3)) Overview.TopInsights.Add(i);
            Overview.HealthSummary = Insights.Summary;
            Overview.ShowAllClear = Overview.TopInsights.Count == 0;
        }
        catch (Exception ex)
        {
            Log.Error("Evaluating insights", ex);
        }
    }

    private void UpdateBanner()
    {
        if (_spec is null) return;
        if (!_spec.Os.IsAdmin)
        {
            BannerText = "Limited mode: drive health (SMART), the TPM and a few controls need administrator access.";
            BannerAction = "Unlock full sensors";
            _bannerLink = null;
            ShowBanner = true;
        }
        else
        {
            ShowBanner = false;
        }
    }

    /// <summary>Raised after a fix button navigated to a page with a named section to scroll to.</summary>
    public event Action<string>? AnchorRequested;

    /// <summary>Raised when the user asks for full sensors; the app restarts itself elevated (UAC prompt).</summary>
    public event Action<string>? RestartElevatedRequested;

    private void OpenBannerLink()
    {
        if (_bannerLink is null)
        {
            if (_spec is { Os.IsAdmin: false }) RestartElevatedRequested?.Invoke(SelectedPage.Title);
            return;
        }
        try { Process.Start(new ProcessStartInfo(_bannerLink) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Error("Opening link", ex); }
    }

    // ───────────────────────────── Page population ─────────────────────────────

    private static bool IsPlaceholder(string value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Contains("To be filled", StringComparison.OrdinalIgnoreCase)
        || value.Contains("System Product Name", StringComparison.OrdinalIgnoreCase)
        || value.Contains("System manufacturer", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Default string", StringComparison.OrdinalIgnoreCase)
        || value.Equals("O.E.M.", StringComparison.OrdinalIgnoreCase);

    private IEnumerable<SpecPageViewModel> SpecPages => Pages.OfType<SpecPageViewModel>();

    /// <summary>Fills every spec page from the previous scan so the app is usable immediately.</summary>
    private bool ShowCachedScan()
    {
        var cache = SpecCache.Load();
        if (cache is null) return false;
        foreach (var page in SpecPages)
        {
            if (!cache.Pages.TryGetValue(page.Title, out var cached)) continue;
            page.Headline = cached.Headline;
            page.SubHeadline = cached.SubHeadline;
            page.SetSections(cached.Sections.Select(c => c.ToSection()));
        }
        Overview.MachineName = cache.MachineName;
        Overview.MachineDetail = cache.MachineDetail;
        StatusText = "Showing last scan · refreshing…";
        Log.Info($"Showing cached scan from {cache.Saved:yyyy-MM-dd HH:mm}");
        return true;
    }

    private void SaveScanCache()
    {
        try
        {
            var cache = new SpecCache { MachineName = Overview.MachineName, MachineDetail = Overview.MachineDetail };
            foreach (var page in SpecPages)
                cache.Pages[page.Title] = new SpecCache.CachedPage
                {
                    Headline = page.Headline,
                    SubHeadline = page.SubHeadline,
                    Sections = page.Sections.Select(SpecCache.CachedSection.From).ToList(),
                };
            cache.Save();
        }
        catch (Exception ex)
        {
            Log.Error("Caching scan", ex);
        }
    }

    private void Populate(SystemSpec spec)
    {
        var cpu = spec.Cpu;
        Processor.Headline = cpu.Name;
        var coreText = cpu.IsHybrid
            ? $"{cpu.Cores} cores ({cpu.PerformanceCores}P + {cpu.EfficiencyCores}E) · {cpu.Threads} threads"
            : $"{cpu.Cores} cores · {cpu.Threads} threads";
        Processor.SubHeadline = cpu.BaseClockMhz > 0 ? $"{coreText} · {Format.Mhz(cpu.BaseClockMhz)} base" : coreText;
        Processor.SetSections(spec.CpuSections);

        Memory.Headline = $"{Format.Bytes(spec.TotalRamBytes)} {spec.MemoryTypeSummary}".Trim();
        Memory.SubHeadline = spec.Modules.Count switch
        {
            0 => "Module details unavailable",
            1 => "1 module installed",
            var n => $"{n} modules installed",
        };
        Memory.SetSections(spec.MemorySections);
        try
        {
            var ram = SlotMap.Memory(spec);
            int freeSlots = ram.Count(r => r.State == SlotState.Empty);
            Memory.SetSlots(ram.All(r => r.State == SlotState.Soldered) ? "Memory layout · all soldered"
                : freeSlots > 0 ? $"Memory slots · {freeSlots} free" : "Memory slots · all in use", ram);
            var (m2, note) = SlotMap.M2(spec);
            Storage.SetSlots(m2.Count > 0 ? "M.2 slots" : "", m2, note);
        }
        catch (Exception ex) { Log.Error("Slot diagrams", ex); }

        var primaryGpu = spec.Gpus.FirstOrDefault();
        Graphics.Headline = primaryGpu?.Name ?? "No graphics adapter found";
        Graphics.SubHeadline = primaryGpu is null ? "" : string.Join(" · ", new[]
        {
            primaryGpu.VramBytes > 0 ? $"{Format.Bytes(primaryGpu.VramBytes)} VRAM" : null,
            primaryGpu.DriverVersion.Length > 0 ? $"Driver {primaryGpu.DriverVersion}" : null,
            spec.Gpus.Count > 1 ? $"+{spec.Gpus.Count - 1} more adapter(s)" : null,
        }.Where(s => s is not null));
        Graphics.SetSections(spec.GpuSections);

        var totalDisk = spec.Disks.Aggregate(0UL, (sum, d) => sum + d.SizeBytes);
        Storage.Headline = spec.Disks.Count switch
        {
            0 => "No drives found",
            1 => spec.Disks[0].Name,
            var n => $"{n} drives · {Format.Bytes(totalDisk)} total",
        };
        var free = spec.Volumes.Sum(v => v.FreeBytes);
        var total = spec.Volumes.Sum(v => v.TotalBytes);
        Storage.SubHeadline = total > 0 ? $"{Format.Bytes(free)} free of {Format.Bytes(total)} across {spec.Volumes.Count} volume(s)" : "";
        Storage.SetSections(spec.StorageSections);

        var b = spec.Board;
        Motherboard.Headline = $"{b.BoardVendor} {b.BoardModel}".Trim();
        Motherboard.SubHeadline = $"{b.FirmwareType} firmware {b.BiosVersion}".Trim();
        Motherboard.SetSections(spec.BoardSections);

        SystemPage.Headline = $"{spec.Os.Name} {spec.Os.DisplayVersion}".Trim();
        SystemPage.SubHeadline = $"Build {spec.Os.Build} · {spec.Os.ComputerName}";
        SystemPage.SetSections(spec.SystemSections);

        if (spec.Upgrade is { } upgrade) Upgrade.Load(upgrade);
        Devices.Load(spec.Devices);
        if (spec.Battery is not null && Native.GetPowerStatus() is not null) Overview.Tiles.Insert(4, _battery);

        // Overview
        Overview.MachineName = spec.Upgrade is { MachineName.Length: > 0 } report ? report.MachineName
            : !IsPlaceholder(b.SystemModel)
            ? $"{(IsPlaceholder(b.SystemVendor) ? "" : b.SystemVendor + " ")}{b.SystemModel}"
            : $"Custom PC · {b.BoardVendor} {b.BoardModel}".Trim();
        Overview.MachineDetail = $"{spec.Os.ComputerName} · {spec.Os.Name} {spec.Os.DisplayVersion}".Trim(' ', '·');

        void Nav(PageViewModel page) => SelectedPage = page;
        Overview.Cards.Clear();
        Overview.Cards.Add(new SummaryCardViewModel(Processor.Glyph, "Processor", cpu.Name, Processor.SubHeadline, Processor, Nav));
        Overview.Cards.Add(new SummaryCardViewModel(Graphics.Glyph, "Graphics", Graphics.Headline, Graphics.SubHeadline, Graphics, Nav));
        Overview.Cards.Add(new SummaryCardViewModel(Memory.Glyph, "Memory", Memory.Headline, Memory.SubHeadline, Memory, Nav));
        Overview.Cards.Add(new SummaryCardViewModel(Storage.Glyph, "Storage", Storage.Headline, Storage.SubHeadline, Storage, Nav));
        Overview.Cards.Add(new SummaryCardViewModel(Motherboard.Glyph, "Motherboard", Motherboard.Headline, Motherboard.SubHeadline, Motherboard, Nav));
        Overview.Cards.Add(new SummaryCardViewModel(SystemPage.Glyph, "Windows", SystemPage.Headline, SystemPage.SubHeadline, SystemPage, Nav));
    }

    private async System.Threading.Tasks.Task SampleProcessesAsync()
    {
        _processSampling = true;
        try
        {
            var (cpu, mem) = await System.Threading.Tasks.Task.Run(() => _processes.Sample());
            Overview.SetProcesses(cpu, mem);
        }
        catch (Exception ex) { Log.Error("Sampling processes", ex); }
        finally { _processSampling = false; }
    }

    /// <summary>Swaps the Memory page's "Timings" card once SPD data (or its absence) is known.</summary>
    private void ApplyTimings()
    {
        if (_spec is null || _spd is null) return;
        try
        {
            var section = SpecCollector.BuildTimingsSection(_spec, _spd);
            for (int i = 0; i < Memory.Sections.Count; i++)
            {
                if (Memory.Sections[i].Title != "Timings") continue;
                Memory.Sections[i] = section;
                return;
            }
            Memory.Sections.Insert(Math.Min(1, Memory.Sections.Count), section);
        }
        catch (Exception ex)
        {
            Log.Error("Applying SPD timings", ex);
        }
    }

    /// <summary>Adds the SMART lifetime card (TB written, power cycles…) to the Storage page once.</summary>
    private void AddStorageLifetime(IReadOnlyList<SensorReading> readings)
    {
        if (_lifetimeAdded || _spec is null || Storage.Sections.Count == 0) return;
        _lifetimeAdded = true;
        var health = SpecCollector.BuildStorageLifetimeSections(readings, _spec);
        if (health.Count == 0) { _lifetimeAdded = false; return; } // sensors not ready yet: try again next tick
        foreach (var old in Storage.Sections.Where(s => s.Title.StartsWith("Health · ", StringComparison.Ordinal)).ToList()) Storage.Sections.Remove(old);
        var volumesIndex = Storage.Sections.ToList().FindIndex(s => s.Title == "Volumes");
        int at = volumesIndex >= 0 ? volumesIndex : Storage.Sections.Count;
        foreach (var section in health) Storage.Sections.Insert(at++, section);
        SaveScanCache();
    }

    public void Dispose()
    {
        Sensors.StopRecording();
        _hub.Updated -= OnSensorsUpdated;
        _hub.Dispose();
    }
}
