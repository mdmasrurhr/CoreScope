using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CoreScope.Core;
using CoreScope.Core.ViewModels;
using CoreScope.Platform;

namespace CoreScope;

public partial class App : Application
{
#if TESTBUILD
    private const string InstanceName = @"Local\CoreScope.Test";
#else
    private const string InstanceName = @"Local\CoreScope";
#endif

    private MainViewModel? _viewModel;
    private WpfAppServices? _services;
    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless self-test: no window, writes selftest.json, exit code = failed checks.
        if (Array.Exists(e.Args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            int failed = 99;
            try { failed = await SelfTest.RunAsync(new RadioService()); }
            catch (Exception ex) { Log.Error("Self-test crashed", ex); Log.Info(ex.ToString()); }
            Shutdown(failed);
            return;
        }

        // One copy at a time (two sensor engines would fight over the same hardware buses).
        // A second launch just signals the running copy to show its window, even if it's hidden in the tray.
        _singleInstance = new Mutex(true, InstanceName + ".Mutex", out bool isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        if (!isFirst && Array.Exists(e.Args, a => a.Equals("--restarted", StringComparison.OrdinalIgnoreCase)))
        {
            // We're the elevated copy started by "Unlock full sensors": wait for the old copy to close.
            try { isFirst = _singleInstance.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { isFirst = true; }
        }
        if (!isFirst)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }
        var listener = new Thread(() =>
        {
            while (_showSignal.WaitOne())
            {
                if (_exiting) return;
                Dispatcher.BeginInvoke(ShowMainWindow);
            }
        }) { IsBackground = true, Name = "CoreScope.ShowSignal" };
        listener.Start();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error("Unhandled exception", args.ExceptionObject as Exception ?? new Exception("Unknown error"));
            _viewModel?.EmergencyRestoreFans();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _viewModel?.EmergencyRestoreFans();
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        Log.Info($"CoreScope {typeof(App).Assembly.GetName().Version} starting on {Environment.OSVersion}");

        // Fluent theme + the user's Appearance settings (mode, skin, accent, transparency, glow, font, layout).
        Platform.TextInteraction.Register();   // all text selectable / copyable
        Platform.ThemeManager.Apply(Core.AppSettings.Current);
        ShutdownMode = ShutdownMode.OnExplicitShutdown; // closing the window may just hide it to the tray

        _services = new WpfAppServices();
        _viewModel = new MainViewModel(new DispatcherSynchronizationContext(Dispatcher), text => _services.CopyText(text), new RadioService(), _services);
        _services.Attach(_viewModel);
        _viewModel.RestartElevatedRequested += RestartElevated;
        _viewModel.Update.ExitForUpdate += ExitApp;
        _viewModel.Appearance.Changed += () => Platform.ThemeManager.Apply(Core.AppSettings.Current);
        Platform.ThemeManager.CurrentPage = _viewModel.SelectedPage.Title;
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(Core.ViewModels.MainViewModel.SelectedPage)) return;
            Platform.ThemeManager.CurrentPage = _viewModel.SelectedPage.Title;
            var saved = Core.AppSettings.Current;
            if (saved.PageAccentsEnabled || Core.Theming.ThemeLooks.AnyPageHasLook(saved)) Platform.ThemeManager.Apply(saved);
        };
        if (Core.Hardware.Native.IsPackaged) PackagedStartup.Attach();
        var pageArg = Array.Find(e.Args, a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase));
        if (pageArg is not null) _viewModel.SelectPage(pageArg["--page=".Length..]);
        _services.OpenRequested += ShowMainWindow;
        _services.ExitRequested += ExitApp;

        var window = new MainWindow { DataContext = _viewModel };
        _viewModel.AnchorRequested += title => Platform.PageAnchor.ScrollTo(window, title);
        window.Closing += (_, args) =>
        {
            if (_exiting) return;
            if (AppSettings.Current.CloseToTray)
            {
                args.Cancel = true;
                window.Hide();
                if (!AppSettings.Current.TrayHintShown)
                {
                    _services.Notify("CoreScope is still running", "It keeps monitoring from the tray. Right-click the tray icon to exit, or turn this off in Settings.");
                    AppSettings.Current.TrayHintShown = true;
                    AppSettings.Current.Save();
                }
            }
            else
            {
                ExitApp();
            }
        };
        MainWindow = window;

        bool minimized = Array.Exists(e.Args, a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)) || AppSettings.Current.StartMinimized;
        if (!minimized) window.Show();
        if (AppSettings.Current.ShowOverlay) _services.SetOverlayVisible(true);

        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Initialization", ex);
            MessageBox.Show($"CoreScope couldn't finish scanning your hardware:\n\n{ex.Message}\n\nDetails were written to:\n{Log.Path}",
                "CoreScope", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowMainWindow()
    {
        if (MainWindow is null) return;
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    /// <summary>
    /// "Unlock full sensors": hand the single-instance lock over, start an elevated copy (Windows shows UAC),
    /// then exit. If the user says No, keep running as we are.
    /// </summary>
    private void RestartElevated(string page)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        try { _singleInstance?.ReleaseMutex(); }
        catch (ApplicationException) { }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"--restarted \"--page={page}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            Log.Info("Restarting elevated");
            ExitApp();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Info($"Elevation declined or failed: {ex.Message}");
            try { _singleInstance?.WaitOne(0); }
            catch (AbandonedMutexException) { }
        }
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _showSignal?.Set(); // release the listener thread
        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception", e.Exception);
        Log.Info(e.Exception.ToString());
        e.Handled = true;
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were written to:\n{Log.Path}",
            "CoreScope", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _viewModel?.Dispose(); // stops the sensor engine and hands fans back to the BIOS
        _services?.Dispose();
        AppSettings.Current.Save();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
