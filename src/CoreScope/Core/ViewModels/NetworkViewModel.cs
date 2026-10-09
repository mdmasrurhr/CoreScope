using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreScope.Core.Hardware;
using CoreScope.Core.Network;

namespace CoreScope.Core.ViewModels;

/// <summary>Runs a finding's one-click fix from any page (Insights or Network) and reports the result.</summary>
public static class InsightActions
{
    public static event Action<Insight, bool, string>? Applied;
    public static event Action<Insight>? Dismissed;

    /// <summary>Stable key: category + title with numbers removed, so "Weak signal (45%)" and "(41%)" match.</summary>
    public static string KeyOf(Insight i) => i.Category + ":" + System.Text.RegularExpressions.Regex.Replace(i.Title, @"[\d.,%]+", "#");

    public static bool IsDismissed(Insight i)
    {
        var map = AppSettings.Current.DismissedInsights;
        if (!map.TryGetValue(KeyOf(i), out var until)) return false;
        if (until > DateTime.Now) return true;
        map.Remove(KeyOf(i));
        return false;
    }

    private static void SetDismissed(object? parameter, DateTime? until)
    {
        if (parameter is not Insight insight) return;
        var map = AppSettings.Current.DismissedInsights;
        if (until is { } u) map[KeyOf(insight)] = u; else map.Remove(KeyOf(insight));
        AppSettings.Current.Save();
        Dismissed?.Invoke(insight);
    }

    /// <summary>Lets the coordinator tell listeners (the Network page re-scans) that a fix ran.</summary>
    public static void Report(Insight insight, bool ok, string message) => Applied?.Invoke(insight, ok, message);

    /// <summary>The command behind every fix button; parameter is a <see cref="Fixes.FixButton"/>.</summary>
    public static RelayCommand RunFix { get; } = new(p => { if (p is Fixes.FixButton button) Coordinator?.Run(button); });

    public static Fixes.FixCoordinator? Coordinator { get; set; }

    public static RelayCommand SnoozeWeek { get; } = new(p => SetDismissed(p, DateTime.Now.AddDays(7)));
    public static RelayCommand DismissForever { get; } = new(p => SetDismissed(p, DateTime.MaxValue));
    public static RelayCommand Restore { get; } = new(p => SetDismissed(p, null));

    public static RelayCommand Fix { get; } = new(parameter =>
    {
        if (parameter is not Insight { FixKey: { } key } insight) return;
        var (ok, message) = NetworkFixes.Apply(key);
        Applied?.Invoke(insight, ok, message);
    });
}

/// <summary>
/// Network page: what you're connected to, live speed/ping tests, and findings with one-click fixes for the
/// settings behind "full signal, slow internet".
/// </summary>
public sealed class NetworkViewModel : PageViewModel
{
    private readonly SynchronizationContext _ui;
    private NetworkSnapshot? _snapshot;
    private SpeedResult? _speed;
    private PingStats? _routerPing, _internetPing;
    private bool _isScanning, _isTesting;
    private string _summary = "Checking your connection…";
    private string _speedText = "—", _speedDetail = "Not tested yet", _routerText = "—", _internetText = "—";
    private string _testStatus = "";
    private string _fixMessage = "";
    private bool _fixOk;
    private DateTime _lastWifiScan;

    public NetworkViewModel(SynchronizationContext ui) : base("Network", "")
    {
        _ui = ui;
        RunTestCommand = new RelayCommand(async () => await RunTestAsync());
        RescanCommand = new RelayCommand(async () => await ScanAsync());
        FlushDnsCommand = new RelayCommand(() => ShowFix(NetworkFixes.Apply(NetworkFixes.FlushDns)));
        ToggleRscCommand = new RelayCommand(async () =>
        {
            ShowFix(NetworkFixes.Apply(_snapshot?.Tcp.RscEnabled == false ? NetworkFixes.EnableRsc : NetworkFixes.DisableRsc));
            await ScanAsync();
        });
        AutoTuningNormalCommand = new RelayCommand(async () =>
        {
            ShowFix(NetworkFixes.Apply(NetworkFixes.AutoTuningNormal));
            await ScanAsync();
        });
        ScanWifiCommand = new RelayCommand(async () => await ScanWifiAsync());
        OpenLocationSettingsCommand = new RelayCommand(() => Shell.Open("ms-settings:privacy-location"));
        InsightActions.Applied += (insight, ok, message) =>
        {
            if (insight.Category != "Network") return;
            _ui.Post(async _ => { ShowFix((ok, message)); await ScanAsync(); }, null);
        };
    }

    public RelayCommand RunTestCommand { get; }
    public RelayCommand RescanCommand { get; }
    public RelayCommand FlushDnsCommand { get; }
    public RelayCommand ToggleRscCommand { get; }
    public RelayCommand AutoTuningNormalCommand { get; }
    public RelayCommand ScanWifiCommand { get; }
    public RelayCommand OpenLocationSettingsCommand { get; }

