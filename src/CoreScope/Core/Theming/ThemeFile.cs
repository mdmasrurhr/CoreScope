using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CoreScope.Core.Theming;

/// <summary>
/// Shareable theme file (*.corescope-theme.json): the Appearance settings and nothing else. Importing never runs code and
/// never touches non-appearance settings; every value is validated and clamped, unknown fields are ignored.
/// </summary>
public static class ThemeFile
{
    public const string FormatId = "corescope-theme";
    public const int Version = 1;
    public const int MaxBytes = 64 * 1024;
    public const string Extension = ".corescope-theme.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly string[] Modes = { "System", "Light", "Dark" };
    private static readonly string[] Backdrops = { "Mica", "Acrylic", "None" };

    private sealed class Dto
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public string? Name { get; set; }
        public string? ThemeMode { get; set; }
        public string? Layout { get; set; }
        public bool CompactSpacing { get; set; }
        public string? AccentHex { get; set; }
        public string? Backdrop { get; set; }
        public int Transparency { get; set; }
        public bool TransparencyWindow { get; set; } = true;
        public bool TransparencySidebar { get; set; } = true;
        public bool TransparencyCards { get; set; } = true;
        public int Tint { get; set; }
        public bool TintWindow { get; set; } = true;
        public bool TintSidebar { get; set; } = true;
        public bool TintCards { get; set; }
        public bool TintTitleBar { get; set; }
        public int Glow { get; set; }
        public bool GlowAccent { get; set; } = true;
        public bool GlowCards { get; set; }
        public bool GlowHeadings { get; set; }
        public string? FontName { get; set; }
        public int TextScalePercent { get; set; } = 100;
        public int CornerRadius { get; set; } = 8;
        public string? BackgroundFromHex { get; set; }
        public string? BackgroundToHex { get; set; }
        public bool PageAccentsEnabled { get; set; }
        public Dictionary<string, string>? PageAccents { get; set; }
    }

    public static string Export(AppSettings s, string? name = null)
    {
        var dto = new Dto
        {
            Format = FormatId, Version = Version, Name = name ?? (ThemeCatalog.Find(s.SkinId)?.Name ?? "My theme"),
            ThemeMode = s.ThemeMode, Layout = s.Layout, CompactSpacing = s.CompactSpacing, AccentHex = s.AccentHex, Backdrop = s.Backdrop,
            Transparency = s.Transparency, TransparencyWindow = s.TransparencyWindow, TransparencySidebar = s.TransparencySidebar, TransparencyCards = s.TransparencyCards,
            Tint = s.Tint, TintWindow = s.TintWindow, TintSidebar = s.TintSidebar, TintCards = s.TintCards, TintTitleBar = s.TintTitleBar,
            Glow = s.Glow, GlowAccent = s.GlowAccent, GlowCards = s.GlowCards, GlowHeadings = s.GlowHeadings,
            FontName = s.FontName, TextScalePercent = s.TextScalePercent, CornerRadius = s.CornerRadius,
            BackgroundFromHex = s.BackgroundFromHex, BackgroundToHex = s.BackgroundToHex,
            PageAccentsEnabled = s.PageAccentsEnabled, PageAccents = new Dictionary<string, string>(s.PageAccents),
        };
        return JsonSerializer.Serialize(dto, Options);
    }

    /// <summary>Validates <paramref name="json"/> and, only if all of it is acceptable, copies it into <paramref name="target"/>.</summary>
    public static bool TryImport(string json, AppSettings target, out string error)
    {
        error = "";
        if (json.Length > MaxBytes) { error = "That file is too large to be a CoreScope theme."; return false; }

        Dto? dto;
        try { dto = JsonSerializer.Deserialize<Dto>(json, Options); }
        catch (JsonException) { error = "That file isn't a valid theme (it isn't readable JSON)."; return false; }
        if (dto is null || dto.Format != FormatId) { error = "That file isn't a CoreScope theme."; return false; }
        if (dto.Version is < 1 or > Version) { error = $"That theme was made by a newer CoreScope (format {dto.Version})."; return false; }
        if (dto.ThemeMode is null || !Modes.Contains(dto.ThemeMode)) { error = "The theme has an unknown theme mode."; return false; }
        if (dto.Layout is null || !ThemeCatalog.Layouts.Contains(dto.Layout)) { error = "The theme has an unknown window layout."; return false; }
        if (dto.Backdrop is null || !Backdrops.Contains(dto.Backdrop)) { error = "The theme has an unknown backdrop."; return false; }
        if (dto.AccentHex is not null && !ThemeColors.TryParse(dto.AccentHex, out _)) { error = "The theme has an invalid accent colour."; return false; }
        foreach (var hex in new[] { dto.BackgroundFromHex, dto.BackgroundToHex })
            if (hex is not null && !ThemeColors.TryParse(hex, out _)) { error = "The theme has an invalid background colour."; return false; }

        var pageAccents = new Dictionary<string, string>();
        foreach (var (page, hex) in dto.PageAccents ?? new())
        {
            if (page.Length > 40 || !ThemeColors.TryParse(hex, out var c)) continue; // drop bad entries rather than refusing the file
            pageAccents[page] = ThemeColors.ToHex(c);
        }

        target.SkinId = ThemeCatalog.CustomId;
        target.ModeBeforeSkin = null;
        target.ThemeMode = dto.ThemeMode;
        target.Layout = dto.Layout;
        target.CompactSpacing = dto.CompactSpacing;
        target.AccentHex = dto.AccentHex is null ? null : ThemeColors.ToHex(ThemeColors.TryParse(dto.AccentHex, out var accent) ? accent : default);
        target.Backdrop = dto.Backdrop;
        target.Transparency = Math.Clamp(dto.Transparency, 0, 100);
        target.TransparencyWindow = dto.TransparencyWindow;
        target.TransparencySidebar = dto.TransparencySidebar;
        target.TransparencyCards = dto.TransparencyCards;
        target.Tint = Math.Clamp(dto.Tint, 0, 100);
        target.TintWindow = dto.TintWindow;
        target.TintSidebar = dto.TintSidebar;
        target.TintCards = dto.TintCards;
        target.TintTitleBar = dto.TintTitleBar;
        target.Glow = Math.Clamp(dto.Glow, 0, 100);
        target.GlowAccent = dto.GlowAccent;
        target.GlowCards = dto.GlowCards;
        target.GlowHeadings = dto.GlowHeadings;
        target.FontName = string.IsNullOrWhiteSpace(dto.FontName) || dto.FontName.Length > 64 ? "Segoe UI Variable" : dto.FontName.Trim();
        target.TextScalePercent = Math.Clamp(dto.TextScalePercent, 80, 130);
        target.CornerRadius = Math.Clamp(dto.CornerRadius, 0, 24);
        target.BackgroundFromHex = dto.BackgroundFromHex;
        target.BackgroundToHex = dto.BackgroundToHex;
        target.PageAccentsEnabled = dto.PageAccentsEnabled;
        target.PageAccents = pageAccents;
        return true;
    }
}
