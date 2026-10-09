using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using CoreScope.Core;
using CoreScope.Core.Theming;
using Microsoft.Win32;

namespace CoreScope.Platform;

/// <summary>
/// Turns <see cref="AppSettings"/> into live application resources. Every themed value in XAML is a DynamicResource, so
/// replacing an entry here restyles the whole UI at once, with no restart. Called at startup and after any Appearance change.
/// </summary>
internal static class ThemeManager
{
    public static bool IsDark { get; private set; }
    public static Color CaptionColor { get; private set; }
    public static Color CaptionText { get; private set; }

    /// <summary>Title of the page on screen; with page colours on, its accent replaces the global one.</summary>
    public static string? CurrentPage { get; set; }

    /// <summary>Raised after resources were replaced (the window then re-applies backdrop and layout).</summary>
    public static event Action? Applied;

    /// <summary>The settings in force right now: the saved ones, or a copy carrying the current page's look.</summary>
    public static AppSettings Effective { get; private set; } = AppSettings.Current;

    public static void Apply(AppSettings saved)
    {
        var app = Application.Current;
        if (app is null) return;
        var s = Effective = ThemeLooks.Resolve(saved, CurrentPage);
        var res = app.Resources;

        var mode = s.ThemeMode switch { "Light" => ThemeMode.Light, "Dark" => ThemeMode.Dark, _ => ThemeMode.System };
        if (app.ThemeMode != mode) app.ThemeMode = mode;
        IsDark = s.ThemeMode == "Dark" || (s.ThemeMode != "Light" && SystemPrefersDark());

        var accentHex = ThemeCatalog.PageAccent(s, CurrentPage) ?? s.AccentHex;
        var accent = ThemeColors.TryParse(accentHex, out var custom) ? custom : SystemColors.AccentColor;
        ApplyAccent(res, accent);
        ApplySurfaces(res, s, accent);
        ApplyEffects(res, s, accent);
        ApplyTypography(res, s);
        ApplyShape(res, s);
        res["NavLabelVis"] = s.Layout == "Rail" ? Visibility.Collapsed : Visibility.Visible;
        res["NavItemMinWidth"] = s.Layout == "Sidebar" ? 176.0 : 0.0;
        res["NavBadgeMaxWidth"] = s.Layout == "Rail" ? 0.0 : 96.0; // the count would overlap the icon in the slim rail
        res["NavBadgeMinWidth"] = s.Layout == "Rail" ? 0.0 : 24.0;

        Applied?.Invoke();
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }

