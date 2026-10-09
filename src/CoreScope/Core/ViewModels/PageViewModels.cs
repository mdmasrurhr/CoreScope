using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CoreScope.Core.Hardware;

namespace CoreScope.Core.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    private string? _badge;

    protected PageViewModel(string title, string glyph)
    {
        Title = title;
        Glyph = glyph;
    }

    public string Title { get; }

    /// <summary>Segoe Fluent Icons / MDL2 code point.</summary>
    public string Glyph { get; }

    /// <summary>Sidebar section this page is listed under. Names starting with "_" show no header.</summary>
    public string Group { get; set; } = "_main";

    /// <summary>Small count shown next to the nav item (e.g. number of warnings).</summary>
    public string? Badge { get => _badge; set => Set(ref _badge, value); }
}

/// <summary>A live metric card: big number plus a sparkline over a selectable time range (1 min … 24 h).</summary>
public sealed class LiveTileViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<SensorReading>, double?> _selector;
    private readonly Func<double, string> _format;
    private readonly HistoryBuffer _history = new();
    private string _valueText = "—";
    private double[] _data = Array.Empty<double>();
    private bool _hasData;
    private HistoryRange _range = HistoryRange.OneMinute;
    private DateTime _lastRedraw;

    private readonly Func<string?>? _detail;
    private string _detailText = "";

    public LiveTileViewModel(string label, Func<IReadOnlyList<SensorReading>, double?> selector, Func<double, string> format,
                             double minimum = double.NaN, double maximum = double.NaN, Func<string?>? detail = null, string? shortLabel = null)
    {
        _detail = detail;
        ShortLabel = shortLabel ?? label;
        Label = label;
        _selector = selector;
        _format = format;
        Minimum = minimum;
        Maximum = maximum;
    }

    public string Label { get; }
    public double Minimum { get; }

    /// <summary>Compact label for the overlay ("CPU", "GPU °C").</summary>
    public string ShortLabel { get; }

    /// <summary>Optional context shown next to the label (e.g. battery state).</summary>
    public string DetailText { get => _detailText; private set => Set(ref _detailText, value); }
    public double Maximum { get; }
    public string ValueText { get => _valueText; private set => Set(ref _valueText, value); }
    public double[] Data { get => _data; private set => Set(ref _data, value); }
    public bool HasData { get => _hasData; private set => Set(ref _hasData, value); }

    /// <summary>Formats a value for the graph's hover tooltip.</summary>
    public Func<double, string> Formatter => _format;
    public int ChartCapacity => HistoryBuffer.Capacity(_range, AppSettings.Current.RefreshIntervalMs);
    public double SpanSeconds => HistoryBuffer.Window(_range).TotalSeconds;

    public HistoryRange Range
    {
        get => _range;
        set
        {
            if (!Set(ref _range, value)) return;
            OnPropertyChanged(nameof(ChartCapacity));
            OnPropertyChanged(nameof(SpanSeconds));
            Data = _history.Get(_range);
        }
    }

    public void Update(IReadOnlyList<SensorReading> readings)
    {
        double? value;
        try { value = _selector(readings); }
        catch (Exception ex) { Log.Error($"Tile '{Label}'", ex); value = null; }

        if (value is not { } v || double.IsNaN(v)) return;
        HasData = true;
        ValueText = _format(v);
        if (_detail is not null) DetailText = _detail() ?? "";
        _history.Add(v);
        // Long ranges change slowly: redraw them every 10 s instead of every tick.
        if (_range <= HistoryRange.FiveMinutes || DateTime.Now - _lastRedraw > TimeSpan.FromSeconds(10))
        {
            _lastRedraw = DateTime.Now;
            Data = _history.Get(_range);
        }
    }
}

public sealed class SpecPageViewModel : PageViewModel
{
    private string _headline = "";
    private string _subHeadline = "";

    public SpecPageViewModel(string title, string glyph) : base(title, glyph) { }

    private bool _isLoading = true;

