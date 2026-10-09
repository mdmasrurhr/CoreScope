using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreScope.Core.Theming;

/// <summary>A choice in the "look for this page" picker.</summary>
public sealed record LookOption(string Id, string Label);

/// <summary>
/// Per-page looks: a page can be assigned a skin or one of the user's saved themes, which then replaces the base look
/// (colours, glass, glow, font, shape and window layout) while that page is on screen. The base settings are never modified.
/// </summary>
public static class ThemeLooks
{
    public const string None = "";
    private const string SkinPrefix = "skin:";
    private const string ThemePrefix = "theme:";

    /// <summary>The Appearance page always shows the base look, so edits made there are visible as they happen.</summary>
    public const string AppearancePage = "Appearance";

    public static string ForSkin(Skin skin) => SkinPrefix + skin.Id;
    public static string ForTheme(string name) => ThemePrefix + name;

    /// <summary>The settings in force for <paramref name="page"/>: the base settings, or a copy with that page's look applied.</summary>
    public static AppSettings Resolve(AppSettings baseSettings, string? page)
    {
        if (string.IsNullOrEmpty(page) || page.Equals(AppearancePage, StringComparison.OrdinalIgnoreCase)) return baseSettings;
        var look = LookFor(baseSettings, page);
        if (look is null) return baseSettings;

        var copy = baseSettings.Clone();
        if (look.StartsWith(SkinPrefix, StringComparison.Ordinal) && ThemeCatalog.Find(look[SkinPrefix.Length..]) is { } skin)
        {
            ThemeCatalog.Apply(skin, copy);
            return KeepBaseLayout(baseSettings, copy);
        }
        if (look.StartsWith(ThemePrefix, StringComparison.Ordinal)
            && baseSettings.SavedThemes.TryGetValue(look[ThemePrefix.Length..], out var json)
            && ThemeFile.TryImport(json, copy, out _))
            return KeepBaseLayout(baseSettings, copy);

        return baseSettings; // the look no longer exists (theme deleted, skin removed): fall back quietly
    }

    /// <summary>With "keep my layout" on, a page's look changes colours and style but never moves the navigation.</summary>
    private static AppSettings KeepBaseLayout(AppSettings baseSettings, AppSettings look)
    {
        if (baseSettings.KeepLayoutOnAllPages) look.Layout = baseSettings.Layout;
        return look;
    }

    public static string? LookFor(AppSettings s, string page)
    {
        foreach (var (key, value) in s.PageLooks)
            if (key.Equals(page, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    public static bool AnyPageHasLook(AppSettings s) => s.PageLooks.Count > 0;

    /// <summary>Everything a page can be given: "default look", every skin, then the saved themes.</summary>
    public static List<LookOption> Choices(AppSettings s)
    {
        var list = new List<LookOption> { new(None, "Use the main look") };
        list.AddRange(ThemeCatalog.Skins.Select(skin => new LookOption(ForSkin(skin), "Skin: " + skin.Name)));
        list.AddRange(s.SavedThemes.Keys.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).Select(n => new LookOption(ForTheme(n), "My theme: " + n)));
        return list;
    }

    // ───────────── Saved themes ─────────────

    public const int MaxSavedThemes = 24;
    public const int MaxNameLength = 40;

    /// <summary>Saves the current look under <paramref name="name"/> (replacing a theme of the same name).</summary>
    public static bool TrySave(AppSettings s, string? name, out string error)
    {
        error = "";
        var clean = (name ?? "").Trim();
        if (clean.Length == 0) { error = "Give the theme a name first."; return false; }
        if (clean.Length > MaxNameLength) { error = $"Names can be up to {MaxNameLength} characters."; return false; }
        var existing = s.SavedThemes.Keys.FirstOrDefault(k => k.Equals(clean, StringComparison.OrdinalIgnoreCase));
        if (existing is null && s.SavedThemes.Count >= MaxSavedThemes) { error = $"You can keep up to {MaxSavedThemes} saved themes. Delete one first."; return false; }
        if (existing is not null) s.SavedThemes.Remove(existing);
        s.SavedThemes[clean] = ThemeFile.Export(s, clean);
        return true;
    }

    /// <summary>Applies a saved theme to the base look.</summary>
    public static bool TryApply(AppSettings s, string name, out string error)
    {
        error = "That theme no longer exists.";
        return s.SavedThemes.TryGetValue(name, out var json) && ThemeFile.TryImport(json, s, out error);
    }

    /// <summary>Deletes a saved theme and any page assignments that pointed at it.</summary>
    public static void Delete(AppSettings s, string name)
    {
        s.SavedThemes.Remove(name);
        var id = ForTheme(name);
        foreach (var page in s.PageLooks.Where(p => p.Value == id).Select(p => p.Key).ToList()) s.PageLooks.Remove(page);
    }
}