    private static SolidColorBrush Solid(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ───────────────────────── Accent ─────────────────────────

    /// <summary>
    /// Fluent bakes the Windows accent into dozens of per-control brushes (CheckBox, Slider, ToggleSwitch, ProgressBar, focus borders…)
    /// instead of referencing one key. Find every brush that equals one of the theme's accent shades and swap in the matching shade of
    /// the chosen accent. The shades are read from the theme before it is changed, so this works on every re-apply.
    /// </summary>
    private static void RecolorFluent(ResourceDictionary res, Color accent, Color hover, Color pressed)
    {
        Color? Read(string key)
        {
            foreach (var dictionary in AllDictionaries(res))
                if (dictionary.Contains(key) && dictionary[key] is SolidColorBrush brush) return brush.Color;
            return null;
        }

        var map = new System.Collections.Generic.List<(Color From, Color To)>();
        void Add(string key, Color to) { if (Read(key) is { } from && !map.Any(m => SameRgb(m.From, from))) map.Add((from, to)); }
        Add("AccentFillColorDefaultBrush", accent);
        Add("AccentFillColorSecondaryBrush", hover);
        Add("AccentFillColorTertiaryBrush", pressed);
        Add("SystemFillColorAttentionBrush", accent);
        if (map.Count == 0) return;

        foreach (var dictionary in AllDictionaries(res))
        {
            if (dictionary.IsReadOnly) continue;
            foreach (var key in dictionary.Keys.Cast<object>().ToList())
            {
                if (dictionary[key] is not SolidColorBrush brush) continue;
                foreach (var (from, to) in map)
                {
                    if (!SameRgb(brush.Color, from)) continue;
                    var replacement = new SolidColorBrush(Color.FromArgb(brush.Color.A, to.R, to.G, to.B)) { Opacity = brush.Opacity };
                    replacement.Freeze();
                    dictionary[key] = replacement;
                    break;
                }
            }
        }
    }

    private static bool SameRgb(Color a, Color b) => a.R == b.R && a.G == b.G && a.B == b.B;

    private static System.Collections.Generic.IEnumerable<ResourceDictionary> AllDictionaries(ResourceDictionary root)
    {
        foreach (var merged in root.MergedDictionaries)
        {
            yield return merged;
            foreach (var nested in AllDictionaries(merged)) yield return nested;
        }
    }

    /// <summary>
    /// Sets a resource at application level and also inside the Fluent theme dictionaries that define it: controls such as
    /// CheckBox and Slider resolve their accent from the theme dictionary itself and would otherwise keep the Windows accent.
    /// </summary>
    private static void Put(ResourceDictionary res, object key, object value)
    {
        res[key] = value;
        Overwrite(res.MergedDictionaries, key, value);
    }

    private static void Overwrite(System.Collections.Generic.IEnumerable<ResourceDictionary> dictionaries, object key, object value)
    {
        foreach (var dictionary in dictionaries)
        {
            if (dictionary.Contains(key) && !dictionary.IsReadOnly) dictionary[key] = value;
            Overwrite(dictionary.MergedDictionaries, key, value);
        }
    }

    private static void ApplyAccent(ResourceDictionary res, Color accent)
    {
        var towardsEarly = IsDark ? Colors.White : Colors.Black;
        RecolorFluent(res, accent, ThemeColors.Lerp(accent, towardsEarly, 0.12), ThemeColors.Lerp(accent, towardsEarly, 0.24));
        var towards = IsDark ? Colors.White : Colors.Black;
        var hover = ThemeColors.Lerp(accent, towards, 0.12);
        var pressed = ThemeColors.Lerp(accent, towards, 0.24);
        Put(res, "SystemAccentColor", accent);
        Put(res, "SystemAccentColorLight1", ThemeColors.Lerp(accent, Colors.White, 0.15));
        Put(res, "SystemAccentColorLight2", ThemeColors.Lerp(accent, Colors.White, 0.30));
        Put(res, "SystemAccentColorLight3", ThemeColors.Lerp(accent, Colors.White, 0.45));
        Put(res, "SystemAccentColorDark1", ThemeColors.Lerp(accent, Colors.Black, 0.15));
        Put(res, "SystemAccentColorDark2", ThemeColors.Lerp(accent, Colors.Black, 0.30));
        Put(res, "SystemAccentColorDark3", ThemeColors.Lerp(accent, Colors.Black, 0.45));
        // The keys Fluent's CheckBox, RadioButton and Slider templates actually resolve (WPF 9+ system accent keys).
        var light1 = ThemeColors.Lerp(accent, Colors.White, 0.15);
        var light2 = ThemeColors.Lerp(accent, Colors.White, 0.30);
        var light3 = ThemeColors.Lerp(accent, Colors.White, 0.45);
        var dark1 = ThemeColors.Lerp(accent, Colors.Black, 0.15);
        var dark2 = ThemeColors.Lerp(accent, Colors.Black, 0.30);
        var dark3 = ThemeColors.Lerp(accent, Colors.Black, 0.45);
        Put(res, SystemColors.AccentColorKey, accent);
        Put(res, SystemColors.AccentColorBrushKey, Solid(accent));
        Put(res, SystemColors.AccentColorLight1Key, light1);
        Put(res, SystemColors.AccentColorLight1BrushKey, Solid(light1));
        Put(res, SystemColors.AccentColorLight2Key, light2);
        Put(res, SystemColors.AccentColorLight2BrushKey, Solid(light2));
        Put(res, SystemColors.AccentColorLight3Key, light3);
        Put(res, SystemColors.AccentColorLight3BrushKey, Solid(light3));
        Put(res, SystemColors.AccentColorDark1Key, dark1);
        Put(res, SystemColors.AccentColorDark1BrushKey, Solid(dark1));
        Put(res, SystemColors.AccentColorDark2Key, dark2);
        Put(res, SystemColors.AccentColorDark2BrushKey, Solid(dark2));
        Put(res, SystemColors.AccentColorDark3Key, dark3);
        Put(res, SystemColors.AccentColorDark3BrushKey, Solid(dark3));
        Put(res, "AccentFillColorDefaultBrush", Solid(accent));
        // Checkboxes, radio buttons and sliders read these other accent keys.
        Put(res, "AccentFillColorPrimary", accent);
        Put(res, "AccentFillColorPrimaryBrush", Solid(accent));
        Put(res, "SystemAccentColorSecondary", accent);
        Put(res, "AccentFillColorSelectedTextBackgroundBrush", Solid(accent));
        Put(res, "AccentDefault", accent);
        Put(res, "AccentDefaultBrush", Solid(accent));
        Put(res, "AccentSecondary", hover);
        Put(res, "AccentSecondaryBrush", Solid(ThemeColors.WithAlpha(hover, 0.9)));
        Put(res, "AccentTertiary", pressed);
        Put(res, "AccentTertiaryBrush", Solid(ThemeColors.WithAlpha(pressed, 0.8)));
        Put(res, "AccentControlElevationBorderBrush", Solid(ThemeColors.Lerp(accent, Colors.Black, 0.12)));
        Put(res, "AccentFillColorSecondaryBrush", Solid(ThemeColors.WithAlpha(hover, 0.9)));
        Put(res, "AccentFillColorTertiaryBrush", Solid(ThemeColors.WithAlpha(pressed, 0.8)));
        Put(res, "AccentOn", Solid(ThemeColors.OnColor(accent)));
        Put(res, "AccentHover", Solid(hover));
        Put(res, "AccentPressed", Solid(pressed));
        Put(res, "AccentWash", Solid(ThemeColors.WithAlpha(accent, IsDark ? 0.28 : 0.2)));
        // Accent used as a text colour must stay readable on the page surface.
        Put(res, "AccentText", Solid(ThemeColors.ReadableOn(accent, IsDark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3))));
    }

