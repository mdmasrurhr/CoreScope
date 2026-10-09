using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CoreScope.Core.Hardware;

namespace CoreScope.Core.Network;

public sealed record WifiLink(string Adapter, string Ssid, string Band, int? Channel, string Radio,
    int? SignalPercent, int? Rssi, double? ReceiveMbps, double? TransmitMbps);

public sealed record ActiveAdapter(string Name, string Description, string Kind, long LinkBps, string Gateway, IReadOnlyList<string> Dns);

/// <summary>TCP receive settings that can make a fast link download slowly. Null = couldn't read.</summary>
public sealed record TcpTuning(bool? RscEnabled, string? AutoTuning);

public sealed record PingStats(string Target, string Label, int Sent, int Received, double? AverageMs, double? JitterMs)
{
    public double LossPercent => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;
}

public sealed record SpeedResult(double Mbps, string Server, double Seconds, long Bytes);

public sealed class NetworkSnapshot
{
    public ActiveAdapter? Primary;
    public WifiLink? Wifi;
    public TcpTuning Tcp = new(null, null);
    public string? Proxy;
    public List<string> ActiveVpns = new();
    public bool IsAdmin;
}

/// <summary>
/// Network health checks: what the connection is, how fast the link is versus how fast data actually arrives,
/// and the Windows settings known to cause "full signal, slow internet" (RSC with some Realtek drivers,
/// restricted TCP auto-tuning, a leftover proxy or VPN).
/// </summary>
public static class NetworkDiagnostics
{
    private static readonly Regex VpnPattern = new(
        @"VPN|TAP-Windows|WireGuard|Wintun|OpenVPN|Cloudflare WARP|ZeroTier|Tailscale|NordLynx|Proton|Fortinet|Cisco AnyConnect|GlobalProtect|PANGP",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ───────────── Snapshot ─────────────

    public static NetworkSnapshot Collect()
    {
        var snap = new NetworkSnapshot { IsAdmin = Native.IsAdministrator() };
        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .ToList();

            snap.ActiveVpns = up.Where(n => VpnPattern.IsMatch(n.Description) || VpnPattern.IsMatch(n.Name) || n.NetworkInterfaceType == NetworkInterfaceType.Ppp)
                                .Select(n => n.Description).Distinct().ToList();

            var primary = up.Select(n => (n, props: n.GetIPProperties()))
                .Where(x => x.props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                .OrderBy(x => snap.ActiveVpns.Contains(x.n.Description) ? 1 : 0) // the physical adapter, not the VPN
                .FirstOrDefault();
            if (primary.n is not null)
            {
                var gw = primary.props.GatewayAddresses.First(g => g.Address.AddressFamily == AddressFamily.InterNetwork).Address.ToString();
                var kind = primary.n.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
                    _ => primary.n.NetworkInterfaceType.ToString(),
                };
                var dns = primary.props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
                snap.Primary = new ActiveAdapter(primary.n.Name, primary.n.Description, kind, primary.n.Speed, gw, dns);
            }
        }
        catch (NetworkInformationException ex)
        {
            Log.Error("Network interfaces", ex);
        }

        snap.Wifi = ReadWifi();
        snap.Tcp = ReadTcpTuning();
        snap.Proxy = ReadProxy();
        return snap;
    }

    /// <summary>`netsh wlan show interfaces`. Numbers and units are language-independent; labels are English-first with fallbacks.</summary>
    public static WifiLink? ReadWifi()
    {
        string output;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh", "wlan show interfaces")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            if (p is null) return null;
            output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("netsh wlan", ex);
            return null;
        }
        if (!Regex.IsMatch(output, @"^\s*SSID\s*:", RegexOptions.Multiline)) return null; // not connected / no Wi-Fi

