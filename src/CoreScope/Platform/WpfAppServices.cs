using System;
using System.IO;
using System.Linq;
using System.Windows;
using CoreScope.Core;
using CoreScope.Core.ViewModels;

namespace CoreScope.Platform;

/// <summary>WPF/WinForms implementations of the services the view models use.</summary>
public sealed class WpfAppServices : IAppServices, IDisposable
{
    private OverlayWindow? _overlay;
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ToolStripMenuItem? _overlayItem;
    private System.Windows.Threading.DispatcherTimer? _tooltipTimer;
    private MainViewModel? _vm;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public void Attach(MainViewModel vm)
    {
        _vm = vm;
        CreateTray();
    }

    // ───────── Tray ─────────

    private void CreateTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open CoreScope", null, (_, _) => OpenRequested?.Invoke());
        _overlayItem = new System.Windows.Forms.ToolStripMenuItem("Show overlay", null, (_, _) =>
        {
            if (_vm is null) return;
            _vm.Settings.ShowOverlay = !_vm.Settings.ShowOverlay;
        })
        { Checked = AppSettings.Current.ShowOverlay };
        menu.Items.Add(_overlayItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        System.Drawing.Icon? icon = null;
        try { if (Environment.ProcessPath is { } exe) icon = System.Drawing.Icon.ExtractAssociatedIcon(exe); }
        catch (Exception ex) when (ex is ArgumentException or IOException) { Log.Error("Tray icon", ex); }

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Text = "CoreScope",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => OpenRequested?.Invoke();

        // Live tooltip: "CPU 12% · 48 °C | GPU 3% | RAM 71%" (Windows caps tooltips at 63 chars).
        _tooltipTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _tooltipTimer.Tick += (_, _) =>
        {
            if (_tray is null || _vm is null) return;
            var t = _vm.OverlayTiles;
            string V(int i) => i < t.Count ? t[i].ValueText.Replace(" ", "") : "—";
            var text = $"CoreScope · CPU {V(0)} {V(1)} · GPU {V(2)} · RAM {V(4)}";
            _tray.Text = text.Length > 63 ? text[..63] : text;
        };
        _tooltipTimer.Start();
    }

    public void Notify(string title, string message)
    {
        if (_tray is null) return;
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = message;
        _tray.ShowBalloonTip(8000);
    }

    // ───────── Overlay ─────────

    public void SetOverlayVisible(bool visible)
    {
        if (_overlayItem is not null) _overlayItem.Checked = visible;
        if (visible)
        {
            if (_overlay is null && _vm is not null)
            {
                _overlay = new OverlayWindow { DataContext = _vm };
                _overlay.Closed += (_, _) => _overlay = null;
            }
            _overlay?.Show();
            _overlay?.ApplySettings();
        }
        else
        {
            _overlay?.Close();
            _overlay = null;
        }
    }

    public void RefreshOverlaySettings() => _overlay?.ApplySettings();

    // ───────── Files & clipboard ─────────

    public string? SaveFile(string defaultName, string filter, string content)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = string.Concat(defaultName.Split(Path.GetInvalidFileNameChars())),
            Filter = filter,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return null;
        File.WriteAllText(dialog.FileName, content);
        return dialog.FileName;
    }

    public bool Confirm(string title, string message, string yes) => FixDialogs.Confirm(title, message, yes);

    public void ShowGuide(CoreScope.Core.Fixes.Guide guide, Func<CoreScope.Core.Fixes.FixAction, System.Threading.Tasks.Task<CoreScope.Core.Fixes.FixResult>> run) => FixDialogs.Guide(guide, run);

    public void CopyText(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException ex) { Log.Error("Clipboard", ex); }
    }

    public void Dispose()
    {
        _tooltipTimer?.Stop();
        _overlay?.Close();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
    }
}