    public string Headline { get => _headline; set => Set(ref _headline, value); }
    public string SubHeadline { get => _subHeadline; set => Set(ref _subHeadline, value); }
    public ObservableCollection<SpecSection> Sections { get; } = new();
    public ObservableCollection<LiveTileViewModel> Tiles { get; } = new();

    /// <summary>Optional slot diagram (Memory: RAM slots, Storage: M.2 slots).</summary>
    public ObservableCollection<SlotVisual> Slots { get; } = new();
    private string _slotsTitle = "", _slotsNote = "";
    public string SlotsTitle { get => _slotsTitle; set => Set(ref _slotsTitle, value); }
    public string SlotsNote { get => _slotsNote; set => Set(ref _slotsNote, value); }

    public void SetSlots(string title, IEnumerable<SlotVisual> slots, string? note = null)
    {
        Slots.Clear();
        foreach (var s in slots) Slots.Add(s);
        SlotsTitle = title;
        SlotsNote = note ?? "";
    }

    /// <summary>True until the hardware scan has delivered this page's data (drives the skeleton).</summary>
    public bool IsLoading { get => _isLoading; private set { if (Set(ref _isLoading, value)) OnPropertyChanged(nameof(IsEmpty)); } }

    /// <summary>Scan finished but produced nothing to show (drives the empty state).</summary>
    public bool IsEmpty => !IsLoading && Sections.Count == 0;

    public void SetSections(IEnumerable<SpecSection> sections)
    {
        Sections.Clear();
        foreach (var s in sections.Where(s => s.Rows.Count > 0 || s.Tags.Count > 0)) Sections.Add(s);
        IsLoading = false;
        OnPropertyChanged(nameof(IsEmpty));
    }
}

public sealed class SummaryCardViewModel
{
    public SummaryCardViewModel(string glyph, string label, string title, string detail, PageViewModel target, Action<PageViewModel> navigate)
    {
        Glyph = glyph;
        Label = label;
        Title = string.IsNullOrWhiteSpace(title) ? "Unknown" : title;
        Detail = detail;
        OpenCommand = new RelayCommand(() => navigate(target));
    }

    public string Glyph { get; }
    public string Label { get; }
    public string Title { get; }
    public string Detail { get; }
    public RelayCommand OpenCommand { get; }
}

public sealed class OverviewViewModel : PageViewModel
{
    private string _machineName = "Scanning your hardware…";
    private string _machineDetail = "";
    private string _healthSummary = "Analyzing…";
    private bool _showAllClear;

    public OverviewViewModel() : base("Overview", "") { }

    private ChoiceOption<HistoryRange> _range = RangeChoices[0];

    public static List<ChoiceOption<HistoryRange>> RangeChoices { get; } = new()
    {
        new("1 min", HistoryRange.OneMinute), new("5 min", HistoryRange.FiveMinutes),
        new("1 hour", HistoryRange.OneHour), new("24 hours", HistoryRange.OneDay),
    };
    public List<ChoiceOption<HistoryRange>> RangeOptions => RangeChoices;

    /// <summary>Time span shown by every live graph (tiles on all pages).</summary>
    public ChoiceOption<HistoryRange> SelectedRange
    {
        get => _range;
        set { if (value is not null && Set(ref _range, value)) RangeChanged?.Invoke(value.Value); }
    }

    public event Action<HistoryRange>? RangeChanged;

    public string MachineName { get => _machineName; set => Set(ref _machineName, value); }
    public string MachineDetail { get => _machineDetail; set => Set(ref _machineDetail, value); }
    public string HealthSummary { get => _healthSummary; set => Set(ref _healthSummary, value); }
    public bool ShowAllClear { get => _showAllClear; set => Set(ref _showAllClear, value); }
    public ObservableCollection<SummaryCardViewModel> Cards { get; } = new();
    public ObservableCollection<LiveTileViewModel> Tiles { get; } = new();
    public ObservableCollection<Insight> TopInsights { get; } = new();
    public RelayCommand? OpenInsightsCommand { get; set; }
    public RelayCommand? ExportReportCommand { get; set; }
    public RelayCommand? CopySummaryCommand { get; set; }

    public ObservableCollection<ProcessUsage> TopCpu { get; } = new();
    public ObservableCollection<ProcessUsage> TopMemory { get; } = new();

