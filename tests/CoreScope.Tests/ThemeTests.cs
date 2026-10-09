using System;
using System.Linq;
using System.Windows.Media;
using CoreScope.Core;
using CoreScope.Core.Theming;
using Xunit;

namespace CoreScope.Tests;

public class ThemeColorsTests
{
    [Theory]
    [InlineData("#7C6CFF", 0x7C, 0x6C, 0xFF)]
    [InlineData("7c6cff", 0x7C, 0x6C, 0xFF)]
    [InlineData("#0AF", 0x00, 0xAA, 0xFF)]
    public void TryParse_AcceptsCommonHexForms(string text, int r, int g, int b)
    {
        Assert.True(ThemeColors.TryParse(text, out var c));
        Assert.Equal((byte)r, c.R);
        Assert.Equal((byte)g, c.G);
        Assert.Equal((byte)b, c.B);
        Assert.Equal(255, c.A);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#12")]
    [InlineData("not a colour")]
    [InlineData("#GGGGGG")]
    public void TryParse_RejectsGarbage(string? text) => Assert.False(ThemeColors.TryParse(text, out _));

    [Fact]
    public void Lerp_EndpointsAndMiddle()
    {
        Assert.Equal(Colors.Black, ThemeColors.Lerp(Colors.Black, Colors.White, 0));
        Assert.Equal(Colors.White, ThemeColors.Lerp(Colors.Black, Colors.White, 1));
        Assert.Equal(Color.FromRgb(128, 128, 128), ThemeColors.Lerp(Colors.Black, Colors.White, 0.5));
    }

    [Fact]
    public void Contrast_BlackOnWhiteIsMaximum() => Assert.Equal(21, ThemeColors.Contrast(Colors.Black, Colors.White), 1);

    [Theory]
    [InlineData("#FFFFFF", "#000000")]   // white background -> black text
    [InlineData("#101010", "#FFFFFF")]   // near-black background -> white text
    [InlineData("#22D3EE", "#000000")]   // bright cyan accent -> black text
    [InlineData("#4F46E5", "#FFFFFF")]   // indigo accent -> white text
    public void OnColor_PicksTheReadableOne(string background, string expected)
    {
        ThemeColors.TryParse(background, out var bg);
        ThemeColors.TryParse(expected, out var want);
        Assert.Equal(want, ThemeColors.OnColor(bg));
    }

    [Fact]
    public void ReadableOn_LightensDarkAccentsOnDarkSurfaces()
    {
        ThemeColors.TryParse("#202020", out var surface);
        ThemeColors.TryParse("#1E3A8A", out var darkBlue);
        var readable = ThemeColors.ReadableOn(darkBlue, surface);
        Assert.True(ThemeColors.Contrast(readable, surface) >= 4.5);
    }
}

public class ThemeCatalogTests
{
    [Fact]
    public void EverySkinHasUniqueIdAndValidValues()
    {
        Assert.Equal(ThemeCatalog.Skins.Count, ThemeCatalog.Skins.Select(s => s.Id).Distinct().Count());
        foreach (var skin in ThemeCatalog.Skins)
        {
            Assert.Contains(skin.Layout, ThemeCatalog.Layouts);
            Assert.InRange(skin.Transparency, 0, 100);
            Assert.InRange(skin.Glow, 0, 100);
            Assert.InRange(skin.Radius, 0, 24);
            if (skin.Accent is not null) Assert.True(ThemeColors.TryParse(skin.Accent, out _), skin.Id);
            if (skin.BackgroundFrom is not null) Assert.True(ThemeColors.TryParse(skin.BackgroundFrom, out _), skin.Id);
        }
    }

    [Fact]
    public void Apply_CopiesSkinIntoSettings()
    {
        var s = new AppSettings();
        ThemeCatalog.Apply(ThemeCatalog.Find("midnight")!, s);
        Assert.Equal("midnight", s.SkinId);
        Assert.Equal("Rail", s.Layout);
        Assert.Equal("Dark", s.ThemeMode);
        Assert.Equal("#22D3EE", s.AccentHex);
    }