        string Field(string label) => Regex.Match(output, $@"^\s*{label}\s*:\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value;
        double? Num(string s) => double.TryParse(Regex.Match(s, @"-?\d+(\.\d+)?").Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

        // Rates: the two lines with "(Mbps)" — receive first, transmit second, in every language.
        var rates = Regex.Matches(output, @"\(Mbps\)\s*:\s*([\d.,]+)").Select(m => Num(m.Groups[1].Value.Replace(',', '.'))).ToList();
        var signal = Regex.Match(output, @":\s*(\d{1,3})\s?%").Groups[1].Value;
        var band = Regex.Match(output, @":\s*(2\.4|5|6)\s*GHz", RegexOptions.IgnoreCase) is { Success: true } b ? b.Groups[1].Value + " GHz" : "";

        return new WifiLink(
            Field("Description") is { Length: > 0 } d ? d : Field("Name"),
            Field("SSID"),
            band,
            Num(Field("Channel")) is { } ch ? (int)ch : null,
            Regex.Match(output, @"802\.11[a-z]{1,2}").Value,
            int.TryParse(signal, out var sp) ? sp : null,
            Num(Field("Rssi")) is { } r ? (int)r : null,
            rates.Count > 0 ? rates[0] : null,
            rates.Count > 1 ? rates[1] : null);
    }

    /// <summary>RSC and auto-tuning from the NetTCPIP CIM classes (what `netsh int tcp show global` reports).</summary>
    public static TcpTuning ReadTcpTuning()
    {
        const string scope = @"root\StandardCimv2";
        bool? rsc = null;
        var offload = Wmi.First("SELECT ReceiveSegmentCoalescing FROM MSFT_NetOffloadGlobalSetting", scope);
        if (offload is not null) rsc = offload.Int("ReceiveSegmentCoalescing") == 1;

        string? tuning = null;
        var tcp = Wmi.First("SELECT AutoTuningLevelLocal FROM MSFT_NetTCPSetting WHERE SettingName='Internet'", scope);
        if (tcp is not null)
        {
            tuning = tcp.Int("AutoTuningLevelLocal") switch
            {
                0 => "Disabled", 1 => "HighlyRestricted", 2 => "Restricted", 3 => "Normal", 4 => "Experimental", _ => null,
            };
        }
        return new TcpTuning(rsc, tuning);
    }

    private static string? ReadProxy()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null) return null;
            if (key.GetValue("ProxyEnable") is int enabled && enabled == 1 && key.GetValue("ProxyServer") is string server && server.Length > 0)
                return server;
            if (key.GetValue("AutoConfigURL") is string pac && pac.Length > 0) return $"automatic script {pac}";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            Log.Error("Proxy settings", ex);
        }
        return null;
    }

    // ───────────── Live tests ─────────────

    public static async Task<PingStats> PingAsync(string target, string label, int count = 10, CancellationToken cancel = default)
    {
        var times = new List<long>();
        using var ping = new Ping();
        for (int i = 0; i < count && !cancel.IsCancellationRequested; i++)
        {
            try
            {
                var reply = await ping.SendPingAsync(target, 1000);
                if (reply.Status == IPStatus.Success) times.Add(reply.RoundtripTime);
            }
            catch (PingException) { }
            await Task.Delay(150, cancel).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        double? avg = times.Count > 0 ? times.Average() : null;
        double? jitter = times.Count > 1 ? times.Zip(times.Skip(1), (a, b) => Math.Abs(a - b)).Average() : null;
        return new PingStats(target, label, count, times.Count, avg, jitter);
    }

    /// <summary>Public test files, tried in order until one serves data. Big enough that we stop on time, not on size.</summary>
    private static readonly (string Name, string Url)[] Servers =
    {
        ("Cloudflare", "https://speed.cloudflare.com/__down?bytes=250000000"),
        ("OVHcloud (US)", "https://proof.ovh.us/files/1Gb.dat"),
        ("Hetzner (US)", "https://ash-speed.hetzner.com/1GB.bin"),
        ("OVHcloud (EU)", "https://proof.ovh.net/files/1Gb.dat"),
        ("Tele2 (EU)", "http://speedtest.tele2.net/1GB.zip"),
    };

    /// <summary>
    /// Download speed with 4 parallel streams for about 8 seconds; the first second is ignored (TCP ramp-up).
    /// Progress reports the running Mbps.
    /// </summary>
    public static async Task<SpeedResult?> SpeedTestAsync(IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        using var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None, PooledConnectionLifetime = TimeSpan.FromMinutes(1) })
        { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) CoreScope/1.0");

        foreach (var (name, url) in Servers)
        {
            if (cancel.IsCancellationRequested) return null;
            var result = await TryServerAsync(http, name, url, progress, cancel);
            if (result is not null) return result;
        }
        return null;
    }

    private static async Task<SpeedResult?> TryServerAsync(HttpClient http, string name, string url, IProgress<double>? progress, CancellationToken cancel)
    {
        const int streams = 4;
        var total = TimeSpan.FromSeconds(9);
        var warmup = TimeSpan.FromSeconds(1);
        long countedBytes = 0, allBytes = 0;
        var clock = Stopwatch.StartNew();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stop.CancelAfter(total);
        int failures = 0;

        async Task Stream()
        {
            var buffer = new byte[64 * 1024];
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stop.Token);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Info($"Speed test {name}: HTTP {(int)response.StatusCode}");
                    Interlocked.Increment(ref failures);
                    return;
                }
                await using var body = await response.Content.ReadAsStreamAsync(stop.Token);
                int read;
                while ((read = await body.ReadAsync(buffer, stop.Token)) > 0)
                {
                    Interlocked.Add(ref allBytes, read);
                    if (clock.Elapsed > warmup) Interlocked.Add(ref countedBytes, read);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException)
            {
                Log.Info($"Speed test {name}: {ex.Message}");
                Interlocked.Increment(ref failures);
            }
        }

        var tasks = Enumerable.Range(0, streams).Select(_ => Stream()).ToList();
        var reporter = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(500).ConfigureAwait(false);
                var secs = (clock.Elapsed - warmup).TotalSeconds;
                if (secs > 0.3) progress?.Report(Interlocked.Read(ref countedBytes) * 8 / secs / 1e6);
                if (Volatile.Read(ref failures) >= streams) break;
            }
        });
        await Task.WhenAll(tasks);
        stop.Cancel();
        await reporter;

        var seconds = Math.Max(0.001, (clock.Elapsed - warmup).TotalSeconds);
        if (Interlocked.Read(ref allBytes) < 256 * 1024 || failures >= streams) return null; // server refused or unreachable → next
        var mbps = countedBytes * 8 / seconds / 1e6;
        Log.Info($"Speed test {name}: {mbps:0.0} Mbps ({allBytes / 1e6:0} MB in {clock.Elapsed.TotalSeconds:0.0} s)");
        return new SpeedResult(mbps, name, seconds, allBytes);
    }

    // ───────────── Findings ─────────────

    /// <summary>Plain-English findings. Each fixable one carries a <see cref="Insight.FixKey"/> handled by <see cref="NetworkFixes"/>.</summary>
    public static List<Insight> Analyze(NetworkSnapshot s, SpeedResult? speed = null, PingStats? router = null, PingStats? internet = null)
    {
        var list = new List<Insight>();
        const string cat = "Network";

        if (s.Primary is null)
        {
            list.Add(new Insight(Severity.Critical, cat, "Not connected to a network",
                "No network adapter has a route to the internet.", "Connect to Wi-Fi or plug in a cable, then run the checks again.")
            { Source = "NetworkInterface / default gateway" });
            return list;
        }

        var w = s.Wifi;
        if (w is not null && s.Primary.Kind == "Wi-Fi")
        {
            if (w.SignalPercent is { } sig && sig < 60)
                list.Add(new Insight(sig < 40 ? Severity.Warning : Severity.Info, cat, $"Weak Wi-Fi signal ({sig}%)",
                    "A weak signal lowers speed and causes drop-outs.",
                    "Move closer to the router, keep it out of cabinets and off the floor, or add a mesh point.")
                { Measured = $"Signal {sig}%{(w.Rssi is { } r ? $", RSSI {r} dBm" : "")}", Rule = "Info below 60 %, warning below 40 %", Source = "netsh wlan show interfaces" });

            if (w.Band == "2.4 GHz")
                list.Add(new Insight(Severity.Info, cat, "Connected on 2.4 GHz",
                    "2.4 GHz is slower and more crowded than 5 GHz. Your router probably offers both.",
                    "Connect to the router's 5 GHz network. If both bands share one name, give 5 GHz its own name in the router settings.")
                { Measured = $"Band {w.Band}, channel {w.Channel}", Rule = "Suggest 5 GHz when on 2.4 GHz", Source = "netsh wlan show interfaces", FixKey = NetworkFixes.OpenWifiSettings });

            if (w.ReceiveMbps is { } link && link < 100)
                list.Add(new Insight(Severity.Info, cat, $"Slow Wi-Fi link ({link:0} Mbps)",
                    "The connection between this PC and the router is negotiated at a low rate, which caps your speed.",
                    "Usually caused by distance, walls or 2.4 GHz. Moving closer or switching to 5 GHz raises it.")
                { Measured = $"Receive link rate {link:0} Mbps", Rule = "Info below 100 Mbps", Source = "netsh wlan show interfaces" });
        }

        // The case Rakin hit: excellent link, almost nothing arriving.
        double? linkMbps = w?.ReceiveMbps ?? (s.Primary.LinkBps > 0 ? s.Primary.LinkBps / 1e6 : null);
        bool slowVsLink = speed is not null && linkMbps is > 0 && speed.Mbps < 50 && speed.Mbps < linkMbps.Value * 0.15;
        if (slowVsLink)
        {
            string measured = $"Download {speed!.Mbps:0.0} Mbps vs link {linkMbps:0} Mbps";
            if (s.Tcp.RscEnabled == true)
                list.Add(new Insight(Severity.Warning, cat, "Fast link, slow downloads: try turning off RSC",
                    "Receive Segment Coalescing (RSC) bundles incoming data to save CPU. With some Wi-Fi drivers, Realtek in particular, it makes downloads crawl even though the signal is perfect.",
                    "Use Fix to turn RSC off (needs full access). It's safe and can be turned back on at any time.")
                { Measured = measured + ", RSC enabled", Rule = "Download under 15 % of link rate and under 50 Mbps", Source = "Speed test + MSFT_NetOffloadGlobalSetting", FixKey = NetworkFixes.DisableRsc });
            else
                list.Add(new Insight(Severity.Warning, cat, "Fast link, slow downloads",
                    "The connection to the router is fine, but data arrives far slower than it should. Common causes: a VPN, the router limiting this device, or a problem past the router.",
                    "Restart the router, check its app for limits or paused devices on this PC, and compare with another device on the same network.")
                { Measured = measured, Rule = "Download under 15 % of link rate and under 50 Mbps", Source = "Speed test" });
        }

        if (s.Tcp.AutoTuning is "Disabled" or "Restricted" or "HighlyRestricted")
            list.Add(new Insight(slowVsLink || speed is { Mbps: < 100 } ? Severity.Warning : Severity.Info, cat, $"TCP auto-tuning is {s.Tcp.AutoTuning.ToLowerInvariant()}",
                "Windows normally grows its download window to match fast connections. Restricted, it can cap downloads well below your plan.",
                "Use Fix to set it back to normal (needs full access). If downloads get worse afterwards, set it back.")
            { Measured = $"AutoTuningLevelLocal = {s.Tcp.AutoTuning}", Rule = "Anything other than Normal can limit fast connections", Source = "MSFT_NetTCPSetting (Internet)", FixKey = NetworkFixes.AutoTuningNormal });

        if (s.ActiveVpns.Count > 0)
            list.Add(new Insight(Severity.Info, cat, "VPN is connected",
                "All traffic goes through the VPN server, so your speed and ping depend on it.",
                "Disconnect the VPN if you don't need it right now, or pick a closer server.")
            { Measured = string.Join(", ", s.ActiveVpns), Rule = "Report any connected VPN adapter", Source = "NetworkInterface" });

        if (s.Proxy is not null)
            list.Add(new Insight(Severity.Info, cat, "A proxy server is set",
                "Web traffic is sent through a proxy. If you didn't set this up, it can slow browsing or come from unwanted software.",
                "Check Settings → Network & internet → Proxy.")
            { Measured = s.Proxy, Rule = "Report a configured proxy", Source = @"HKCU\...\Internet Settings", FixKey = NetworkFixes.OpenProxySettings });

        if (router is { Received: > 0 })
        {
            if (router.LossPercent >= 5 || router.AverageMs > 30)
                list.Add(new Insight(Severity.Warning, cat, "Unstable connection to your router",
                    "Pings to your own router should be quick and never get lost. These are signs of Wi-Fi interference or a weak signal.",
                    "Move closer, switch to 5 GHz, or change the router's Wi-Fi channel.")
                { Measured = $"{router.LossPercent:0}% lost, {router.AverageMs:0} ms average, {router.JitterMs:0} ms jitter", Rule = "Warn at 5 % loss or 30 ms average", Source = $"Ping {router.Target} ×{router.Sent}" });
        }
        if (internet is not null && (internet.Received == 0 || internet.LossPercent >= 3 || internet.AverageMs > 120))
            list.Add(new Insight(Severity.Warning, cat, internet.Received == 0 ? "Internet doesn't answer pings" : "High ping or packet loss to the internet",
                internet.Received == 0 ? "Pings to the internet got no reply. The connection may be down, or a firewall blocks ping." : "Games and video calls will lag or stutter.",
                "If the router ping is fine, the problem is past your router: restart the modem or contact your internet provider.")
            { Measured = $"{internet.LossPercent:0}% lost, {internet.AverageMs:0} ms average", Rule = "Warn at 3 % loss or 120 ms", Source = $"Ping {internet.Target} ×{internet.Sent}" });

        if (!list.Any(i => i.Severity <= Severity.Warning))
            list.Insert(0, new Insight(Severity.Good, cat, speed is null ? "No network problems found" : $"Network is healthy ({speed.Mbps:0} Mbps)",
                speed is null ? "No known speed-limiting settings. Run the speed test for a full check." : "Connection, settings and speed all look right.")
            { Source = "CoreScope network checks" });
        return list;
    }
}