    // ───── Wi-Fi analyzer ─────
    private WifiScan? _wifi;
    private bool _isScanningWifi;
    public bool IsScanningWifi { get => _isScanningWifi; private set => Set(ref _isScanningWifi, value); }
    public bool HasWifi => _wifi is not null;
    public ObservableCollection<WifiNetwork> NearbyNetworks { get; } = new();
    public ObservableCollection<ChannelLoad> Channels24 { get; } = new();
    public ObservableCollection<ChannelLoad> Channels5 { get; } = new();
    public string WifiSummary => _wifi?.Summary ?? "";
    public string WifiRecommendation => _wifi?.Recommendation ?? "";
    public string WifiProblem => _wifi?.Problem ?? "";
    public bool WifiNeedsLocation => _wifi?.NeedsLocation == true;
    public bool WifiCrowded => _wifi?.Crowded == true;

    public async Task ScanWifiAsync()
    {
        if (IsScanningWifi) return;
        IsScanningWifi = true;
        try
        {
            var ssid = _snapshot?.Wifi?.Ssid;
            var channel = _snapshot?.Wifi?.Channel;
            _wifi = await Task.Run(() => WifiAnalyzer.Scan(ssid, channel));
            NearbyNetworks.Clear();
            foreach (var n in _wifi.Networks.OrderByDescending(n => n.IsYours).ThenByDescending(n => n.Signal).Take(20)) NearbyNetworks.Add(n);
            Channels24.Clear();
            foreach (var c in _wifi.Channels24) Channels24.Add(c);
            Channels5.Clear();
            foreach (var c in _wifi.Channels5) Channels5.Add(c);
            foreach (var name in new[] { nameof(HasWifi), nameof(WifiSummary), nameof(WifiRecommendation), nameof(WifiProblem), nameof(WifiNeedsLocation), nameof(WifiCrowded) })
                OnPropertyChanged(name);
            Evaluate();
        }
        catch (Exception ex)
        {
            Log.Error("Wi-Fi scan", ex);
        }
        finally
        {
            IsScanningWifi = false;
        }
    }

    public ObservableCollection<SpecRow> Connection { get; } = new();
    public ObservableCollection<Insight> Findings { get; } = new();
    /// <summary>Latest findings, merged into the Insights page.</summary>
    public IReadOnlyList<Insight> LatestFindings { get; private set; } = Array.Empty<Insight>();
    public event Action? FindingsChanged;

