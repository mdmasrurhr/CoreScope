using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CoreScope.Core;

namespace CoreScope.Platform;

/// <summary>
/// Small always-on-top live readout (CPU/GPU load and temperature, RAM). Drag it anywhere; optional
/// click-through mode lets mouse clicks pass to the window underneath (e.g. while gaming).
/// Built in code so it has no dependency on the app's theme dictionaries.
/// </summary>
public sealed class OverlayWindow : Window
{
    private const int GwlExStyle = -20, WsExTransparent = 0x20, WsExLayered = 0x80000, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = false;
        Title = "CoreScope overlay";
        Left = AppSettings.Current.OverlayLeft;
        Top = AppSettings.Current.OverlayTop;

        // Fixed dark surface + white text: readable over any game or wallpaper.
        var panel = new ItemsControl { Focusable = false };
        panel.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("OverlayTiles"));
        var factory = new FrameworkElementFactory(typeof(StackPanel));
        factory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.ItemsPanel = new ItemsPanelTemplate(factory);
        panel.ItemTemplate = BuildItemTemplate();

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x16, 0x18, 0x1C)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 4, 8),
            Child = panel,
            Cursor = Cursors.SizeAll,
            ToolTip = "Drag to move · turn off in Settings or the tray menu",
        };

        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        LocationChanged += (_, _) =>
        {
            AppSettings.Current.OverlayLeft = Left;
            AppSettings.Current.OverlayTop = Top;
        };
        Closed += (_, _) => AppSettings.Current.Save();
        SourceInitialized += (_, _) => ApplySettings();
    }

    private static DataTemplate BuildItemTemplate()
    {
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(MarginProperty, new Thickness(0, 0, 16, 0));

        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding("ShortLabel"));
        label.SetValue(TextBlock.FontSizeProperty, 11.0);
        label.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xA8, 0xB0, 0xBA)));
        stack.AppendChild(label);

        var value = new FrameworkElementFactory(typeof(TextBlock));
        value.SetBinding(TextBlock.TextProperty, new Binding("ValueText"));
        value.SetValue(TextBlock.FontSizeProperty, 16.0);
        value.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        value.SetValue(TextBlock.ForegroundProperty, Brushes.White);
        value.SetValue(Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
        stack.AppendChild(value);

        return new DataTemplate { VisualTree = stack };
    }

    public void ApplySettings()
    {
        Opacity = AppSettings.Current.OverlayOpacity;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int style = GetWindowLong(hwnd, GwlExStyle) | WsExToolWindow | WsExNoActivate | WsExLayered;
        style = AppSettings.Current.OverlayClickThrough ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLong(hwnd, GwlExStyle, style);
    }
}
