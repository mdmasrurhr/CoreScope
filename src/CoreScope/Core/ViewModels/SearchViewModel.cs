using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CoreScope.Core.Hardware;

namespace CoreScope.Core.ViewModels;

public sealed record SearchResult(string Title, string Path, string Value, PageViewModel Page, int Score);

/// <summary>
/// Ctrl+K search across everything CoreScope shows: page names, every spec row, insights, devices,
/// upgrade notes, network details and settings. Every word typed must match (in any order).
/// </summary>
public sealed class SearchViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _query = "";
    private bool _isOpen;
    private SearchResult? _selected;

    public SearchViewModel(MainViewModel main)
    {
        _main = main;
        OpenCommand = new RelayCommand(() => { Query = ""; IsOpen = true; Refresh(); });
        CloseCommand = new RelayCommand(() => IsOpen = false);
        GoCommand = new RelayCommand(p => Go(p as SearchResult ?? Selected ?? Results.FirstOrDefault()));
    }

    public RelayCommand OpenCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand GoCommand { get; }
    public ObservableCollection<SearchResult> Results { get; } = new();

    public bool IsOpen { get => _isOpen; set => Set(ref _isOpen, value); }
    public SearchResult? Selected { get => _selected; set => Set(ref _selected, value); }
    public bool HasNoResults => Query.Trim().Length > 0 && Results.Count == 0;

    public string Query
    {
        get => _query;
        set { if (Set(ref _query, value)) Refresh(); }
    }

    /// <summary>Settings and actions people look for by name, mapped to the page that has them.</summary>
    private IEnumerable<(string Title, string Path, string Value, PageViewModel Page)> Shortcuts()
    {
        var s = _main.Settings;
        yield return ("Start with Windows", "Settings › General", "startup tray", s);
        yield return ("Always-on-top overlay", "Settings › Overlay", "fps game hud", s);
        yield return ("Temperature alerts", "Settings › Alerts", "notification warning hot", s);
        yield return ("Check for updates", "Settings › Updates", "version upgrade new", s);
        yield return ("Fahrenheit / Celsius", "Settings › General", "units temperature", s);
        yield return ("Speed test", "Network", "internet download ping mbps slow wifi", _main.Network);
        yield return ("Wi-Fi analyzer", "Network", "channel nearby networks router congestion", _main.Network);
        yield return ("RSC / auto-tuning fixes", "Network › Advanced fixes", "slow internet realtek tcp", _main.Network);
        yield return ("Run benchmark", "Benchmark", "score speed test cpu memory disk", _main.Benchmark);
        yield return ("Brightness, resolution, refresh rate", "Control › Display", "screen monitor hz", _main.Control);
        yield return ("Power mode", "Control › Power & performance", "battery performance boost plan", _main.Control);
        yield return ("Battery conservation / report", "Control › Battery care", "charge limit health wear", _main.Control);
        yield return ("Wi-Fi and Bluetooth switches", "Control › Wireless & sound", "radio volume mute", _main.Control);
        yield return ("Fan control", "Control › Fans", "fan curve rpm noise", _main.Control);
        yield return ("Startup apps", "Control › Startup apps", "boot slow login", _main.Control);
        yield return ("Record sensors to CSV", "Sensors", "log export excel", _main.Sensors);
        yield return ("Export report", "Overview", "html share copy summary", _main.Overview);
    }

    private IEnumerable<(string Title, string Path, string Value, PageViewModel Page)> Index()
    {
        foreach (var page in _main.Pages)
            yield return (page.Title, "Page", "", page);

        foreach (var page in _main.Pages.OfType<SpecPageViewModel>())
            foreach (var section in page.Sections)
                foreach (var row in section.Rows)
                    yield return (row.Label, $"{page.Title} › {section.Title}", row.Value, page);

        foreach (var insight in _main.Insights.Items)
            yield return (insight.Title, $"Insights › {insight.Category}", insight.Detail, _main.Insights);

        foreach (var category in _main.Devices.Categories)
            foreach (var device in category.Devices)
                yield return (device.Name, $"Devices › {category.Title}", device.Manufacturer, _main.Devices);

        foreach (var item in _main.Upgrade.Items)
        {
            yield return (item.Component, "Upgrade", item.Summary, _main.Upgrade);
            foreach (var row in item.Current.Concat(item.Buy))
                yield return (row.Label, $"Upgrade › {item.Component}", row.Value, _main.Upgrade);
        }

        foreach (var row in _main.Network.Connection)
            yield return (row.Label, "Network › Your connection", row.Value, _main.Network);

        foreach (var group in _main.Sensors.Groups)
            foreach (var sensor in group.Sensors)
                yield return (sensor.Name, $"Sensors › {group.Name}", $"{sensor.Type} {sensor.Value}", _main.Sensors);

        foreach (var shortcut in Shortcuts()) yield return shortcut;
    }

    private void Refresh()
    {
        Results.Clear();
        var words = Query.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            // Empty box: offer the pages so the palette doubles as quick navigation.
            foreach (var page in _main.Pages) Results.Add(new SearchResult(page.Title, "Page", "", page, 0));
        }
        else
        {
            var hits = new List<SearchResult>();
            var seen = new HashSet<string>();
            foreach (var (title, path, value, page) in Index())
            {
                var haystack = $"{title} {path} {value}".ToLowerInvariant();
                if (!words.All(haystack.Contains)) continue;
                var t = title.ToLowerInvariant();
                int score = words.Sum(w => t.StartsWith(w, StringComparison.Ordinal) ? 30 : t.Contains(w, StringComparison.Ordinal) ? 20 : path.Contains(w, StringComparison.OrdinalIgnoreCase) ? 8 : 4);
                if (path == "Page") score += 25;
                if (seen.Add($"{path}|{title}|{value}")) hits.Add(new SearchResult(title, path, value, page, score));
            }
            foreach (var hit in hits.OrderByDescending(h => h.Score).ThenBy(h => h.Title.Length).Take(40)) Results.Add(hit);
        }
        Selected = Results.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoResults));
    }

    private void Go(SearchResult? result)
    {
        if (result is null) return;
        IsOpen = false;
        _main.SelectedPage = result.Page;
    }

    /// <summary>Arrow keys in the search box move the selection.</summary>
    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        int i = Selected is null ? -1 : Results.IndexOf(Selected);
        Selected = Results[Math.Clamp(i + delta, 0, Results.Count - 1)];
    }
}
