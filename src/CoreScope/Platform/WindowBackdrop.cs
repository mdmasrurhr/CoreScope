using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CoreScope.Core;
using Microsoft.Win32;

namespace CoreScope.Platform;

/// <summary>
/// Windows 11 backdrop for the main window: Mica, Acrylic or none, extended under the title bar, with the caption colour
/// and text following the app theme (see <see cref="ThemeManager"/>). Every call is best effort; older Windows builds
/// simply keep the default frame.
/// </summary>
internal static class WindowBackdrop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int DwmwaSystemBackdropType = 38;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    /// <summary>Applies the backdrop when the window is created, after every theme change, and when Windows switches light/dark.</summary>
    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => Apply(window);
        Action applied = () => window.Dispatcher.BeginInvoke(() => Apply(window));
        ThemeManager.Applied += applied;

        UserPreferenceChangedEventHandler handler = (_, e) =>
        {
            // "System" mode follows Windows: rebuild the theme, which in turn re-applies the backdrop.
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                window.Dispatcher.BeginInvoke(() => ThemeManager.Apply(AppSettings.Current));
        };
        SystemEvents.UserPreferenceChanged += handler;
        window.Closed += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= handler;
            ThemeManager.Applied -= applied;
        };
    }

    private static void Apply(Window window)
    {
        if (Environment.OSVersion.Version.Build < 22621) return; // backdrop types need Windows 11 22H2
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int dark = ThemeManager.IsDark ? 1 : 0;
        int backdrop = ThemeManager.Effective.Backdrop switch { "None" => 1, "Acrylic" => 3, _ => 2 };
        int caption = ToColorRef(ThemeManager.CaptionColor);
        int text = ToColorRef(ThemeManager.CaptionText);

        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    /// <summary>COLORREF is 0x00BBGGRR.</summary>
    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
}
