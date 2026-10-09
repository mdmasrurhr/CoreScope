using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CoreScope.Core.Control;
using CoreScope.Core.Hardware;

namespace CoreScope.Core.ViewModels;

/// <summary>A pending or applied system change, with the exact action needed to put things back.</summary>
public sealed class ControlChange
{
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public bool NeedsConfirm { get; init; } = true;

    /// <summary>Display-mode style: auto-reverts unless the user clicks "Keep".</summary>
    public bool AutoRevert { get; init; }

    public required Func<Task<bool>> Apply { get; init; }
    public required Func<Task<bool>> Revert { get; init; }

    /// <summary>Re-reads the real system state so the UI never shows a value that wasn't applied.</summary>
    public Action? Resync { get; init; }

    /// <summary>Consecutive changes with the same key (a slider drag) collapse into one history line.</summary>
    public string? CoalesceKey { get; init; }
}

public sealed record TimeoutOption(string Label, uint Seconds)
{
    public static readonly TimeoutOption[] Standard =
    {
        new("1 minute", 60), new("2 minutes", 120), new("3 minutes", 180), new("5 minutes", 300),
        new("10 minutes", 600), new("15 minutes", 900), new("20 minutes", 1200), new("30 minutes", 1800),
        new("45 minutes", 2700), new("1 hour", 3600), new("2 hours", 7200), new("3 hours", 10800),
        new("5 hours", 18000), new("Never", 0),
    };

    public override string ToString() => Label;
}

public sealed record PowerModeOption(string Label, string Detail, Guid Id)
{
    public override string ToString() => Label;
}

public sealed record SettingsLink(string Label, string Uri, string Glyph)
{
    public RelayCommand OpenCommand { get; } = new(() => Shell.Open(Uri));
}

public sealed class DisplayItemViewModel : ObservableObject
{
    private readonly ControlCenterViewModel _owner;
    private DisplayTarget _target;
    private string? _resolution;
    private int _refresh;
    private bool _suppress;

    public DisplayItemViewModel(DisplayTarget target, ControlCenterViewModel owner)
    {
        _owner = owner;
        _target = target;
        Load(target);
    }

    public string Label => _target.Label;
    public ObservableCollection<string> Resolutions { get; } = new();
    public ObservableCollection<int> RefreshRates { get; } = new();

    private void Load(DisplayTarget target)
    {
        _suppress = true;
        _target = target;
        Resolutions.Clear();
        foreach (var r in target.Modes.Select(m => m.Resolution).Distinct()) Resolutions.Add(r);
        Resolution = target.Current.Resolution;
        FillRates(target.Current.Resolution);
        Refresh = target.Current.RefreshHz;
        OnPropertyChanged(nameof(Label));
        _suppress = false;
    }

    private void FillRates(string resolution)
    {
        RefreshRates.Clear();
        foreach (var hz in _target.Modes.Where(m => m.Resolution == resolution).Select(m => m.RefreshHz).Distinct().OrderByDescending(h => h))
            RefreshRates.Add(hz);
    }

    public string? Resolution
    {
        get => _resolution;
        set
        {
            if (!Set(ref _resolution, value) || _suppress || value is null) return;
            FillRates(value);
            var best = _target.Modes.Where(m => m.Resolution == value).OrderByDescending(m => m.RefreshHz)
                .FirstOrDefault(m => m.RefreshHz == _target.Current.RefreshHz) ?? _target.Modes.First(m => m.Resolution == value);
            _suppress = true;
            Refresh = best.RefreshHz;
            _suppress = false;
            RequestMode(best);
        }
    }

    public int Refresh
    {
        get => _refresh;
        set
        {
            if (!Set(ref _refresh, value) || _suppress || _resolution is null) return;
            var mode = _target.Modes.FirstOrDefault(m => m.Resolution == _resolution && m.RefreshHz == value);
            if (mode is not null) RequestMode(mode);
        }
    }