    // ───────────────────────── Surfaces ─────────────────────────

    private static void ApplySurfaces(ResourceDictionary res, AppSettings s, Color accent)
    {
        double t = Math.Clamp(s.Transparency, 0, 100) / 100.0;
        double tint = Math.Clamp(s.Tint, 0, 100) / 100.0;
        bool hasBackdrop = s.Backdrop != "None";

        var windowBase = IsDark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
        var sidebarBase = IsDark ? Color.FromRgb(0x1B, 0x1B, 0x1B) : Color.FromRgb(0xF7, 0xF7, 0xF7);
        var cardBase = IsDark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White;

        // Window: solid base (optionally tinted or a gradient). With a backdrop its alpha lets Mica or Acrylic show through.
        var win = s.TintWindow ? ThemeColors.Lerp(windowBase, accent, tint * 0.35) : windowBase;
        double winAlpha = hasBackdrop && s.TransparencyWindow ? 1 - t : 1;
        Brush window;
        if (ThemeColors.TryParse(s.BackgroundFromHex, out var from) && ThemeColors.TryParse(s.BackgroundToHex, out var to))
        {
            var g = new LinearGradientBrush(AdaptStop(windowBase, from), AdaptStop(windowBase, to), 35)
            { Opacity = hasBackdrop && s.TransparencyWindow ? Math.Max(0.35, 1 - t * 0.6) : 1 };
            g.Freeze();
            window = g;
        }
        else
        {
            window = Solid(ThemeColors.WithAlpha(win, winAlpha));
        }
        res["WindowBase"] = window;

        var side = s.TintSidebar ? ThemeColors.Lerp(sidebarBase, accent, tint * 0.3) : sidebarBase;
        res["SidebarFill"] = Solid(ThemeColors.WithAlpha(side, s.TransparencySidebar ? 1 - 0.75 * t : 1));

        var card = s.TintCards ? ThemeColors.Lerp(cardBase, accent, tint * 0.25) : cardBase;
        double cardAlpha = s.TransparencyCards ? 1 - 0.55 * t : 1;
        res["CardFill"] = Solid(ThemeColors.WithAlpha(card, cardAlpha));
        res["CardFillSubtle"] = Solid(ThemeColors.WithAlpha(IsDark ? Colors.White : Colors.Black, IsDark ? 0.05 : 0.04));

        // Solid-looking cards get a crisp hairline; glass cards get the fading highlight edge.
        res["CardStroke"] = cardAlpha >= 0.95
            ? Solid(IsDark ? Color.FromRgb(0x3C, 0x3C, 0x3C) : Color.FromRgb(0xD8, 0xD8, 0xD8))
            : res["GlassEdge"];

        // Title bar (Windows caption).
        var cap = s.TintTitleBar ? ThemeColors.Lerp(windowBase, accent, 0.2 + tint * 0.3) : windowBase;
        CaptionColor = cap;
        CaptionText = ThemeColors.OnColor(cap);
    }

