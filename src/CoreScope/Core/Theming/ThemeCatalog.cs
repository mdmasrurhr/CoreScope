using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreScope.Core.Theming;

/// <summary>
/// A skin is a complete look: colours, transparency, glow, font, shape and window layout. Picking one copies its values into
/// <see cref="AppSettings"/>; every control can then be tweaked individually (the skin id becomes "custom").
/// </summary>
public sealed record Skin(
    string Id, string Name, string Description,
    string? Mode, string? Accent, string Backdrop, int Transparency, int Tint, int Glow,
    string Font, int Radius, string Layout, bool Compact,
    string? BackgroundFrom = null, string? BackgroundTo = null,
    bool GlowCards = false, bool GlowHeadings = false, bool TintCards = false, bool TintTitleBar = false)
{
    /// <summary>Swatches for the picker preview (page background, sidebar, card), independent of the live theme.</summary>
    public string PreviewBackground => BackgroundFrom ?? (Mode == "Dark" ? "#202020" : "#EEF1F6");
    public string PreviewSurface => Mode == "Dark" ? "#2E2E34" : "#FFFFFF";
}

public static class ThemeCatalog
{
    public const string CustomId = "custom";
    public static readonly string[] Layouts = { "Sidebar", "Rail", "TopBar" };

    public static IReadOnlyList<Skin> Skins { get; } = new[]
    {
        new Skin("fluent", "Fluent", "Windows 11 look: Mica backdrop and your Windows accent colour.",
            null, null, "Mica", 70, 0, 0, "Segoe UI Variable", 8, "Sidebar", false),
        new Skin("aurora", "Aurora", "Frosted glass over a violet-to-teal wash with a soft glow.",
            null, "#7C6CFF", "Acrylic", 80, 35, 35, "Segoe UI Variable", 16, "Sidebar", false,
            "#6D5BFF", "#13C8B4", GlowCards: false, TintCards: true),
        new Skin("midnight", "Midnight", "Deep navy, neon cyan and a slim icon rail. Built for dark rooms.",
            "Dark", "#22D3EE", "None", 15, 30, 70, "Segoe UI", 6, "Rail", false,
            "#0B1020", "#111B3A", GlowCards: true),
        new Skin("ember", "Ember", "Warm copper tones with tabs across the top.",
            null, "#FF7A45", "Mica", 55, 40, 30, "Trebuchet MS", 20, "TopBar", false,
            "#FF9A5A", "#C2410C", TintTitleBar: true),
        new Skin("paper", "Paper", "Flat, solid and crisp. No transparency, no glow, tight spacing.",
            "Light", "#2F5BEA", "None", 0, 0, 0, "Segoe UI", 4, "Sidebar", true,
            "#F6F5F1", "#F6F5F1"),
        new Skin("terminal", "Terminal", "Phosphor green on black, monospaced, square corners.",
            "Dark", "#3DDC84", "None", 0, 15, 55, "Consolas", 0, "TopBar", true,
            "#050805", "#0A140A", GlowHeadings: true),
    };

    /// <summary>Colours used when "colour-code pages" is on and a page has no override of its own.</summary>
    private static readonly Dictionary<string, string> PageDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Overview"] = "#0078D4", ["Insights"] = "#F5A623", ["Upgrade"] = "#2EBD59", ["Control"] = "#7C6CFF",
        ["Network"] = "#13C8B4", ["Benchmark"] = "#E5484D", ["Processor"] = "#FF7A45", ["Memory"] = "#4F46E5",
        ["Graphics"] = "#E5489B", ["Storage"] = "#22D3EE", ["Motherboard"] = "#8BC34A", ["Devices"] = "#64748B",
        ["System"] = "#0078D4", ["Sensors"] = "#F5A623", ["Appearance"] = "#E5489B", ["Settings"] = "#64748B",
    };

    /// <summary>The accent a page should use, or null to keep the global accent.</summary>
    public static string? PageAccent(AppSettings s, string? page)
    {
        if (!s.PageAccentsEnabled || string.IsNullOrEmpty(page)) return null;
        foreach (var (key, hex) in s.PageAccents)
            if (key.Equals(page, StringComparison.OrdinalIgnoreCase)) return hex;
        return PageDefaults.TryGetValue(page, out var auto) ? auto : null;
    }

    public static Skin? Find(string? id) => Skins.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Copies a skin's values into the settings. Mode is only changed by skins that need a specific one.</summary>
    public static void Apply(Skin skin, AppSettings s)
    {
        s.SkinId = skin.Id;
        if (skin.Mode is { } mode)
        {
            if (s.ThemeMode != mode) s.ModeBeforeSkin ??= s.ThemeMode;
            s.ThemeMode = mode;
        }
        else if (s.ModeBeforeSkin is { } previous)
        {
            s.ThemeMode = previous;
            s.ModeBeforeSkin = null;
        }
        s.AccentHex = skin.Accent;
        s.Backdrop = skin.Backdrop;
        s.Transparency = skin.Transparency;
        s.TransparencyWindow = s.TransparencySidebar = s.TransparencyCards = true;
        s.Tint = skin.Tint;
        s.TintWindow = s.TintSidebar = true;
        s.TintCards = skin.TintCards;
        s.TintTitleBar = skin.TintTitleBar;
        s.Glow = skin.Glow;
        s.GlowAccent = true;
        s.GlowCards = skin.GlowCards;
        s.GlowHeadings = skin.GlowHeadings;
        s.FontName = skin.Font;
        s.CornerRadius = skin.Radius;
        s.Layout = skin.Layout;
        s.CompactSpacing = skin.Compact;
        s.BackgroundFromHex = skin.BackgroundFrom;
        s.BackgroundToHex = skin.BackgroundTo;
    }
}