    private void RequestMode(DisplayMode mode)
    {
        var previous = _target.Current;
        if (mode == previous) return;
        _owner.Request(new ControlChange
        {
            Title = $"Change {_target.Label} to {mode.Width} × {mode.Height} at {mode.RefreshHz} Hz?",
            Detail = "If the screen goes blank, wait 15 seconds and the previous mode comes back automatically.",
            AutoRevert = true,
            Apply = () => Task.Run(() => DisplayControl.SetMode(_target.DeviceName, mode)),
            Revert = () => Task.Run(() => DisplayControl.SetMode(_target.DeviceName, previous)),
            Resync = Resync,
        });
    }

    public void Resync()
    {
        var fresh = DisplayControl.Displays().FirstOrDefault(d => d.DeviceName == _target.DeviceName);
        if (fresh is not null) Load(fresh);
    }
}

public sealed class SwitchItemViewModel : ObservableObject
{
    private bool _isOn;
    private bool _suppress;
    private readonly Func<bool, ControlChange> _makeChange;
    private readonly ControlCenterViewModel _owner;

    public SwitchItemViewModel(string name, string detail, string group, bool isOn, ControlCenterViewModel owner, Func<bool, ControlChange> makeChange)
    {
        Name = name;
        Detail = detail;
        Group = group;
        _isOn = isOn;
        _owner = owner;
        _makeChange = makeChange;
    }

    public string Name { get; }
    public string Detail { get; }
    public string Group { get; }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!Set(ref _isOn, value) || _suppress) return;
            _owner.Request(_makeChange(value));
        }
    }

    /// <summary>Sets the switch without triggering a change request (used after cancel / failure / undo).</summary>
    public void SetSilently(bool value)
    {
        _suppress = true;
        IsOn = value;
        _suppress = false;
    }
}

/// <summary>
/// Control Center: changes system settings directly, with confirm-before-apply for impactful changes,
/// a 15-second Undo afterwards, and auto-revert for display modes. Every change is logged.
/// </summary>
public sealed class ControlCenterViewModel : PageViewModel
{
    private const int UndoSeconds = 15;
    private const double SliderSessionSeconds = 4;
    private string? _lastHistoryKey;
    private DateTime _lastHistoryAt;
    private readonly IRadioService _radios;
    private bool _suppress = true;

    private ControlChange? _pending;
    private ControlChange? _lastApplied;
    private int _undoGeneration;
    private int _undoRemaining;
    private string _undoText = "";
    private bool _isApplying;

    private readonly SensorHub? _hub;

    public ControlCenterViewModel(IRadioService radios, SensorHub? hub = null) : base("Control", "\uE9E9")
    {
        _radios = radios;
        _hub = hub;
        ConfirmCommand = new RelayCommand(async () => await ConfirmAsync());
        CancelCommand = new RelayCommand(CancelPending);
        UndoCommand = new RelayCommand(async () => await UndoAsync());
        KeepCommand = new RelayCommand(Keep);
        BatteryReportCommand = new RelayCommand(async () => await BatteryReportAsync());
    }

    // ───────────── Battery report ─────────────

    public RelayCommand BatteryReportCommand { get; }
    private string _batteryReportStatus = "";
    public string BatteryReportStatus { get => _batteryReportStatus; private set => Set(ref _batteryReportStatus, value); }