    [Fact]
    public void Apply_RestoresTheUsersModeAfterAForcedModeSkin()
    {
        var s = new AppSettings { ThemeMode = "Light" };
        ThemeCatalog.Apply(ThemeCatalog.Find("midnight")!, s);   // forces Dark
        Assert.Equal("Dark", s.ThemeMode);
        ThemeCatalog.Apply(ThemeCatalog.Find("aurora")!, s);     // no preference: back to what the user had
        Assert.Equal("Light", s.ThemeMode);
        Assert.Null(s.ModeBeforeSkin);
    }

    [Fact]
    public void Apply_DoesNotOverwriteTheRememberedModeWhenSwitchingBetweenForcedSkins()
    {
        var s = new AppSettings { ThemeMode = "System" };
        ThemeCatalog.Apply(ThemeCatalog.Find("midnight")!, s);   // Dark
        ThemeCatalog.Apply(ThemeCatalog.Find("paper")!, s);      // Light
        ThemeCatalog.Apply(ThemeCatalog.Find("fluent")!, s);
        Assert.Equal("System", s.ThemeMode);
    }

    [Fact]
    public void Find_IsCaseInsensitiveAndNullSafe()
    {
        Assert.NotNull(ThemeCatalog.Find("AURORA"));
        Assert.Null(ThemeCatalog.Find("nope"));
        Assert.Null(ThemeCatalog.Find(null));
    }
}

public class ThemeFileTests
{
    [Fact]
    public void ExportThenImport_RoundTripsEverything()
    {
        var source = new AppSettings();
        ThemeCatalog.Apply(ThemeCatalog.Find("aurora")!, source);
        source.Glow = 61;
        source.GlowHeadings = true;
        source.PageAccentsEnabled = true;
        source.PageAccents["Network"] = "#FF00AA";

        var target = new AppSettings();
        Assert.True(ThemeFile.TryImport(ThemeFile.Export(source), target, out var error), error);

        Assert.Equal(source.AccentHex, target.AccentHex);
        Assert.Equal(source.Backdrop, target.Backdrop);
        Assert.Equal(source.Transparency, target.Transparency);
        Assert.Equal(61, target.Glow);
        Assert.True(target.GlowHeadings);
        Assert.Equal(source.Layout, target.Layout);
        Assert.Equal(source.BackgroundFromHex, target.BackgroundFromHex);
        Assert.Equal("#FF00AA", target.PageAccents["Network"]);
        Assert.Equal(ThemeCatalog.CustomId, target.SkinId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"format\":\"something-else\",\"version\":1}")]
    [InlineData("{\"format\":\"corescope-theme\",\"version\":99,\"themeMode\":\"Dark\",\"layout\":\"Sidebar\",\"backdrop\":\"Mica\"}")]
    [InlineData("{\"format\":\"corescope-theme\",\"version\":1,\"themeMode\":\"Purple\",\"layout\":\"Sidebar\",\"backdrop\":\"Mica\"}")]
    [InlineData("{\"format\":\"corescope-theme\",\"version\":1,\"themeMode\":\"Dark\",\"layout\":\"Sidebar\",\"backdrop\":\"Mica\",\"accentHex\":\"nope\"}")]
    public void Import_RejectsBadFilesAndLeavesSettingsUntouched(string json)
    {
        var target = new AppSettings { Glow = 12, ThemeMode = "Light" };
        Assert.False(ThemeFile.TryImport(json, target, out var error));
        Assert.NotEmpty(error);
        Assert.Equal(12, target.Glow);
        Assert.Equal("Light", target.ThemeMode);
    }