    public bool IsScanning { get => _isScanning; private set => Set(ref _isScanning, value); }
    public bool IsTesting { get => _isTesting; private set { if (Set(ref _isTesting, value)) OnPropertyChanged(nameof(CanTest)); } }
    public bool CanTest => !_isTesting;
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }
    public string SpeedDetail { get => _speedDetail; private set => Set(ref _speedDetail, value); }
    public string RouterText { get => _routerText; private set => Set(ref _routerText, value); }
    public string InternetText { get => _internetText; private set => Set(ref _internetText, value); }
    public string TestStatus { get => _testStatus; private set => Set(ref _testStatus, value); }
    public string FixMessage { get => _fixMessage; private set => Set(ref _fixMessage, value); }
    public bool FixOk { get => _fixOk; private set => Set(ref _fixOk, value); }

    public string RscText => _snapshot?.Tcp.RscEnabled switch { true => "On", false => "Off", _ => "Unknown" };
    public string RscButtonText => _snapshot?.Tcp.RscEnabled == false ? "Turn on" : "Turn off";
    public string AutoTuningText => _snapshot?.Tcp.AutoTuning ?? "Unknown";
    public bool AutoTuningIsNormal => _snapshot?.Tcp.AutoTuning is "Normal" or null;

    private void ShowFix((bool Ok, string Message) result)
    {
        FixOk = result.Ok;
        FixMessage = result.Message;
    }

    public async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        try
        {
            _snapshot = await Task.Run(NetworkDiagnostics.Collect);
            Connection.Clear();
            foreach (var row in Rows(_snapshot)) Connection.Add(row);
            OnPropertyChanged(nameof(RscText));
            OnPropertyChanged(nameof(RscButtonText));
            OnPropertyChanged(nameof(AutoTuningText));
            OnPropertyChanged(nameof(AutoTuningIsNormal));
            Evaluate();
            if (_snapshot.Primary?.Kind == "Wi-Fi" && (_wifi is null || _lastWifiScan < DateTime.Now.AddMinutes(-2)))
            {
                _lastWifiScan = DateTime.Now;
                _ = ScanWifiAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Network scan", ex);
            Summary = "Couldn't read the network settings.";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private static IEnumerable<SpecRow> Rows(NetworkSnapshot s)
    {
        if (s.Primary is null)
        {
            yield return new SpecRow("Status", "Not connected");
            yield break;
        }
        var p = s.Primary;
        yield return new SpecRow("Connection", p.Kind);
        yield return new SpecRow("Adapter", Format.CleanName(p.Description));
        if (s.Wifi is { } w && p.Kind == "Wi-Fi")
        {
            if (w.Ssid.Length > 0) yield return new SpecRow("Network", w.Ssid);
            var radio = string.Join(" · ", new[] { w.Band, w.Channel is { } c ? $"channel {c}" : "", w.Radio }.Where(x => x.Length > 0));
            if (radio.Length > 0) yield return new SpecRow("Band", radio);
            if (w.SignalPercent is { } sig) yield return new SpecRow("Signal", $"{sig}%" + (w.Rssi is { } r ? $" ({r} dBm)" : ""));
            if (w.ReceiveMbps is { } rx) yield return new SpecRow("Link speed", $"{rx:0} Mbps down · {w.TransmitMbps ?? rx:0} Mbps up");
        }
        else if (p.LinkBps > 0)
        {
            yield return new SpecRow("Link speed", p.LinkBps >= 1_000_000_000 ? $"{p.LinkBps / 1e9:0.#} Gbps" : $"{p.LinkBps / 1e6:0} Mbps");
        }
        yield return new SpecRow("Router", p.Gateway);
        if (p.Dns.Count > 0) yield return new SpecRow("DNS servers", string.Join(", ", p.Dns.Take(3)));
        yield return new SpecRow("VPN", s.ActiveVpns.Count > 0 ? string.Join(", ", s.ActiveVpns) : "None");
        yield return new SpecRow("Proxy", s.Proxy ?? "None");
    }

    public async Task RunTestAsync()
    {
        if (IsTesting) return;
        IsTesting = true;
        try
        {
            if (_snapshot is null) await ScanAsync();
            var gateway = _snapshot?.Primary?.Gateway;

            TestStatus = "Pinging your router and the internet…";
            var routerTask = gateway is null ? Task.FromResult<PingStats?>(null) : NetworkDiagnostics.PingAsync(gateway, "Router").ContinueWith(t => (PingStats?)t.Result, TaskScheduler.Default);
            var internetTask = NetworkDiagnostics.PingAsync("1.1.1.1", "Internet");
            _routerPing = await routerTask;
            _internetPing = await internetTask;
            RouterText = Describe(_routerPing);
            InternetText = Describe(_internetPing);

            TestStatus = "Measuring download speed (about 10 seconds)…";
            SpeedText = "…";
            var progress = new Progress<double>(mbps => SpeedText = $"{mbps:0} Mbps");
            _speed = await NetworkDiagnostics.SpeedTestAsync(progress);
            if (_speed is null)
            {
                SpeedText = "—";
                SpeedDetail = "No test server could be reached. Check that you're online, or try again later.";
            }
            else
            {
                SpeedText = $"{_speed.Mbps:0} Mbps";
                SpeedDetail = $"Download · {_speed.Server} · {DateTime.Now:HH:mm}";
            }
            TestStatus = "";
            Evaluate();
        }
        catch (Exception ex)
        {
            Log.Error("Network test", ex);
            TestStatus = "The test stopped unexpectedly. Try again.";
        }
        finally
        {
            IsTesting = false;
        }
    }

    private static string Describe(PingStats? p) => p switch
    {
        null => "—",
        { Received: 0 } => "No reply",
        _ => $"{p.AverageMs:0} ms" + (p.LossPercent > 0 ? $" · {p.LossPercent:0}% lost" : "") + (p.JitterMs is { } j ? $" · ±{j:0} ms" : ""),
    };

    private void Evaluate()
    {
        if (_snapshot is null) return;
        var findings = NetworkDiagnostics.Analyze(_snapshot, _speed, _routerPing, _internetPing);
        if (_wifi is { Crowded: true, YourChannel: { } mine, BestChannel: { } best })
        {
            findings.RemoveAll(f => f.Severity == Severity.Good);
            findings.Add(new Insight(Severity.Info, "Network", "Your Wi-Fi channel is crowded",
                _wifi.Recommendation ?? "", $"In your router's settings, set the Wi-Fi channel to {best}.")
            { Measured = $"Channel {mine} vs channel {best}, {_wifi.Networks.Count} networks nearby", Rule = "Suggest a channel when it's at least twice as quiet", Source = "netsh wlan show networks mode=bssid" });
        }
        Findings.Clear();
        foreach (var f in findings) Findings.Add(f);
        LatestFindings = findings;

        int issues = findings.Count(f => f.Severity <= Severity.Warning);
        Badge = issues > 0 ? issues.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        Summary = _snapshot.Primary is null ? "Not connected"
            : issues > 0 ? $"{issues} issue{(issues == 1 ? "" : "s")} found"
            : _speed is null ? "Connected · run the speed test for a full check" : "Connected · no issues found";
        FindingsChanged?.Invoke();
    }
}