    /// <summary>
    /// Windows' own battery report (powercfg /batteryreport): design vs full-charge capacity history,
    /// cycle count, recent usage and estimated life. Saved to Documents\CoreScope and opened in the browser.
    /// </summary>
    private async Task BatteryReportAsync()
    {
        BatteryReportStatus = "Creating battery report…";
        var path = System.IO.Path.Combine(SensorRecorder.DefaultFolder, "battery-report.html");
        try
        {
            System.IO.Directory.CreateDirectory(SensorRecorder.DefaultFolder);
            int code = await Task.Run(() =>
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powercfg.exe", $"/batteryreport /output \"{path}\"")
                {
                    CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                });
                if (p is null) return -1;
                p.StandardOutput.ReadToEnd();
                return p.WaitForExit(30000) ? p.ExitCode : -2;
            });
            if (code == 0 && System.IO.File.Exists(path))
            {
                BatteryReportStatus = "Saved to Documents\\CoreScope\\battery-report.html";
                Shell.Open(path);
            }
            else BatteryReportStatus = $"Windows couldn't create the report (powercfg exit {code}).";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException)
        {
            Log.Error("Battery report", ex);
            BatteryReportStatus = "Windows couldn't create the report.";
        }
    }

    // ───────────── Confirm / undo bar ─────────────

    public RelayCommand ConfirmCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand KeepCommand { get; }

    public bool HasPending => _pending is not null;
    public string PendingTitle => _pending?.Title ?? "";
    public string PendingDetail => _pending?.Detail ?? "";
    public bool IsApplying { get => _isApplying; private set => Set(ref _isApplying, value); }

    public bool HasUndo => _lastApplied is not null && _undoRemaining > 0;
    public bool IsAutoRevert => _lastApplied?.AutoRevert == true;
    public string UndoText { get => _undoText; private set => Set(ref _undoText, value); }
    public ObservableCollection<string> History { get; } = new();

    public void Request(ControlChange change)
    {
        if (_suppress) return;
        if (_pending is not null) CancelPending();
        if (!change.NeedsConfirm)
        {
            _ = ApplyAsync(change);
            return;
        }
        _pending = change;
        RaisePending();
    }

    private void RaisePending()
    {
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(PendingTitle));
        OnPropertyChanged(nameof(PendingDetail));
    }

    private async Task ConfirmAsync()
    {
        if (_pending is not { } change) return;
        _pending = null;
        RaisePending();
        await ApplyAsync(change);
    }

    private void CancelPending()
    {
        var change = _pending;
        _pending = null;
        RaisePending();
        change?.Resync?.Invoke();
    }

    private async Task ApplyAsync(ControlChange change)
    {
        IsApplying = true;
        bool ok;
        try { ok = await change.Apply(); }
        catch (Exception ex) { Log.Error($"Applying '{change.Title}'", ex); ok = false; }
        IsApplying = false;

        var label = change.Title.TrimEnd('?');
        if (!ok)
        {
            History.Insert(0, $"{DateTime.Now:HH:mm}  Failed: {label}");
            UndoText = $"Couldn't apply: {label}. Nothing was changed.";
            change.Resync?.Invoke();
            StartUndoWindow(null);
            return;
        }
        var line = $"{DateTime.Now:HH:mm}  {label}";
        if (change.CoalesceKey is { } key && key == _lastHistoryKey && (DateTime.Now - _lastHistoryAt).TotalSeconds < SliderSessionSeconds && History.Count > 0)
            History[0] = line;
        else
            History.Insert(0, line);
        _lastHistoryKey = change.CoalesceKey;
        _lastHistoryAt = DateTime.Now;
        StartUndoWindow(change);
    }

    private void StartUndoWindow(ControlChange? change)
    {
        _lastApplied = change;
        int generation = ++_undoGeneration;
        _undoRemaining = UndoSeconds;
        _ = CountdownAsync(generation);
        UpdateUndoText();
    }

    private async Task CountdownAsync(int generation)
    {
        while (_undoRemaining > 0)
        {
            await Task.Delay(1000);
            if (generation != _undoGeneration) return;
            _undoRemaining--;
            UpdateUndoText();
        }
        if (generation == _undoGeneration && _lastApplied is { AutoRevert: true }) await UndoAsync(); // nobody confirmed the display mode
        if (generation == _undoGeneration) { _lastApplied = null; UndoText = ""; RaiseUndo(); }
    }

    private void UpdateUndoText()
    {
        if (_lastApplied is { } c)
            UndoText = c.AutoRevert
                ? $"Keep these display settings? Reverting in {_undoRemaining} s."
                : $"Done: {c.Title.TrimEnd('?')}";
        RaiseUndo();
    }

    private void RaiseUndo()
    {
        OnPropertyChanged(nameof(HasUndo));
        OnPropertyChanged(nameof(IsAutoRevert));
    }

    private async Task UndoAsync()
    {
        if (_lastApplied is not { } change) return;
        _lastApplied = null;
        _undoGeneration++;
        _undoRemaining = 0;
        bool ok;
        try { ok = await change.Revert(); }
        catch (Exception ex) { Log.Error($"Reverting '{change.Title}'", ex); ok = false; }
        History.Insert(0, $"{DateTime.Now:HH:mm}  {(ok ? "Undid" : "Couldn't undo")}: {change.Title.TrimEnd('?')}");
        change.Resync?.Invoke();
        UndoText = ok ? "Change undone." : "Couldn't undo automatically — check the setting in Windows Settings.";
        RaiseUndo();
    }

    private void Keep()
    {
        _undoGeneration++;
        _undoRemaining = 0;
        _lastApplied = null;
        UndoText = "Display settings kept.";
        RaiseUndo();
    }

    // ───────────── Display & brightness ─────────────

    public ObservableCollection<DisplayItemViewModel> Displays { get; } = new();
    private int _brightness;
    private int _committedBrightness;
    private int _brightnessSessionStart;
    private DateTime _brightnessChangedAt;
    public bool HasBrightness { get; private set; }

    public int Brightness
    {
        get => _brightness;
        set
        {
            if (!Set(ref _brightness, value) || _suppress) return;
            if ((DateTime.Now - _brightnessChangedAt).TotalSeconds > SliderSessionSeconds) _brightnessSessionStart = _committedBrightness;
            _brightnessChangedAt = DateTime.Now;
            int previous = _brightnessSessionStart, target = value;
            _committedBrightness = value;
            Request(new ControlChange
            {
                Title = $"Brightness {previous}% → {target}%",
                NeedsConfirm = false,
                CoalesceKey = "brightness",
                Apply = () => Task.Run(() => DisplayControl.SetBrightness(target)),
                Revert = () => Task.Run(() => DisplayControl.SetBrightness(previous)),
                Resync = ResyncBrightness,
            });
        }
    }

    private void ResyncBrightness()
    {
        var b = DisplayControl.Brightness();
        _suppress = true;
        HasBrightness = b is not null;
        Brightness = b ?? 0;
        _committedBrightness = Brightness;
        OnPropertyChanged(nameof(HasBrightness));
        _suppress = false;
    }

    // ───────────── Power ─────────────

    public List<PowerModeOption> PowerModes { get; } = new()
    {
        new("Best power efficiency", "Quieter and cooler, longest battery life", PowerControl.ModeBestEfficiency),
        new("Balanced", "Windows default", PowerControl.ModeBalanced),
        new("Best performance", "Highest clocks, more fan noise and heat", PowerControl.ModeBestPerformance),
    };

    private PowerModeOption? _powerMode;
    public bool HasPowerMode { get; private set; }

    public PowerModeOption? PowerMode
    {
        get => _powerMode;
        set
        {
            var previous = _powerMode;
            if (!Set(ref _powerMode, value) || _suppress || value is null || previous is null) return;
            Request(new ControlChange
            {
                Title = $"Switch power mode to \"{value.Label}\"?",
                Detail = value.Detail,
                Apply = () => Task.Run(() => PowerControl.SetPowerMode(value.Id)),
                Revert = () => Task.Run(() => PowerControl.SetPowerMode(previous.Id)),
                Resync = ResyncPower,
            });
        }
    }

    public ObservableCollection<PowerPlan> Plans { get; } = new();
    private PowerPlan? _plan;

    public PowerPlan? Plan
    {
        get => _plan;
        set
        {
            var previous = _plan;
            if (!Set(ref _plan, value) || _suppress || value is null || previous is null) return;
            Request(new ControlChange
            {
                Title = $"Switch power plan to \"{value.Name}\"?",
                Detail = "Windows 11's power mode (above) only works while the Balanced plan is active.",
                Apply = () => Task.Run(() => PowerControl.SetActivePlan(value.Id)),
                Revert = () => Task.Run(() => PowerControl.SetActivePlan(previous.Id)),
                Resync = ResyncPower,
            });
        }
    }

    private bool _boost;
    private uint _boostOnValue = 2;
    public bool HasBoost { get; private set; }

    public bool BoostEnabled
    {
        get => _boost;
        set
        {
            if (!Set(ref _boost, value) || _suppress) return;
            uint target = value ? _boostOnValue : 0, previous = value ? 0 : _boostOnValue;
            Request(new ControlChange
            {
                Title = value ? "Turn CPU boost on?" : "Turn CPU boost off?",
                Detail = value
                    ? "Restores full burst performance."
                    : "Caps the CPU at its base clock: much cooler and quieter, longer battery life, but slower in heavy tasks.",
                Apply = () => Task.Run(() => PowerControl.SetBoostMode(target)),
                Revert = () => Task.Run(() => PowerControl.SetBoostMode(previous)),
                Resync = ResyncPower,
            });
        }
    }

    public List<TimeoutOption> Timeouts { get; } = TimeoutOption.Standard.ToList();
    public bool HasBattery { get; private set; }

    private TimeoutOption? _screenAc, _screenDc, _sleepAc, _sleepDc;
    public TimeoutOption? ScreenOffPluggedIn { get => _screenAc; set => SetTimeout(ref _screenAc, value, PowerControl.Timeout.ScreenOff, true); }
    public TimeoutOption? ScreenOffOnBattery { get => _screenDc; set => SetTimeout(ref _screenDc, value, PowerControl.Timeout.ScreenOff, false); }
    public TimeoutOption? SleepPluggedIn { get => _sleepAc; set => SetTimeout(ref _sleepAc, value, PowerControl.Timeout.Sleep, true); }
    public TimeoutOption? SleepOnBattery { get => _sleepDc; set => SetTimeout(ref _sleepDc, value, PowerControl.Timeout.Sleep, false); }

    private void SetTimeout(ref TimeoutOption? field, TimeoutOption? value, PowerControl.Timeout which, bool ac,
                            [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        var previous = field;
        if (!Set(ref field, value, name) || _suppress || value is null || previous is null) return;
        var what = which == PowerControl.Timeout.ScreenOff ? "Turn screen off" : "Sleep";
        var when = ac ? "when plugged in" : "on battery";
        Request(new ControlChange
        {
            Title = $"{what} after {value.Label.ToLowerInvariant()} {when}?",
            Apply = () => Task.Run(() => PowerControl.SetTimeout(which, ac, value.Seconds)),
            Revert = () => Task.Run(() => PowerControl.SetTimeout(which, ac, previous.Seconds)),
            Resync = ResyncPower,
        });
    }

    private TimeoutOption OptionFor(uint? seconds)
    {
        var s = seconds ?? 0;
        var match = Timeouts.FirstOrDefault(t => t.Seconds == s);
        if (match is not null) return match;
        var custom = new TimeoutOption(s % 60 == 0 ? $"{s / 60} minutes" : $"{s} seconds", s);
        Timeouts.Insert(Timeouts.Count - 1, custom);
        OnPropertyChanged(nameof(Timeouts));
        return custom;
    }

    private void ResyncPower()
    {
        _suppress = true;
        var activeId = PowerControl.ActivePlan();
        Plans.Clear();
        foreach (var p in PowerControl.Plans()) Plans.Add(p);
        Plan = Plans.FirstOrDefault(p => p.Id == activeId);

        var mode = PowerControl.PowerMode();
        HasPowerMode = mode is not null;
        PowerMode = PowerModes.FirstOrDefault(m => m.Id == mode) ?? PowerModes[1];

        var boost = PowerControl.BoostMode(ac: true);
        HasBoost = boost is not null;
        if (boost is > 0) _boostOnValue = boost.Value;
        BoostEnabled = boost is > 0;

        ScreenOffPluggedIn = OptionFor(PowerControl.GetTimeout(PowerControl.Timeout.ScreenOff, true));
        ScreenOffOnBattery = OptionFor(PowerControl.GetTimeout(PowerControl.Timeout.ScreenOff, false));
        SleepPluggedIn = OptionFor(PowerControl.GetTimeout(PowerControl.Timeout.Sleep, true));
        SleepOnBattery = OptionFor(PowerControl.GetTimeout(PowerControl.Timeout.Sleep, false));

        OnPropertyChanged(nameof(HasPowerMode));
        OnPropertyChanged(nameof(HasBoost));
        _suppress = false;
    }

    // ───────────── Battery, radios, audio ─────────────

    public SwitchItemViewModel? Conservation { get; private set; }
    public bool HasConservation => Conservation is not null;
    public ObservableCollection<SwitchItemViewModel> Radios { get; } = new();

    private int _volume;
    private int _committedVolume;
    private int _volumeSessionStart;
    private DateTime _volumeChangedAt;
    public bool HasAudio { get; private set; }

    public int Volume
    {
        get => _volume;
        set
        {
            if (!Set(ref _volume, value) || _suppress) return;
            if ((DateTime.Now - _volumeChangedAt).TotalSeconds > SliderSessionSeconds) _volumeSessionStart = _committedVolume;
            _volumeChangedAt = DateTime.Now;
            int previous = _volumeSessionStart, target = value;
            _committedVolume = value;
            Request(new ControlChange
            {
                Title = $"Volume {previous}% → {target}%",
                NeedsConfirm = false,
                CoalesceKey = "volume",
                Apply = () => Task.Run(() => AudioControl.SetVolume(target)),
                Revert = () => Task.Run(() => AudioControl.SetVolume(previous)),
                Resync = ResyncAudio,
            });
        }
    }

    private bool _muted;
    public bool Muted
    {
        get => _muted;
        set
        {
            if (!Set(ref _muted, value) || _suppress) return;
            bool target = value;
            Request(new ControlChange
            {
                Title = target ? "Mute" : "Unmute",
                NeedsConfirm = false,
                Apply = () => Task.Run(() => AudioControl.SetMuted(target)),
                Revert = () => Task.Run(() => AudioControl.SetMuted(!target)),
                Resync = ResyncAudio,
            });
        }
    }

    private void ResyncAudio()
    {
        _suppress = true;
        var v = AudioControl.Volume();
        HasAudio = v is not null;
        Volume = v ?? 0;
        _committedVolume = Volume;
        Muted = AudioControl.Muted() ?? false;
        OnPropertyChanged(nameof(HasAudio));
        _suppress = false;
    }

    // ───────────── Fans (desktop boards only) ─────────────

    public List<ChoiceOption<SensorHub.FanPolicy>> FanModes { get; } = new()
    {
        new("Automatic (BIOS)", SensorHub.FanPolicy.Auto),
        new("Fixed speed", SensorHub.FanPolicy.Fixed),
        new("Temperature curve (CPU)", SensorHub.FanPolicy.Curve),
    };

    private ChoiceOption<SensorHub.FanPolicy>? _fanMode;
    private int _fanPercent = 50;
    public bool HasFans => _hub?.ControllableFans.Count > 0;
    public string FanNames => _hub is null ? "" : string.Join(", ", _hub.ControllableFans);
    public bool IsFixedFan => _fanMode?.Value == SensorHub.FanPolicy.Fixed;

    public ChoiceOption<SensorHub.FanPolicy>? FanMode
    {
        get => _fanMode;
        set
        {
            var previous = _fanMode;
            if (!Set(ref _fanMode, value) || _suppress || value is null || previous is null || _hub is null) return;
            OnPropertyChanged(nameof(IsFixedFan));
            Request(new ControlChange
            {
                Title = $"Switch fans to \"{value.Label}\"?",
                Detail = value.Value switch
                {
                    SensorHub.FanPolicy.Fixed => "Fans hold the speed you set (minimum 20%). Keep an eye on temperatures under load.",
                    SensorHub.FanPolicy.Curve => "Quiet at idle, ramps to 100% by 85 °C CPU. Falls back to full speed if the temperature can't be read.",
                    _ => "Hands fan control back to the motherboard.",
                },
                Apply = () => Task.FromResult(SetFans(value.Value)),
                Revert = () => Task.FromResult(SetFans(previous.Value)),
                Resync = () => { _suppress = true; FanMode = previous; _suppress = false; OnPropertyChanged(nameof(IsFixedFan)); },
            });
        }
    }

    public int FanPercent
    {
        get => _fanPercent;
        set
        {
            if (!Set(ref _fanPercent, Math.Clamp(value, 20, 100)) || _suppress || _hub is null) return;
            if (_fanMode?.Value == SensorHub.FanPolicy.Fixed) _hub.SetFanPolicy(SensorHub.FanPolicy.Fixed, _fanPercent);
        }
    }

    private bool SetFans(SensorHub.FanPolicy policy)
    {
        _hub?.SetFanPolicy(policy, _fanPercent);
        return _hub is not null;
    }

    // ───────────── Devices & startup apps ─────────────

    public ObservableCollection<SwitchItemViewModel> Devices { get; } = new();
    public ObservableCollection<SwitchItemViewModel> StartupItems { get; } = new();
    public string StartupSummary { get; private set; } = "";

    // ───────────── Windows Settings shortcuts (for everything not controlled here) ─────────────

    public List<SettingsLink> SettingsLinks { get; } = new()
    {
        new("Night light", "ms-settings:nightlight", ""),
        new("Display scale & HDR", "ms-settings:display", ""),
        new("Default sound device", "ms-settings:sound", ""),
        new("Bluetooth devices", "ms-settings:bluetooth", ""),
        new("Battery saver", "ms-settings:batterysaver", ""),
        new("Graphics (per-app GPU)", "ms-settings:display-advancedgraphics", ""),
        new("Store-app startup tasks", "ms-settings:startupapps", ""),
        new("Camera privacy", "ms-settings:privacy-webcam", ""),
    };

    // ───────────── Loading ─────────────

    public async Task LoadAsync(SystemSpec spec)
    {
        _suppress = true;
        try
        {
            Displays.Clear();
            foreach (var d in await Task.Run(DisplayControl.Displays)) Displays.Add(new DisplayItemViewModel(d, this));
            ResyncBrightness();
            HasBattery = spec.Battery is not null;
            OnPropertyChanged(nameof(HasBattery));
            ResyncPower();
            ResyncAudio();

            if (await Task.Run(BatteryControl.ConservationEnabled) is { } conservation)
            {
                SwitchItemViewModel? item = null;
                item = new SwitchItemViewModel("Conservation mode",
                    "Stops charging at about 60–80% (model-dependent) to slow battery wear when you're mostly plugged in.",
                    "Battery", conservation, this, on => new ControlChange
                    {
                        Title = on ? "Turn on battery Conservation mode?" : "Turn off Conservation mode?",
                        Detail = on ? "The battery will stop charging around 60–80%. Turn it off before a trip to get a full charge."
                                    : "The battery will charge to 100% again.",
                        Apply = () => Task.Run(() => BatteryControl.SetConservation(on)),
                        Revert = () => Task.Run(() => BatteryControl.SetConservation(!on)),
                        Resync = () => item!.SetSilently(BatteryControl.ConservationEnabled() ?? false),
                    });
                Conservation = item;
                OnPropertyChanged(nameof(Conservation));
                OnPropertyChanged(nameof(HasConservation));
            }

            _fanMode = FanModes[0]; // always start in BIOS mode: custom fan control is opt-in per session
            OnPropertyChanged(nameof(FanMode));
            OnPropertyChanged(nameof(HasFans));
            OnPropertyChanged(nameof(FanNames));
            OnPropertyChanged(nameof(IsFixedFan));

            await LoadRadiosAsync();
            LoadDevices(spec.Devices);
            await LoadStartupAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Loading Control Center", ex);
        }
        finally
        {
            _suppress = false;
        }
    }

    private async Task LoadRadiosAsync()
    {
        Radios.Clear();
        List<RadioInfo> radios;
        try { radios = await _radios.GetAsync(); }
        catch (Exception ex) { Log.Error("Reading radios", ex); return; }

        foreach (var radio in radios)
        {
            SwitchItemViewModel? item = null;
            item = new SwitchItemViewModel(radio.Kind, radio.Name, "Radios", radio.IsOn, this, on => new ControlChange
            {
                Title = $"Turn {radio.Kind} {(on ? "on" : "off")}?",
                Detail = on ? "" : radio.Kind == "Bluetooth" ? "Bluetooth mice, keyboards and headphones will disconnect." : "You'll be disconnected from Wi-Fi.",
                Apply = () => _radios.SetAsync(radio.Kind, on),
                Revert = () => _radios.SetAsync(radio.Kind, !on),
                Resync = async () =>
                {
                    var now = (await _radios.GetAsync()).FirstOrDefault(r => r.Kind == radio.Kind);
                    if (now is not null) item!.SetSilently(now.IsOn);
                },
            });
            Radios.Add(item);
        }
    }

    private void LoadDevices(IReadOnlyList<DeviceCategory> categories)
    {
        Devices.Clear();
        foreach (var d in DeviceControl.Candidates(categories))
        {
            SwitchItemViewModel? item = null;
            item = new SwitchItemViewModel(d.Name, d.Category, d.Category, d.Enabled, this, on => new ControlChange
            {
                Title = $"{(on ? "Turn on" : "Turn off")} {d.Name}?",
                Detail = on ? "" : d.Warning,
                Apply = () => Task.Run(() => DeviceControl.SetEnabled(d.DeviceId, on)),
                Revert = () => Task.Run(() => DeviceControl.SetEnabled(d.DeviceId, !on)),
                // Resync runs after cancel, failure or undo — in every case the device is back to !on.
                Resync = () => item!.SetSilently(!on),
            });
            Devices.Add(item);
        }
    }

    private async Task LoadStartupAsync()
    {
        StartupItems.Clear();
        var apps = await Task.Run(StartupApps.List);
        foreach (var app in apps)
        {
            var detail = string.Join(" · ", new[] { app.Publisher, app.Location }.Where(s => s.Length > 0));
            SwitchItemViewModel? item = null;
            item = new SwitchItemViewModel(app.Name, detail, "Startup", app.Enabled, this, on => new ControlChange
            {
                Title = $"{(on ? "Start" : "Stop starting")} {app.Name} at sign-in?",
                Detail = on ? "" : "The app stays installed; it just won't launch automatically. You can still open it yourself.",
                Apply = () => Task.Run(() => StartupApps.SetEnabled(app, on)),
                Revert = () => Task.Run(() => StartupApps.SetEnabled(app, !on)),
                Resync = () => item!.SetSilently(!on),
            });
            StartupItems.Add(item);
        }
        int enabled = apps.Count(a => a.Enabled);
        StartupSummary = apps.Count == 0 ? "No desktop startup apps found." : $"{enabled} of {apps.Count} apps start when you sign in.";
        OnPropertyChanged(nameof(StartupSummary));
    }
}