/// <summary>One-click network fixes. The netsh ones need administrator rights ("Unlock full sensors").</summary>
public static class NetworkFixes
{
    public const string DisableRsc = "net.rsc.off";
    public const string EnableRsc = "net.rsc.on";
    public const string AutoTuningNormal = "net.autotuning.normal";
    public const string FlushDns = "net.dns.flush";
    public const string OpenWifiSettings = "net.settings.wifi";
    public const string OpenProxySettings = "net.settings.proxy";

    public static bool NeedsAdmin(string key) => key is DisableRsc or EnableRsc or AutoTuningNormal;

    public static string ButtonText(string key) => key switch
    {
        DisableRsc => "Turn off RSC",
        EnableRsc => "Turn on RSC",
        AutoTuningNormal => "Set to normal",
        FlushDns => "Clear DNS cache",
        OpenWifiSettings => "Open Wi-Fi settings",
        OpenProxySettings => "Open proxy settings",
        _ => "Fix",
    };

    /// <summary>Runs a fix; returns (ok, message for the user).</summary>
    public static (bool Ok, string Message) Apply(string key)
    {
        switch (key)
        {
            case OpenWifiSettings: Shell.Open("ms-settings:network-wifi"); return (true, "Opened Wi-Fi settings.");
            case OpenProxySettings: Shell.Open("ms-settings:network-proxy"); return (true, "Opened proxy settings.");
        }
        if (NeedsAdmin(key) && !Native.IsAdministrator())
            return (false, "This fix needs full access. Click \"Unlock full sensors\" at the top, then try again.");

        var (file, args, done) = key switch
        {
            DisableRsc => ("netsh", "int tcp set global rsc=disabled", "RSC is off. Run the speed test again to compare."),
            EnableRsc => ("netsh", "int tcp set global rsc=enabled", "RSC is back on."),
            AutoTuningNormal => ("netsh", "int tcp set global autotuninglevel=normal", "Auto-tuning is set to normal."),
            FlushDns => ("ipconfig", "/flushdns", "DNS cache cleared."),
            _ => ("", "", ""),
        };
        if (file.Length == 0) return (false, "Unknown fix.");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p is null) return (false, "Windows couldn't run the command.");
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            Log.Info($"Network fix {key}: exit {p.ExitCode} {output.Trim()}");
            return p.ExitCode == 0 ? (true, done) : (false, $"Windows reported an error: {output.Trim()}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Network fix {key}", ex);
            return (false, "Windows couldn't run the command.");
        }
    }
}