    /// <summary>
    /// Mixes a skin's gradient colour into the window base, as strongly as it can while body text stays at least 9:1 against it.
    /// A dark skin gradient in light mode (or a bright one in dark mode) is therefore softened instead of turning muddy.
    /// </summary>
    private static Color AdaptStop(Color windowBase, Color stop)
    {
        var text = IsDark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A);
        double strength = 0.5;
        var mixed = ThemeColors.Lerp(windowBase, stop, strength);
        while (strength > 0.08 && ThemeColors.Contrast(mixed, text) < 9)
        {
            strength -= 0.06;
            mixed = ThemeColors.Lerp(windowBase, stop, strength);
        }
        return mixed;
    }

    // ───────────────────────── Glow ─────────────────────────

    private static DropShadowEffect? Glow(Color color, int glow, double scale = 1)
    {
        if (glow <= 0) return null;
        double g = Math.Clamp(glow, 0, 100) / 100.0;
        var e = new DropShadowEffect
        {
            Color = color,
            ShadowDepth = 0,
            BlurRadius = (6 + 28 * g) * scale,
            Opacity = 0.25 + 0.7 * g,
        };
        e.Freeze();
        return e;
    }

    private static void ApplyEffects(ResourceDictionary res, AppSettings s, Color accent)
    {
        var glowColor = IsDark ? accent : ThemeColors.Lerp(accent, Colors.Black, 0.1);
        res["AccentGlowEffect"] = s.GlowAccent ? Glow(glowColor, s.Glow) : null;
        res["CardGlowEffect"] = s.GlowCards ? Glow(glowColor, (int)(s.Glow * 0.6), 0.9) : null;
        res["TextGlowEffect"] = s.GlowHeadings ? Glow(glowColor, s.Glow, 0.5) : null;
    }

    // ───────────────────────── Type, shape, spacing ─────────────────────────

    private static void ApplyTypography(ResourceDictionary res, AppSettings s)
    {
        var name = Fonts.SystemFontFamilies.Any(f => f.Source.Equals(s.FontName, StringComparison.OrdinalIgnoreCase)) ? s.FontName : "Segoe UI";
        bool variable = name.Equals("Segoe UI Variable", StringComparison.OrdinalIgnoreCase);
        res["UiFont"] = new FontFamily(variable ? "Segoe UI Variable Text, Segoe UI" : $"{name}, Segoe UI");
        res["UiFontDisplay"] = new FontFamily(variable ? "Segoe UI Variable Display, Segoe UI" : $"{name}, Segoe UI");

        double scale = Math.Clamp(s.TextScalePercent, 80, 130) / 100.0;
        double Scaled(double size) => Math.Round(size * scale * 2) / 2;
        res["FontCaption"] = Scaled(12);
        res["FontBody"] = Scaled(14);
        res["FontSubtitle"] = Scaled(16);
        res["FontTitle"] = Scaled(20);
        res["FontDisplay"] = Scaled(24);

        // Block line heights keep their original 4-pt rhythm at 100% and grow with the text (ratios of the default scale).
        double Line(double size, double ratio) => Math.Max(4, Math.Round(Scaled(size) * ratio / 4) * 4);
        res["LineCaption"] = Line(12, 4.0 / 3);
        res["LineBody"] = Line(14, 10.0 / 7);
        res["LineSubtitle"] = Line(16, 1.25);
        res["LineTitle"] = Line(20, 1.2);
        res["LineDisplay"] = Line(24, 7.0 / 6);
    }

    private static void ApplyShape(ResourceDictionary res, AppSettings s)
    {
        double r = Math.Clamp(s.CornerRadius, 0, 24);
        res["RadiusSmall"] = new CornerRadius(Math.Round(r / 2));
        res["RadiusCard"] = new CornerRadius(r);
        res["RadiusPill"] = new CornerRadius(r == 0 ? 0 : 12);

        if (s.CompactSpacing)
        {
            res["PagePadding"] = new Thickness(16, 12, 16, 16);
            res["CardPadding"] = new Thickness(12, 8, 12, 8);
            res["CardGap"] = new Thickness(0, 0, 0, 8);
            res["SectionGap"] = new Thickness(0, 0, 0, 16);
        }
        else
        {
            res["PagePadding"] = new Thickness(32, 24, 32, 32);
            res["CardPadding"] = new Thickness(20, 16, 20, 16);
            res["CardGap"] = new Thickness(0, 0, 0, 12);
            res["SectionGap"] = new Thickness(0, 0, 0, 24);
        }
    }
}