    public void SetProcesses(IReadOnlyList<ProcessUsage> cpu, IReadOnlyList<ProcessUsage> memory)
    {
        Replace(TopCpu, cpu);
        Replace(TopMemory, memory);
    }

    private static void Replace(ObservableCollection<ProcessUsage> target, IReadOnlyList<ProcessUsage> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (i < target.Count) target[i] = items[i];
            else target.Add(items[i]);
        }
        while (target.Count > items.Count) target.RemoveAt(target.Count - 1);
    }

    private string _toastText = "";
    private bool _showToast;
    private int _toastGeneration;
    public string ToastText { get => _toastText; private set => Set(ref _toastText, value); }
    public bool ShowToast { get => _showToast; private set => Set(ref _showToast, value); }

    public async void Toast(string text)
    {
        int generation = ++_toastGeneration;
        ToastText = text;
        ShowToast = false;
        ShowToast = true;
        await System.Threading.Tasks.Task.Delay(2500);
        if (generation == _toastGeneration) ShowToast = false;
    }
}

/// <summary>One health area on the Insights page (Performance, Storage, …) with a 0–100 score.</summary>
public sealed class InsightArea : ObservableObject
{
    private bool _isSelected;

    public InsightArea(string name, string glyph, int score, int issues, int total, Action<InsightArea> select)
    {
        Name = name;
        Glyph = glyph;
        Score = score;
        Issues = issues;
        Total = total;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public string Name { get; }
    public string Glyph { get; }
    public int Score { get; }
    public int Issues { get; }
    public int Total { get; }
    /// <summary>Good / Warning / Critical — drives the color.</summary>
    public string Level => Score >= 85 ? "Good" : Score >= 60 ? "Warning" : "Critical";
    public string ScoreText => Name == "All" ? $"{Issues}" : $"{Score}";
    public string Caption => Name == "All" ? (Issues == 1 ? "issue" : "issues") : Issues == 0 ? "no issues" : Issues == 1 ? "1 issue" : $"{Issues} issues";
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public RelayCommand SelectCommand { get; }
}

public sealed class InsightsViewModel : PageViewModel
{
    private string _summary = "Analyzing…";
    private int _issueCount;
    private string _signature = "";
    private IReadOnlyList<Insight> _all = Array.Empty<Insight>();
    private string _area = "All";
    private bool _showHidden;
    private int _hiddenCount;

    public InsightsViewModel() : base("Insights", "")
    {
        ToggleHiddenCommand = new RelayCommand(() => { _showHidden = !_showHidden; OnPropertyChanged(nameof(HiddenText)); Rebuild(); });
        InsightActions.Dismissed += _ => Rebuild(force: true);
    }

    private bool _isChecking;
    private DateTime? _lastChecked;
    private string _announcement = "";
    private bool _announcementOk;

    public ObservableCollection<Insight> Items { get; } = new();
    public ObservableCollection<InsightArea> Areas { get; } = new();

    /// <summary>"Run full checkup": re-reads security, updates, maintenance and startup state.</summary>
    public RelayCommand? CheckupCommand { get; set; }
    public bool IsChecking { get => _isChecking; set { if (Set(ref _isChecking, value)) { OnPropertyChanged(nameof(IsNotChecking)); OnPropertyChanged(nameof(CheckupStatus)); } } }
    public bool IsNotChecking => !_isChecking;
    public DateTime? LastChecked { get => _lastChecked; set { if (Set(ref _lastChecked, value)) OnPropertyChanged(nameof(CheckupStatus)); } }
    public string CheckupStatus => _isChecking ? "Checking your PC…" : _lastChecked is { } t ? $"Last checked {t:HH:mm}. Checks repeat every 10 minutes." : "Not checked yet.";