    [Fact]
    public void Import_ClampsOutOfRangeNumbers()
    {
        var json = "{\"format\":\"corescope-theme\",\"version\":1,\"themeMode\":\"Dark\",\"layout\":\"Rail\",\"backdrop\":\"None\"," +
                   "\"transparency\":900,\"glow\":-5,\"cornerRadius\":500,\"textScalePercent\":1}";
        var target = new AppSettings();
        Assert.True(ThemeFile.TryImport(json, target, out _));
        Assert.Equal(100, target.Transparency);
        Assert.Equal(0, target.Glow);
        Assert.Equal(24, target.CornerRadius);
        Assert.Equal(80, target.TextScalePercent);
    }

    [Fact]
    public void Import_RefusesHugeFiles() =>
        Assert.False(ThemeFile.TryImport(new string(' ', ThemeFile.MaxBytes + 1), new AppSettings(), out _));

    [Fact]
    public void Import_DropsBadPageAccentsButKeepsTheRest()
    {
        var json = "{\"format\":\"corescope-theme\",\"version\":1,\"themeMode\":\"Dark\",\"layout\":\"Sidebar\",\"backdrop\":\"Mica\"," +
                   "\"pageAccents\":{\"Network\":\"#13C8B4\",\"Bad\":\"zzz\"}}";
        var target = new AppSettings();
        Assert.True(ThemeFile.TryImport(json, target, out _));
        Assert.Equal("#13C8B4", target.PageAccents["Network"]);
        Assert.False(target.PageAccents.ContainsKey("Bad"));
    }
}

public class PageAccentTests
{
    [Fact]
    public void Disabled_KeepsTheGlobalAccent() =>
        Assert.Null(ThemeCatalog.PageAccent(new AppSettings { PageAccentsEnabled = false }, "Network"));

    [Fact]
    public void Enabled_UsesOverrideThenAutomaticThenNothing()
    {
        var s = new AppSettings { PageAccentsEnabled = true };
        Assert.NotNull(ThemeCatalog.PageAccent(s, "Network"));          // automatic colour
        s.PageAccents["Network"] = "#010203";
        Assert.Equal("#010203", ThemeCatalog.PageAccent(s, "network")); // override wins (case-insensitive page names are not needed here)
        Assert.Null(ThemeCatalog.PageAccent(s, "No such page"));        // unknown page: global accent
    }
}

public class ThemeLooksTests
{
    [Fact]
    public void Resolve_ReturnsTheBaseSettingsWhenThePageHasNoLook()
    {
        var s = new AppSettings();
        Assert.Same(s, ThemeLooks.Resolve(s, "Network"));
        Assert.Same(s, ThemeLooks.Resolve(s, null));
    }

    [Fact]
    public void Resolve_AppliesASkinToACopyAndLeavesTheBaseAlone()
    {
        var s = new AppSettings();
        s.PageLooks["Network"] = ThemeLooks.ForSkin(ThemeCatalog.Find("midnight")!);

        var effective = ThemeLooks.Resolve(s, "network");   // page names match case-insensitively
        Assert.NotSame(s, effective);
        Assert.Equal("Rail", effective.Layout);
        Assert.Equal("Dark", effective.ThemeMode);
        Assert.Equal("Sidebar", s.Layout);                  // base untouched
        Assert.Equal("System", s.ThemeMode);
    }

    [Fact]
    public void Resolve_KeepsTheBaseLayoutWhenAsked()
    {
        var s = new AppSettings { KeepLayoutOnAllPages = true, Layout = "TopBar" };
        s.PageLooks["Network"] = ThemeLooks.ForSkin(ThemeCatalog.Find("midnight")!);   // Midnight is a Rail skin

        var effective = ThemeLooks.Resolve(s, "Network");
        Assert.Equal("TopBar", effective.Layout);          // navigation stays put
        Assert.Equal("Dark", effective.ThemeMode);         // but the rest of the look still applies

        s.KeepLayoutOnAllPages = false;
        Assert.Equal("Rail", ThemeLooks.Resolve(s, "Network").Layout);
    }

