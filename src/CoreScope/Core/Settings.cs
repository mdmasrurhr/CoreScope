using System;
using System.IO;
using System.Text.Json;

namespace CoreScope.Core;

/// <summary>User preferences, stored as JSON in %LocalAppData%\CoreScope\settings.json.</summary>
public sealed class AppSettings
{
    // Behaviour
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public int RefreshIntervalMs { get; set; } = 1000;
    public bool UseFahrenheit { get; set; }
    public bool TrayHintShown { get; set; }

    // Updates
    public bool CheckForUpdates { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }
    public string? SkippedVersion { get; set; }

    // Insights the user dismissed: title → until (DateTime.MaxValue = forever)
    public System.Collections.Generic.Dictionary<string, DateTime> DismissedInsights { get; set; } = new();

    // Overlay
    public bool ShowOverlay { get; set; }
    public double OverlayLeft { get; set; } = 24;
    public double OverlayTop { get; set; } = 24;
    public double OverlayOpacity { get; set; } = 0.9;
    public bool OverlayClickThrough { get; set; }

    // Alerts
    public bool AlertsEnabled { get; set; } = true;
    public int AlertCpuTempC { get; set; } = 90;
    public int AlertGpuTempC { get; set; } = 85;
    public int AlertDriveTempC { get; set; } = 70;
    public int AlertBatteryPercent { get; set; } = 15;

    // Appearance (see Core/Theming). Defaults reproduce the "Fluent" skin.
    public string ThemeMode { get; set; } = "System";          // System | Light | Dark
    public string? ModeBeforeSkin { get; set; }                // the user's mode while a skin (e.g. Midnight) forces its own
    public string SkinId { get; set; } = "fluent";             // a ThemeCatalog skin id, or "custom" once tweaked
    public string Layout { get; set; } = "Sidebar";            // Sidebar | Rail | TopBar
    public bool CompactSpacing { get; set; }
    public string? AccentHex { get; set; }                     // null = Windows accent colour
    public string Backdrop { get; set; } = "Mica";             // Mica | Acrylic | None
    public int Transparency { get; set; } = 70;                // 0 solid .. 100 see-through
    public bool TransparencyWindow { get; set; } = true;
    public bool TransparencySidebar { get; set; } = true;
    public bool TransparencyCards { get; set; } = true;
    public int Tint { get; set; }                              // 0..100 accent wash over surfaces
    public bool TintWindow { get; set; } = true;
    public bool TintSidebar { get; set; } = true;
    public bool TintCards { get; set; }
    public bool TintTitleBar { get; set; }
    public int Glow { get; set; }                              // 0 off .. 100 strong
    public bool GlowAccent { get; set; } = true;               // primary buttons, selected nav item
    public bool GlowCards { get; set; }
    public bool GlowHeadings { get; set; }
    public string FontName { get; set; } = "Segoe UI Variable";
    public int TextScalePercent { get; set; } = 100;
    public int CornerRadius { get; set; } = 8;
    public System.Collections.Generic.Dictionary<string, string> SavedThemes { get; set; } = new();  // name -> theme file JSON
    public System.Collections.Generic.Dictionary<string, string> PageLooks { get; set; } = new();    // page title -> "skin:id" | "theme:name"
    public bool PageAccentsEnabled { get; set; }               // colour-code pages: each page can use its own accent
    public System.Collections.Generic.Dictionary<string, string> PageAccents { get; set; } = new();   // page title -> hex overrides
    public string? BackgroundFromHex { get; set; }             // optional window gradient (skins)
    public string? BackgroundToHex { get; set; }

    // Fans (desktop boards with controllable fans)
    public string FanMode { get; set; } = "Auto";
    public int FanFixedPercent { get; set; } = 50;

    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoreScope");
    private static string FilePath => Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Current { get; private set; } = Load();

    /// <summary>An independent copy (used to overlay a page's look without touching the saved settings).</summary>
    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this)) ?? new AppSettings();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error("Loading settings (using defaults)", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Saving settings", ex);
        }
    }

    public static string DataFolder => Folder;
}
