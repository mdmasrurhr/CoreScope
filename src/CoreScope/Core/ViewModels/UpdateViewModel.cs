using System;
using System.Threading.Tasks;
using CoreScope.Core.Hardware;

namespace CoreScope.Core.ViewModels;

/// <summary>"Update available" bar and the Settings → Updates controls.</summary>
public sealed class UpdateViewModel : ObservableObject
{
    private UpdateInfo? _info;
    private bool _isAvailable, _isBusy;
    private string _status = "", _settingsStatus = "";

    public UpdateViewModel()
    {
        InstallCommand = new RelayCommand(async () => await InstallAsync());
        LaterCommand = new RelayCommand(() =>
        {
            if (_info is not null) { AppSettings.Current.SkippedVersion = _info.Version.ToString(); AppSettings.Current.Save(); }
            IsAvailable = false;
        });
        OpenNotesCommand = new RelayCommand(() => Shell.Open(_info?.PageUrl ?? UpdateChecker.ReleasesPage));
        CheckNowCommand = new RelayCommand(async () => await CheckAsync(manual: true));
    }

    public RelayCommand InstallCommand { get; }
    public RelayCommand LaterCommand { get; }
    public RelayCommand OpenNotesCommand { get; }
    public RelayCommand CheckNowCommand { get; }

    /// <summary>Raised when the installer is running and CoreScope should exit so it can replace files.</summary>
    public event Action? ExitForUpdate;

    public bool IsAvailable { get => _isAvailable; private set => Set(ref _isAvailable, value); }
    public bool CanInstall => !_isBusy;
    public string Title => _info is null ? "" : $"CoreScope {_info.Version} is available (you have {UpdateChecker.CurrentVersion})";
    public string InstallText => _isBusy ? "Downloading…" : "Update now";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string SettingsStatus { get => _settingsStatus; private set => Set(ref _settingsStatus, value); }
    public string CurrentVersion => UpdateChecker.CurrentVersion.ToString();

    public bool AutoCheck
    {
        get => AppSettings.Current.CheckForUpdates;
        set { AppSettings.Current.CheckForUpdates = value; AppSettings.Current.Save(); OnPropertyChanged(); }
    }

    /// <summary>Startup check: at most twice a day, skipped versions stay quiet.</summary>
    public async Task CheckOnStartupAsync()
    {
        if (!AppSettings.Current.CheckForUpdates) return;
        if (AppSettings.Current.LastUpdateCheck is { } last && DateTime.Now - last < TimeSpan.FromHours(12)) return;
        await Task.Delay(TimeSpan.FromSeconds(15)); // let the app settle first
        await CheckAsync(manual: false);
    }

    public async Task CheckAsync(bool manual)
    {
        SettingsStatus = "Checking…";
        var info = await UpdateChecker.CheckAsync();
        if (info is null)
        {
            SettingsStatus = $"You have the latest version ({UpdateChecker.CurrentVersion}) · checked {DateTime.Now:HH:mm}";
            return;
        }
        SettingsStatus = $"Version {info.Version} is available.";
        if (!manual && AppSettings.Current.SkippedVersion == info.Version.ToString()) return;
        _info = info;
        OnPropertyChanged(nameof(Title));
        Status = "";
        IsAvailable = true;
    }

    private async Task InstallAsync()
    {
        if (_info is null || _isBusy) return;
        _isBusy = true;
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(InstallText));
        var progress = new Progress<double>(p => Status = $"Downloading… {p:P0}");
        var (started, message) = await UpdateChecker.DownloadAndRunAsync(_info, progress);
        Status = message;
        _isBusy = false;
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(InstallText));
        if (started) ExitForUpdate?.Invoke();
        else Shell.Open(_info.PageUrl);
    }
}
