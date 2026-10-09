using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CoreScope.Core.Network;

public sealed record WifiNetwork(string Ssid, string Bssid, int Signal, int Channel, string Band, string Radio, string Security)
{
    public string DisplayName => Ssid.Length > 0 ? Ssid : "(hidden network)";
    public string Where => $"{Band} · ch {Channel}" + (Radio.Length > 0 ? $" · {Radio}" : "");
    /// <summary>0–1 bar for the signal meter.</summary>
    public double SignalFraction => Math.Clamp(Signal / 100.0, 0, 1);
    public bool IsYours { get; init; }
}

public sealed record ChannelLoad(int Channel, string Band, int Networks, double Load, bool IsYours, bool IsBest)
{
    /// <summary>Bar height 0–1 relative to the busiest channel in the band.</summary>
    public double Height { get; init; }
    public string Tooltip => $"Channel {Channel}: {Networks} network{(Networks == 1 ? "" : "s")} nearby" + (IsYours ? " · your router" : "") + (IsBest ? " · least crowded" : "");
}

public sealed class WifiScan
{
    public List<WifiNetwork> Networks { get; } = new();
    public List<ChannelLoad> Channels24 { get; } = new();
    public List<ChannelLoad> Channels5 { get; } = new();
    public string? Problem { get; set; }
    public bool NeedsLocation { get; set; }
    public string Summary { get; set; } = "";
    public string? Recommendation { get; set; }
    public int? YourChannel { get; set; }
    public int? BestChannel { get; set; }
    public bool Crowded { get; set; }
}

/// <summary>
/// Nearby Wi-Fi networks from `netsh wlan show networks mode=bssid`, how crowded each channel is, and whether
/// moving your router to another channel would help. Windows 11 24H2+ only shows nearby networks to desktop
/// apps when Location is turned on.
/// </summary>
public static class WifiAnalyzer
{
    private static readonly int[] Best24 = { 1, 6, 11 };
    private static readonly int[] Best5 = { 36, 40, 44, 48, 149, 153, 157, 161, 165 }; // non-DFS: every router supports them

    public static WifiScan Scan(string? yourSsid, int? yourChannel)
    {
        var scan = new WifiScan { YourChannel = yourChannel };
        string output;
        int exit;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("netsh", "wlan show networks mode=bssid")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            if (p is null) { scan.Problem = "Windows couldn't list nearby networks."; return scan; }
            output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            exit = p.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("netsh wlan networks", ex);
            scan.Problem = "Windows couldn't list nearby networks.";
            return scan;
        }