    /// <summary>The newest fix result, shown as a line at the top so it isn't lost when the finding disappears.</summary>
    public string Announcement { get => _announcement; private set { if (Set(ref _announcement, value)) OnPropertyChanged(nameof(HasAnnouncement)); } }
    public bool AnnouncementOk { get => _announcementOk; private set => Set(ref _announcementOk, value); }
    public bool HasAnnouncement => _announcement.Length > 0;
    public void Announce(string message, bool ok) { AnnouncementOk = ok; Announcement = message; }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public int IssueCount { get => _issueCount; private set => Set(ref _issueCount, value); }
    public RelayCommand ToggleHiddenCommand { get; }
    public int HiddenCount { get => _hiddenCount; private set { if (Set(ref _hiddenCount, value)) OnPropertyChanged(nameof(HiddenText)); } }
    public string HiddenText => _showHidden ? "Hide dismissed findings" : $"Show {HiddenCount} dismissed finding{(HiddenCount == 1 ? "" : "s")}";
    public bool IsEmptyFilter => _all.Count > 0 && Items.Count == 0;

    /// <summary>Findings that aren't dismissed — what the Overview and the badge use.</summary>
    public IReadOnlyList<Insight> Active => _all.Where(i => !InsightActions.IsDismissed(i)).ToList();

    public static string AreaOf(Insight i) => i.Category switch
    {
        "Processor" or "Memory" or "Graphics" => "Performance",
        "Storage" => "Storage",
        "Security" => "Security",
        "Battery" => "Battery",
        "Network" => "Network",
        "Startup" or "Performance" => "Performance",
        _ => "System",
    };

    private static string AreaGlyph(string area) => area switch
    {
        "Performance" => "\uE9D9", "Storage" => "\uEDA2", "Security" => "\uE72E", "Battery" => "\uE83F",
        "Network" => "\uE701", "System" => "\uE7F8", _ => "\uE80F",
    };

    /// <summary>
    /// Starts at 100: −25 per critical, −10 per warning, −1 per tip with tips capped at −10 in total, so a long list of
    /// small suggestions never makes a healthy PC look broken.
    /// </summary>
    public static int Score(IEnumerable<Insight> items)
    {
        var list = items.ToList();
        var penalty = 25 * list.Count(i => i.Severity == Severity.Critical)
                    + 10 * list.Count(i => i.Severity == Severity.Warning)
                    + Math.Min(10, list.Count(i => i.Severity == Severity.Info));
        return Math.Max(0, 100 - penalty);
    }

    /// <summary>Replaces the list only when the findings actually changed, so the page doesn't flicker.</summary>
    public bool Apply(IReadOnlyList<Insight> insights)
    {
        var signature = string.Join("|", insights.Select(i => $"{i.Severity}:{i.Title}:{i.AllFixes.Count}"));
        if (signature == _signature) return false;
        _signature = signature;
        _all = insights;
        Rebuild(force: true);
        return true;
    }

    private void SelectArea(InsightArea area)
    {
        _area = area.Name;
        Rebuild(force: true);
    }

    private void Rebuild(bool force = false)
    {
        var active = Active;
        HiddenCount = _all.Count - active.Count;

        // Area tiles: "All" plus every area that has findings.
        Areas.Clear();
        int allIssues = active.Count(i => i.Severity <= Severity.Warning);
        Areas.Add(new InsightArea("All", "\uE80F", Score(active), allIssues, active.Count, SelectArea) { IsSelected = _area == "All" });
        foreach (var g in active.GroupBy(AreaOf).OrderBy(g => g.Key))
            Areas.Add(new InsightArea(g.Key, AreaGlyph(g.Key), Score(g), g.Count(i => i.Severity <= Severity.Warning), g.Count(), SelectArea) { IsSelected = _area == g.Key });
        if (!Areas.Any(a => a.IsSelected)) { _area = "All"; Areas[0].IsSelected = true; }

        var source = _showHidden ? _all : active;
        Items.Clear();
        foreach (var i in source.Where(i => _area == "All" || AreaOf(i) == _area)) Items.Add(i);
        OnPropertyChanged(nameof(IsEmptyFilter));

        int critical = active.Count(i => i.Severity == Severity.Critical);
        int warnings = active.Count(i => i.Severity == Severity.Warning);
        int good = active.Count(i => i.Severity == Severity.Good);
        int tips = active.Count(i => i.Severity == Severity.Info);
        IssueCount = critical + warnings;
        Badge = IssueCount > 0 ? IssueCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

        var parts = new List<string>();
        if (critical > 0) parts.Add($"{critical} critical");
        if (warnings > 0) parts.Add($"{warnings} warning{(warnings == 1 ? "" : "s")}");
        if (tips > 0) parts.Add($"{tips} tip{(tips == 1 ? "" : "s")}");
        if (good > 0) parts.Add($"{good} check{(good == 1 ? "" : "s")} passed");
        Summary = parts.Count > 0 ? $"Health score {Score(active)} / 100 · " + string.Join(" · ", parts) : "No findings yet";
        if (force) DismissedChanged?.Invoke();
    }

