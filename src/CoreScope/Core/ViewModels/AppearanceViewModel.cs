using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using CoreScope.Core.Theming;

namespace CoreScope.Core.ViewModels;

/// <summary>One accent colour in the picker.</summary>
public sealed class AccentSwatch : ObservableObject
{
    private bool _isSelected;

    public AccentSwatch(string name, string hex)
    {
        Name = name;
        Hex = hex;
        var brush = new SolidColorBrush(ThemeColors.TryParse(hex, out var c) ? c : Colors.Gray);
        brush.Freeze();
        Brush = brush;
    }

    public string Name { get; }
    public string Hex { get; }
    public Brush Brush { get; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>A skin card in the picker, with its selection state.</summary>
public sealed class SkinCard : ObservableObject
{
    private bool _isSelected;

    public SkinCard(Skin skin)
    {
        Skin = skin;
        var surface = Make(skin.PreviewSurface);
        Background = Make(skin.PreviewBackground);
        Surface = surface;
        Accent = Make(skin.Accent ?? "#0078D4");
        Sidebar = Make(skin.Mode == "Dark" ? "#16161A" : "#FAFAFA");
    }

    public Skin Skin { get; }
    public string Name => Skin.Name;
    public string Description => Skin.Description;
    public string Layout => Skin.Layout;
    /// <summary>Miniature preview colours: page, panel surface, accent, navigation.</summary>
    public Brush Background { get; }
    public Brush Surface { get; }
    public Brush Accent { get; }
    public Brush Sidebar { get; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    private static Brush Make(string hex)
    {
        var brush = new SolidColorBrush(ThemeColors.TryParse(hex, out var c) ? c : Colors.Gray);
        brush.Freeze();
        return brush;
    }
}

/// <summary>A saved theme in the "My themes" list.</summary>
public sealed record SavedThemeItem(string Name, ICommand ApplyCommand, ICommand DeleteCommand);

/// <summary>A window-layout option (Sidebar, Rail, TopBar) with a one-line description.</summary>
public sealed class LayoutCard : ObservableObject
{
    private bool _isSelected;

    public LayoutCard(string id, string name, string description)
    {
        Id = id;
        Name = name;
        Description = description;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string Layout => Id;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    // Neutral colours for the miniature preview (layouts are independent of the skin colours).
    public Brush Background { get; } = Neutral("#E4E8F0");
    public Brush Surface { get; } = Neutral("#FFFFFF");
    public Brush Accent { get; } = Neutral("#0078D4");
    public Brush Sidebar { get; } = Neutral("#F7F8FA");

    private static Brush Neutral(string hex)
    {
        var brush = new SolidColorBrush(ThemeColors.TryParse(hex, out var c) ? c : Colors.Gray);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Appearance page: theme mode, skins, layout, accent colour, transparency, tint, glow, font, shape and spacing, each with
/// the parts of the UI it applies to. Edits apply live (<see cref="Changed"/>) and are saved shortly after the last change.
/// </summary>
public sealed class AppearanceViewModel : PageViewModel
{
    private static readonly string[] FontCandidates =
    {
        "Segoe UI Variable", "Segoe UI", "Bahnschrift", "Calibri", "Candara", "Trebuchet MS", "Verdana", "Georgia",
        "Cascadia Mono", "Consolas",
    };

    private readonly AppSettings _s = AppSettings.Current;
    private CancellationTokenSource? _saveCts;
    private string _accentText;
    private string? _accentError;

    private readonly Func<IEnumerable<string>> _pageTitles;
    private List<string>? _pageChoices;
    private string _accentPage = "Overview";
    private string _shareStatus = "";
    private string _newThemeName = "";
    private string _lookPage = "Overview";

    public AppearanceViewModel(Func<IEnumerable<string>> pageTitles) : base("Appearance", "")
    {
        _pageTitles = pageTitles;
        Fonts = FontCandidates.Where(IsInstalled).ToList();
        if (!Fonts.Contains(_s.FontName)) Fonts.Add(_s.FontName);
        Skins = ThemeCatalog.Skins.Select(s => new SkinCard(s)).ToList();
        Layouts = new List<LayoutCard>
        {
            new("Sidebar", "Sidebar", "Labelled navigation down the left."),
            new("Rail", "Compact rail", "Slim icon bar. Most room for content."),
            new("TopBar", "Top tabs", "Navigation across the top, content below."),
        };
        _accentText = _s.AccentHex ?? "";
        SelectSkinCommand = new RelayCommand(p => { if (p is SkinCard card) ApplySkin(card.Skin); });
        SelectLayoutCommand = new RelayCommand(p => { if (p is LayoutCard card) { _s.Layout = card.Id; Touched(); } });
        SelectAccentCommand = new RelayCommand(p => { if (p is AccentSwatch sw) SetAccent(sw.Hex); });
        SetPageAccentCommand = new RelayCommand(p =>
        {
            if (p is not AccentSwatch sw) return;
            _s.PageAccents[AccentPage] = sw.Hex;
            Touched();
        });
        ClearPageAccentCommand = new RelayCommand(() => { _s.PageAccents.Remove(AccentPage); Touched(); });
        SaveThemeCommand = new RelayCommand(SaveCurrent);
        foreach (var swatch in Swatches) PageSwatches.Add(new AccentSwatch(swatch.Name, swatch.Hex));
        UseWindowsAccentCommand = new RelayCommand(() => SetAccent(null));
        ResetSkinCommand = new RelayCommand(() => ApplySkin(ThemeCatalog.Find(_s.SkinId) ?? ThemeCatalog.Skins[0]));
        ResetAllCommand = new RelayCommand(() => ApplySkin(ThemeCatalog.Skins[0]));
        RefreshSelection();
    }

    private static bool IsInstalled(string family) =>
        System.Windows.Media.Fonts.SystemFontFamilies.Any(f => f.Source.Equals(family, StringComparison.OrdinalIgnoreCase));

    /// <summary>Raised on the UI thread after any change, so the theme can be re-applied live.</summary>
    public event Action? Changed;

    // ───────────── Pickers ─────────────

    public List<SkinCard> Skins { get; }
    public List<LayoutCard> Layouts { get; }
    public List<string> Fonts { get; }

    public List<AccentSwatch> Swatches { get; } = new()
    {
        new("Azure", "#0078D4"), new("Violet", "#7C6CFF"), new("Pink", "#E5489B"), new("Red", "#E5484D"),
        new("Copper", "#FF7A45"), new("Amber", "#F5A623"), new("Lime", "#8BC34A"), new("Green", "#2EBD59"),
        new("Teal", "#13C8B4"), new("Cyan", "#22D3EE"), new("Indigo", "#4F46E5"), new("Slate", "#64748B"),
    };

    public List<ChoiceOption<string>> BackdropOptions { get; } = new()
    {
        new("Mica (soft, follows your wallpaper)", "Mica"), new("Acrylic (frosted glass)", "Acrylic"), new("None (solid colour)", "None"),
    };

    public List<ChoiceOption<int>> TextSizeOptions { get; } = new()
    {
        new("Small (90%)", 90), new("Default (100%)", 100), new("Large (110%)", 110), new("Extra large (120%)", 120),
    };

    public ICommand SelectSkinCommand { get; }
    public ICommand SelectLayoutCommand { get; }
    public ICommand SelectAccentCommand { get; }
    public ICommand UseWindowsAccentCommand { get; }
    public ICommand SetPageAccentCommand { get; }
    public ICommand ClearPageAccentCommand { get; }
    public ICommand SaveThemeCommand { get; }
    public ICommand ResetSkinCommand { get; }
    public ICommand ResetAllCommand { get; }

    // ───────────── Mode ─────────────

    public bool ModeSystem { get => _s.ThemeMode == "System"; set { if (value) SetMode("System"); } }
    public bool ModeLight { get => _s.ThemeMode == "Light"; set { if (value) SetMode("Light"); } }
    public bool ModeDark { get => _s.ThemeMode == "Dark"; set { if (value) SetMode("Dark"); } }

    private void SetMode(string mode)
    {
        if (_s.ThemeMode == mode) return;
        _s.ThemeMode = mode;
        _s.ModeBeforeSkin = null; // an explicit choice always wins over the remembered mode
        Touched();
    }

    // ───────────── Accent ─────────────

    public string AccentText
    {
        get => _accentText;
        set
        {
            _accentText = value;
            if (string.IsNullOrWhiteSpace(value)) { SetAccent(null); return; }
            if (ThemeColors.TryParse(value, out var c)) SetAccent(ThemeColors.ToHex(c), fromText: true);
            else { AccentError = "Use a colour like #7C6CFF"; OnPropertyChanged(); }
        }
    }

    public string? AccentError { get => _accentError; private set { if (Set(ref _accentError, value)) OnPropertyChanged(nameof(HasAccentError)); } }
    public bool HasAccentError => _accentError is not null;
    public string AccentSummary => _s.AccentHex is { } hex ? hex : "Windows accent colour";

    private void SetAccent(string? hex, bool fromText = false)
    {
        _s.AccentHex = hex;
        AccentError = null;
        if (!fromText) { _accentText = hex ?? ""; }
        Touched();
    }

    // ───────────── Per-page accent ─────────────

    public bool PageAccentsEnabled { get => _s.PageAccentsEnabled; set { _s.PageAccentsEnabled = value; Touched(); } }

    public List<string> PageChoices => _pageChoices ??= _pageTitles().ToList();

    public string AccentPage
    {
        get => _accentPage;
        set
        {
            if (value is null) return;
            _accentPage = value;
            RefreshSelection();
            OnPropertyChanged(string.Empty);
        }
    }

    public string PageAccentSummary =>
        _s.PageAccents.TryGetValue(AccentPage, out var hex) ? $"{AccentPage}: {hex}"
        : ThemeCatalog.PageAccent(_s, AccentPage) is { } auto ? $"{AccentPage}: automatic ({auto})"
        : $"{AccentPage}: global accent";

    /// <summary>Swatches for the selected page (separate instances: their ring follows that page, not the global accent).</summary>
    public List<AccentSwatch> PageSwatches { get; } = new();

    // ───────────── My themes ─────────────

    public string NewThemeName { get => _newThemeName; set => Set(ref _newThemeName, value); }

    public List<SavedThemeItem> SavedThemeItems =>
        _s.SavedThemes.Keys.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .Select(n => new SavedThemeItem(n, new RelayCommand(() => ApplySaved(n)), new RelayCommand(() => DeleteSaved(n)))).ToList();

    public bool HasSavedThemes => _s.SavedThemes.Count > 0;

    private void SaveCurrent()
    {
        if (!ThemeLooks.TrySave(_s, NewThemeName, out var error)) { ShareStatus = error; return; }
        ShareStatus = $"Saved \"{NewThemeName.Trim()}\".";
        NewThemeName = "";
        Commit();
    }

    private void ApplySaved(string name)
    {
        if (!ThemeLooks.TryApply(_s, name, out var error)) { ShareStatus = error; return; }
        _accentText = _s.AccentHex ?? "";
        ShareStatus = $"Applied \"{name}\".";
        Commit();
    }

    private void DeleteSaved(string name)
    {
        ThemeLooks.Delete(_s, name);
        ShareStatus = $"Deleted \"{name}\".";
        Commit();
    }

    // ───────────── Looks per page ─────────────

    public string LookPage
    {
        get => _lookPage;
        set { if (value is null) return; _lookPage = value; OnPropertyChanged(string.Empty); }
    }

    public List<LookOption> LookChoices => ThemeLooks.Choices(_s);

    public LookOption SelectedLook
    {
        get
        {
            var id = ThemeLooks.LookFor(_s, LookPage) ?? ThemeLooks.None;
            return LookChoices.FirstOrDefault(o => o.Id == id) ?? LookChoices[0];
        }
        set
        {
            if (value is null) return;
            foreach (var key in _s.PageLooks.Keys.Where(k => k.Equals(LookPage, StringComparison.OrdinalIgnoreCase)).ToList()) _s.PageLooks.Remove(key);
            if (value.Id != ThemeLooks.None) _s.PageLooks[LookPage] = value.Id;
            Commit(); // not "Touched": the main look is unchanged
        }
    }

    public string LookSummary => _s.PageLooks.Count == 0
        ? "No page has its own look."
        : "Pages with their own look: " + string.Join(", ", _s.PageLooks.Keys.OrderBy(k => k));

    // ───────────── Share (export / import) ─────────────

    public string ShareStatus { get => _shareStatus; private set => Set(ref _shareStatus, value); }

    public string ExportJson() => ThemeFile.Export(_s);

    /// <summary>Applies a theme file's contents. On failure nothing changes and <see cref="ShareStatus"/> says why.</summary>
    public bool Import(string json)
    {
        if (!ThemeFile.TryImport(json, _s, out var error)) { ShareStatus = error; return false; }
        _accentText = _s.AccentHex ?? "";
        ShareStatus = "Theme imported.";
        Commit();
        return true;
    }

    public void ReportShare(string message) => ShareStatus = message;

    // ───────────── Backdrop and transparency ─────────────

    public ChoiceOption<string> Backdrop
    {
        get => BackdropOptions.First(o => o.Value == _s.Backdrop);
        set { if (value is null) return; _s.Backdrop = value.Value; Touched(); }
    }

    public bool HasBackdrop => _s.Backdrop != "None";
    public double Transparency { get => _s.Transparency; set { _s.Transparency = (int)Math.Round(value); Touched(); } }
    public bool TransparencyWindow { get => _s.TransparencyWindow; set { _s.TransparencyWindow = value; Touched(); } }
    public bool TransparencySidebar { get => _s.TransparencySidebar; set { _s.TransparencySidebar = value; Touched(); } }
    public bool TransparencyCards { get => _s.TransparencyCards; set { _s.TransparencyCards = value; Touched(); } }

    // ───────────── Tint ─────────────

    public double Tint { get => _s.Tint; set { _s.Tint = (int)Math.Round(value); Touched(); } }
    public bool TintWindow { get => _s.TintWindow; set { _s.TintWindow = value; Touched(); } }
    public bool TintSidebar { get => _s.TintSidebar; set { _s.TintSidebar = value; Touched(); } }
    public bool TintCards { get => _s.TintCards; set { _s.TintCards = value; Touched(); } }
    public bool TintTitleBar { get => _s.TintTitleBar; set { _s.TintTitleBar = value; Touched(); } }

    // ───────────── Glow ─────────────

    public double Glow { get => _s.Glow; set { _s.Glow = (int)Math.Round(value); Touched(); } }
    public bool GlowAccent { get => _s.GlowAccent; set { _s.GlowAccent = value; Touched(); } }
    public bool GlowCards { get => _s.GlowCards; set { _s.GlowCards = value; Touched(); } }
    public bool GlowHeadings { get => _s.GlowHeadings; set { _s.GlowHeadings = value; Touched(); } }

    // ───────────── Type, shape, spacing ─────────────

    public string FontName { get => _s.FontName; set { if (value is null) return; _s.FontName = value; Touched(); } }

    public ChoiceOption<int> TextSize
    {
        get => TextSizeOptions.FirstOrDefault(o => o.Value == _s.TextScalePercent) ?? TextSizeOptions[1];
        set { if (value is null) return; _s.TextScalePercent = value.Value; Touched(); }
    }

    public double CornerRadius { get => _s.CornerRadius; set { _s.CornerRadius = (int)Math.Round(value); Touched(); } }
    public bool CompactSpacing { get => _s.CompactSpacing; set { _s.CompactSpacing = value; Touched(); } }
    public bool KeepLayoutOnAllPages { get => _s.KeepLayoutOnAllPages; set { _s.KeepLayoutOnAllPages = value; Touched(); } }

    public string CurrentSkinName => _s.SkinId == ThemeCatalog.CustomId ? "Custom" : ThemeCatalog.Find(_s.SkinId)?.Name ?? "Custom";

    // ───────────── Plumbing ─────────────

    private void ApplySkin(Skin skin)
    {
        ThemeCatalog.Apply(skin, _s);
        _accentText = _s.AccentHex ?? "";
        AccentError = null;
        Commit();
    }

    /// <summary>A control was edited by hand: the look is now "custom" (unless it only changed the skin).</summary>
    private void Touched()
    {
        _s.SkinId = ThemeCatalog.CustomId;
        Commit();
    }

    private void Commit()
    {
        RefreshSelection();
        OnPropertyChanged(string.Empty); // every binding re-reads (skin changes touch almost everything)
        Changed?.Invoke();
        _ = SaveSoonAsync();
    }

    private void RefreshSelection()
    {
        foreach (var skin in Skins) skin.IsSelected = skin.Skin.Id == _s.SkinId;
        foreach (var layout in Layouts) layout.IsSelected = layout.Id == _s.Layout;
        foreach (var swatch in PageSwatches)
            swatch.IsSelected = string.Equals(swatch.Hex, _s.PageAccents.GetValueOrDefault(AccentPage), StringComparison.OrdinalIgnoreCase);
        foreach (var swatch in Swatches) swatch.IsSelected = string.Equals(swatch.Hex, _s.AccentHex, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Waits for a quiet moment (slider drags fire many changes), then writes settings.json once.</summary>
    private async Task SaveSoonAsync()
    {
        _saveCts?.Cancel();
        var cts = _saveCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(400, cts.Token);
            _s.Save();
        }
        catch (TaskCanceledException) { /* a newer change replaced this save */ }
    }
}