        Parse(output, yourSsid, scan.Networks);
        if (scan.Networks.Count == 0)
        {
            if (output.Contains("location", StringComparison.OrdinalIgnoreCase) || exit != 0)
            {
                scan.NeedsLocation = true;
                scan.Problem = "Windows only shows nearby Wi-Fi networks to apps when Location is on. Turn on Location services and \"Let desktop apps access your location\", then scan again.";
            }
            else scan.Problem = "No Wi-Fi networks found. Is Wi-Fi turned on?";
            return scan;
        }
        Analyze(scan, yourSsid, yourChannel);
        return scan;
    }

    public static void Parse(string output, string? yourSsid, List<WifiNetwork> into)
    {
        string ssid = "", security = "";
        string? bssid = null;
        int signal = 0, channel = 0;
        string band = "", radio = "";

        void Flush()
        {
            if (bssid is not null && channel > 0)
                into.Add(new WifiNetwork(ssid, bssid, signal, channel, band.Length > 0 ? band : channel <= 14 ? "2.4 GHz" : "5 GHz", radio, security)
                { IsYours = yourSsid is { Length: > 0 } && ssid == yourSsid });
            bssid = null; signal = 0; channel = 0; band = ""; radio = "";
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = Regex.Match(line, @"^SSID\s+\d+\s*:\s?(.*)$");
            if (m.Success) { Flush(); ssid = m.Groups[1].Value.Trim(); security = ""; continue; }
            m = Regex.Match(line, @"^\s+BSSID\s+\d+\s*:\s*([0-9a-fA-F:]{17})");
            if (m.Success) { Flush(); bssid = m.Groups[1].Value.ToLowerInvariant(); continue; }

            var kv = Regex.Match(line, @"^\s+([^:]+?)\s*:\s*(.+?)\s*$");
            if (!kv.Success) continue;
            string key = kv.Groups[1].Value, value = kv.Groups[2].Value;

            if (bssid is null)
            {
                if (key.StartsWith("Authentication", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(value, @"^(WPA|WEP|Open|Ouvert|Offen)", RegexOptions.IgnoreCase))
                    security = security.Length == 0 ? value : security;
                continue;
            }
            if (Regex.Match(value, @"^(\d{1,3})\s?%$") is { Success: true } sm) signal = int.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture);
            else if (Regex.IsMatch(value, @"^802\.11")) radio = value;
            else if (Regex.Match(value, @"^(2\.4|5|6)\s*GHz$", RegexOptions.IgnoreCase) is { Success: true } bm) band = bm.Groups[1].Value + " GHz";
            else if (channel == 0 && (key.StartsWith("Channel", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Canal", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Kanal", StringComparison.OrdinalIgnoreCase))
                     && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ch) && ch is > 0 and < 234)
                channel = ch;
        }
        Flush();
    }

    /// <summary>A neighbour's weight on a channel: strong nearby networks matter far more than faint ones.</summary>
    private static double Weight(int signal) => Math.Pow(Math.Clamp(signal, 0, 100) / 100.0, 2);

    /// <summary>5 GHz routers usually use 80 MHz: channels in the same 80 MHz block share airtime.</summary>
    private static int Block5(int ch) => ch >= 149 ? 1000 + (ch - 149) / 16 : (ch - 36) / 16;

    public static void Analyze(WifiScan scan, string? yourSsid, int? yourChannel)
    {
        var others = scan.Networks.Where(n => !n.IsYours).ToList();

        // ── 2.4 GHz: 20 MHz channels overlap with anything within 4 channels.
        var nets24 = others.Where(n => n.Channel <= 14).ToList();
        var load24 = Enumerable.Range(1, 13).Select(c => (c, load: nets24.Where(n => Math.Abs(n.Channel - c) < 5).Sum(n => Weight(n.Signal)),
                                                            count: nets24.Count(n => n.Channel == c))).ToList();
        int best24 = Best24.OrderBy(c => load24[c - 1].load).First();

        // ── 5 GHz: share airtime within an 80 MHz block.
        var nets5 = others.Where(n => n.Channel >= 32 && n.Channel <= 177).ToList();
        var channels5 = Best5.Concat(nets5.Select(n => n.Channel)).Concat(yourChannel is >= 32 ? new[] { yourChannel.Value } : Array.Empty<int>())
                             .Distinct().OrderBy(c => c).ToList();
        double Load5(int c) => nets5.Where(n => Block5(n.Channel) == Block5(c)).Sum(n => Weight(n.Signal));
        int best5 = Best5.OrderBy(Load5).ThenBy(c => c).First();

        double max24 = Math.Max(0.01, load24.Max(l => l.load));
        foreach (var (c, load, count) in load24)
            scan.Channels24.Add(new ChannelLoad(c, "2.4 GHz", count, load, yourChannel == c, c == best24) { Height = load / max24 });
        double max5 = Math.Max(0.01, channels5.Max(Load5));
        foreach (var c in channels5)
            scan.Channels5.Add(new ChannelLoad(c, "5 GHz", nets5.Count(n => n.Channel == c), Load5(c), yourChannel == c, c == best5) { Height = Load5(c) / max5 });

        int total = scan.Networks.Count;
        int on5 = scan.Networks.Count(n => n.Channel >= 32);
        scan.Summary = $"{total} network{(total == 1 ? "" : "s")} nearby · {total - on5} on 2.4 GHz · {on5} on 5 GHz";

        if (yourChannel is not { } mine) return;
        bool is24 = mine <= 14;
        double myLoad = is24 ? load24[Math.Clamp(mine, 1, 13) - 1].load : Load5(mine);
        int best = is24 ? best24 : best5;
        double bestLoad = is24 ? load24[best - 1].load : Load5(best);
        int sharing = is24 ? nets24.Count(n => Math.Abs(n.Channel - mine) < 5) : nets5.Count(n => Block5(n.Channel) == Block5(mine));
        scan.BestChannel = best;
        scan.Crowded = myLoad > 0.6 && myLoad > bestLoad * 2 + 0.2 && best != mine;
        scan.Recommendation = scan.Crowded
            ? $"Your router is on channel {mine}, shared with {sharing} nearby network{(sharing == 1 ? "" : "s")}. Channel {best} is much quieter: change it in your router's settings (or its app) for faster, steadier Wi-Fi."
            : $"Your router's channel {mine} is fine: {(sharing == 0 ? "no other networks share it" : $"{sharing} other network{(sharing == 1 ? "" : "s")} share it, but they're weak or the alternatives are just as busy")}.";
    }
}