    /// <summary>Lets the Overview refresh its top findings after a dismiss/restore.</summary>
    public event Action? DismissedChanged;
}

public sealed class UpgradeViewModel : PageViewModel
{
    private readonly Action<string> _copyToClipboard;
    private string _machineName = "Checking upgrade options…";
    private string _machineDetail = "";
    private string _shoppingList = "";
    private string _copyStatus = "";
    private bool _showCopyToast;
    private int _toastGeneration;

    public UpgradeViewModel(Action<string> copyToClipboard) : base("Upgrade", "")
    {
        _copyToClipboard = copyToClipboard;
        CopyCommand = new RelayCommand(Copy);
    }

    public string MachineName { get => _machineName; private set => Set(ref _machineName, value); }
    public string MachineDetail { get => _machineDetail; private set => Set(ref _machineDetail, value); }
    public string CopyStatus { get => _copyStatus; private set => Set(ref _copyStatus, value); }

    /// <summary>Drives the "Copied" toast animation; auto-resets after a short delay.</summary>
    public bool ShowCopyToast { get => _showCopyToast; private set => Set(ref _showCopyToast, value); }
    public ObservableCollection<UpgradeItem> Items { get; } = new();
    public ObservableCollection<UpgradeLink> Links { get; } = new();
    public RelayCommand CopyCommand { get; }

    public void Load(UpgradeReport report)
    {
        MachineName = report.MachineName;
        MachineDetail = report.MachineDetail;
        _shoppingList = report.ShoppingList;
        Items.Clear();
        // Most actionable first (upgradeable, partial, check manually, soldered); OrderBy is stable within a status.
        foreach (var item in report.Items.OrderBy(i => (int)i.Status)) Items.Add(item);
        Links.Clear();
        foreach (var link in report.Links) Links.Add(link);
    }

    private void Copy()
    {
        if (_shoppingList.Length == 0) return;
        try
        {
            _copyToClipboard(_shoppingList);
            CopyStatus = "Shopping list copied to clipboard";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            Log.Error("Copying shopping list", ex);
            CopyStatus = "Couldn't access the clipboard. Try again.";
        }
        _ = FlashToastAsync();
    }

    private async System.Threading.Tasks.Task FlashToastAsync()
    {
        int generation = ++_toastGeneration;
        ShowCopyToast = false; // restart the animation if clicked twice
        ShowCopyToast = true;
        await System.Threading.Tasks.Task.Delay(2500);
        if (generation == _toastGeneration) ShowCopyToast = false;
    }
}

public sealed class DevicesViewModel : PageViewModel
{
    private string _summary = "Scanning devices…";
    private bool _isLoading = true;

    public DevicesViewModel() : base("Devices", "") { }

    public ObservableCollection<DeviceCategory> Categories { get; } = new();
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    public void Load(IReadOnlyList<DeviceCategory> categories)
    {
        Categories.Clear();
        foreach (var c in categories) Categories.Add(c);
        int total = categories.Where(c => c.Title != "Needs attention").Sum(c => c.Devices.Count);
        int problems = categories.FirstOrDefault(c => c.Title == "Needs attention")?.Devices.Count ?? 0;
        Badge = problems > 0 ? problems.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        Summary = problems > 0
            ? $"{total} devices · {problems} need{(problems == 1 ? "s" : "")} attention"
            : $"{total} devices · all working";
        IsLoading = false;
    }
}