    [Fact]
    public void Resolve_AppliesASavedTheme()
    {
        var s = new AppSettings { Glow = 77, Layout = "TopBar" };
        Assert.True(ThemeLooks.TrySave(s, "Neon", out _));
        var baseLook = new AppSettings { SavedThemes = new(s.SavedThemes) };
        baseLook.PageLooks["Storage"] = ThemeLooks.ForTheme("Neon");

        var effective = ThemeLooks.Resolve(baseLook, "Storage");
        Assert.Equal(77, effective.Glow);
        Assert.Equal("TopBar", effective.Layout);
        Assert.Equal(0, baseLook.Glow);
    }

    [Fact]
    public void Resolve_ShowsTheMainLookOnTheAppearancePage()
    {
        var s = new AppSettings();
        s.PageLooks["Appearance"] = ThemeLooks.ForSkin(ThemeCatalog.Find("terminal")!);
        Assert.Same(s, ThemeLooks.Resolve(s, "Appearance"));
    }

    [Fact]
    public void Resolve_FallsBackWhenTheThemeWasDeleted()
    {
        var s = new AppSettings();
        s.PageLooks["Network"] = ThemeLooks.ForTheme("Gone");
        Assert.Same(s, ThemeLooks.Resolve(s, "Network"));
    }

    [Fact]
    public void Save_ValidatesNamesAndReplacesSameName()
    {
        var s = new AppSettings();
        Assert.False(ThemeLooks.TrySave(s, "  ", out var empty)); Assert.NotEmpty(empty);
        Assert.False(ThemeLooks.TrySave(s, new string('x', 41), out var tooLong)); Assert.NotEmpty(tooLong);
        Assert.True(ThemeLooks.TrySave(s, " Mine ", out _));
        s.Glow = 40;
        Assert.True(ThemeLooks.TrySave(s, "MINE", out _));     // same name, different case: replaces
        Assert.Single(s.SavedThemes);
        Assert.Contains("\"Glow\": 40", s.SavedThemes["MINE"]);
    }

    [Fact]
    public void Save_StopsAtTheLimit()
    {
        var s = new AppSettings();
        for (var i = 0; i < ThemeLooks.MaxSavedThemes; i++) Assert.True(ThemeLooks.TrySave(s, "t" + i, out _));
        Assert.False(ThemeLooks.TrySave(s, "one too many", out var error));
        Assert.Contains("up to", error);
        Assert.True(ThemeLooks.TrySave(s, "t3", out _));         // replacing is still allowed
    }

    [Fact]
    public void ApplyAndDelete_WorkOnTheBaseSettings()
    {
        var s = new AppSettings { Glow = 50, AccentHex = "#112233" };
        ThemeLooks.TrySave(s, "Saved", out _);
        s.Glow = 0; s.AccentHex = null;
        s.PageLooks["Network"] = ThemeLooks.ForTheme("Saved");

        Assert.True(ThemeLooks.TryApply(s, "Saved", out _));
        Assert.Equal(50, s.Glow);
        Assert.Equal("#112233", s.AccentHex);

        ThemeLooks.Delete(s, "Saved");
        Assert.Empty(s.SavedThemes);
        Assert.Empty(s.PageLooks);                              // assignments to the deleted theme go too
        Assert.False(ThemeLooks.TryApply(s, "Saved", out var error)); Assert.NotEmpty(error);
    }

    [Fact]
    public void Choices_ListsDefaultSkinsAndSavedThemes()
    {
        var s = new AppSettings();
        ThemeLooks.TrySave(s, "Zed", out _);
        ThemeLooks.TrySave(s, "Alpha", out _);
        var ids = ThemeLooks.Choices(s).Select(o => o.Id).ToList();
        Assert.Equal(ThemeLooks.None, ids[0]);
        Assert.Contains("skin:aurora", ids);
        Assert.True(ids.IndexOf("theme:Alpha") < ids.IndexOf("theme:Zed"));
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var s = new AppSettings { Glow = 10 };
        s.PageLooks["A"] = "skin:aurora";
        var copy = s.Clone();
        copy.Glow = 99; copy.PageLooks["B"] = "x";
        Assert.Equal(10, s.Glow);
        Assert.Single(s.PageLooks);
    }
}
