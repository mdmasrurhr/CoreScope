using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace CoreScope.Core.Hardware;

public sealed class DeviceEntry
{
    public string Name { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string ClassName { get; init; } = "";
    public int ErrorCode { get; init; }
    public string DriverVersion { get; set; } = "";
    public DateTime? DriverDate { get; set; }
    public string DriverProvider { get; set; } = "";
    public List<SpecRow> Details { get; } = new();

    public bool IsDisabled => ErrorCode == 22;
    public bool HasProblem => ErrorCode != 0 && ErrorCode != 22 && ErrorCode != 45;
    public string Problem => DeviceCatalog.ProblemText(ErrorCode);
    public string StatusName => HasProblem ? "Problem" : IsDisabled ? "Disabled" : "OK";

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (Manufacturer.Length > 0 && !Manufacturer.StartsWith("(", StringComparison.Ordinal)) parts.Add(Manufacturer);
            if (DriverVersion.Length > 0) parts.Add($"driver {DriverVersion}{(DriverDate is { } d ? $" ({d:MMM yyyy})" : "")}");
            if (ErrorCode != 0) parts.Add(Problem);
            return string.Join(" · ", parts);
        }
    }
}

public sealed class DeviceCategory
{
    public DeviceCategory(string title, string glyph, string? note = null)
    {
        Title = title;
        Glyph = glyph;
        Note = note;
    }

    public string Title { get; }
    public string Glyph { get; }
    public string? Note { get; set; }
    public List<DeviceEntry> Devices { get; } = new();
    public string CountText => Devices.Count == 1 ? "1 device" : $"{Devices.Count} devices";
}

/// <summary>
/// Device-Manager-style inventory of secondary hardware: network, Bluetooth, audio, cameras, input,
/// USB, displays, printers and security devices, with driver details and problem codes.
/// </summary>
public static class DeviceCatalog
{
    private const string GlyphNetwork = "", GlyphBluetooth = "", GlyphAudio = "", GlyphCamera = "",
                         GlyphInput = "", GlyphUsb = "", GlyphDisplay = "", GlyphPrinter = "",
                         GlyphSecurity = "", GlyphWarning = "", GlyphStorage = "";

    private static readonly Regex Virtual = new(@"^(ROOT|SWD|SW|UMB|ACPI_HAL|HTREE|STORAGE\\VOLUME)\\", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NoiseNames = new(
        @"Microsoft Bluetooth (LE )?Enumerator|RFCOMM Protocol|Bluetooth Device \(Personal Area|Microsoft Print to PDF|Microsoft XPS|OneNote|^Fax$|WAN Miniport|Kernel Debug|Teredo|6to4|IP-HTTPS|Root Print Queue|Generic Non-PnP|Microsoft Streaming",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VirtualNic = new(@"Virtual Adapter|Wi-Fi Direct|Hyper-V|VirtualBox|VMware|TAP-Windows|WireGuard|Npcap|Loopback", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InputHid = new(@"touch ?pad|touch ?screen|precision|pen\b|stylus|digitizer|i2c hid", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UsbController = new(@"host controller|xhci|usb4|usb 4|thunderbolt|root hub", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<DeviceCategory> Collect()
    {
        var watch = Stopwatch.StartNew();
        var drivers = ReadDrivers();
        var entities = Wmi.Query("SELECT Name, PNPClass, PNPDeviceID, Manufacturer, ConfigManagerErrorCode FROM Win32_PnPEntity");

        var problems = new DeviceCategory("Needs attention", GlyphWarning, "Devices Windows reports as not working. Open Device Manager for details, or update the driver from the maker's site.");
        var network = new DeviceCategory("Network adapters", GlyphNetwork);
        var bluetooth = new DeviceCategory("Bluetooth", GlyphBluetooth);
        var audio = new DeviceCategory("Audio", GlyphAudio);
        var cameras = new DeviceCategory("Cameras", GlyphCamera);
        var input = new DeviceCategory("Keyboard, mouse & touch", GlyphInput);
        var displays = new DeviceCategory("Displays", GlyphDisplay);
        var usb = new DeviceCategory("USB & Thunderbolt", GlyphUsb, "Host controllers and devices connected over USB.");
        var external = new DeviceCategory("External storage", GlyphStorage);
        var printers = new DeviceCategory("Printers & scanners", GlyphPrinter);
        var security = new DeviceCategory("Security & biometrics", GlyphSecurity);
        int hiddenVirtual = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in entities)
        {
            var name = e.Str("Name");
            var id = e.Str("PNPDeviceID");
            if (name.Length == 0 || id.Length == 0 || !seen.Add(id)) continue;

            var cls = e.Str("PNPClass");
            var entry = new DeviceEntry
            {
                Name = name,
                Manufacturer = e.Str("Manufacturer"),
                DeviceId = id,
                ClassName = cls,
                ErrorCode = e.Int("ConfigManagerErrorCode"),
            };
            if (drivers.TryGetValue(id, out var drv))
            {
                entry.DriverVersion = drv.Version;
                entry.DriverDate = drv.Date;
                entry.DriverProvider = drv.Provider;
            }

            if (entry.HasProblem) problems.Devices.Add(entry);
            if (NoiseNames.IsMatch(name)) continue;

            bool isVirtual = Virtual.IsMatch(id);
            switch (cls)
            {
                case "Net":
                    if (isVirtual || VirtualNic.IsMatch(name)) { hiddenVirtual++; break; }
                    network.Devices.Add(entry);
                    break;
                case "Bluetooth":
                    if (!id.StartsWith(@"BTH\MS_", StringComparison.OrdinalIgnoreCase) && !isVirtual) bluetooth.Devices.Add(entry);
                    break;
                case "AudioEndpoint":
                case "MEDIA":
                    if (cls == "MEDIA" && isVirtual) break;
                    audio.Devices.Add(entry);
                    break;
                case "Camera":
                case "Image":
                    cameras.Devices.Add(entry);
                    break;
                case "Keyboard":
                case "Mouse":
                    if (!isVirtual) input.Devices.Add(entry);
                    break;
                case "HIDClass":
                    if (InputHid.IsMatch(name)) input.Devices.Add(entry);
                    break;
                case "Monitor":
                    displays.Devices.Add(entry);
                    break;
                case "PrintQueue":
                case "Printer":
                    printers.Devices.Add(entry);
                    break;
                case "Biometric":
                case "SmartCardReader":
                case "SecurityDevices":
                    security.Devices.Add(entry);
                    break;
                case "USB":
                    if (UsbController.IsMatch(name) && !name.Contains("Root Hub", StringComparison.OrdinalIgnoreCase)) usb.Devices.Add(entry);
                    break;
                case "DiskDrive":
                    if (id.StartsWith(@"USBSTOR\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"SCSI\DISK&VEN_USB", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("USB", StringComparison.OrdinalIgnoreCase))
                        external.Devices.Add(entry);
                    break;
                default:
                    // Any other function device plugged in over USB (e.g. phones, game controllers, dongles).
                    if (id.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase) && cls is not ("USB" or "HIDClass" or "System" or "Ports" or ""))
                        usb.Devices.Add(entry);
                    break;
            }
        }

        EnrichNetwork(network);
        if (hiddenVirtual > 0) network.Note = $"{hiddenVirtual} virtual adapter(s) (VPN, Hyper-V, miniports) hidden.";
        foreach (var bt in bluetooth.Devices.Where(d => d.DeviceId.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase) || d.DeviceId.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase)))
            bt.Details.Add(new SpecRow("Type", "Paired device"));

        var categories = new List<DeviceCategory> { problems, network, bluetooth, audio, displays, cameras, input, usb, external, printers, security };
        categories.RemoveAll(c => c.Devices.Count == 0);
        foreach (var c in categories) c.Devices.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        // Connected network adapters first.
        network.Devices.Sort((a, b) =>
        {
            bool ac = a.Details.Any(r => r.Label == "Status" && r.Value == "Connected"), bc = b.Details.Any(r => r.Label == "Status" && r.Value == "Connected");
            return ac == bc ? string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) : ac ? -1 : 1;
        });
        Log.Info($"Devices: {entities.Count} PnP entities → {categories.Sum(c => c.Devices.Count)} shown in {watch.ElapsedMilliseconds} ms");
        return categories;
    }

    private sealed record DriverInfo(string Version, DateTime? Date, string Provider);

    private static Dictionary<string, DriverInfo> ReadDrivers()
    {
        var map = new Dictionary<string, DriverInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in Wmi.Query("SELECT DeviceID, DriverVersion, DriverDate, DriverProviderName FROM Win32_PnPSignedDriver"))
        {
            var id = d.Str("DeviceID");
            if (id.Length > 0) map[id] = new DriverInfo(d.Str("DriverVersion"), d.Date("DriverDate"), d.Str("DriverProviderName"));
        }
        return map;
    }

    // ───────────── Network detail ─────────────

    private static void EnrichNetwork(DeviceCategory network)
    {
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException ex) { Log.Error("Enumerating network interfaces", ex); return; }

        var wifi = ReadWifiConnection();
        foreach (var device in network.Devices)
        {
            var nic = interfaces.FirstOrDefault(n => n.Description.Equals(device.Name, StringComparison.OrdinalIgnoreCase));
            if (nic is null) continue;

            bool up = nic.OperationalStatus == OperationalStatus.Up;
            device.Details.Add(new SpecRow("Status", up ? "Connected" : nic.OperationalStatus == OperationalStatus.Down ? "Not connected" : nic.OperationalStatus.ToString()));
            device.Details.Add(new SpecRow("Type", nic.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
                _ => nic.NetworkInterfaceType.ToString(),
            }));
            if (up && nic.Speed > 0) device.Details.Add(new SpecRow("Link speed", FormatSpeed(nic.Speed)));
            var mac = nic.GetPhysicalAddress().GetAddressBytes();
            if (mac.Length == 6) device.Details.Add(new SpecRow("MAC address", string.Join(":", mac.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)))));

            if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && wifi is not null && up)
            {
                if (wifi.Ssid.Length > 0) device.Details.Add(new SpecRow("Network (SSID)", wifi.Ssid));
                if (wifi.Signal.Length > 0) device.Details.Add(new SpecRow("Signal", wifi.Signal));
                if (wifi.Radio.Length > 0) device.Details.Add(new SpecRow("Connected using", wifi.Radio + (wifi.Band.Length > 0 ? $" · {wifi.Band}" : "")));
            }

            if (!up) continue;
            try
            {
                var props = nic.GetIPProperties();
                var v4 = props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).ToList();
                var v6 = props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal).Select(a => a.Address.ToString()).ToList();
                if (v4.Count > 0) device.Details.Add(new SpecRow("IPv4", string.Join(", ", v4)));
                if (v6.Count > 0) device.Details.Add(new SpecRow("IPv6", string.Join(", ", v6.Take(2))));
                var gateway = props.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
                if (!string.IsNullOrEmpty(gateway)) device.Details.Add(new SpecRow("Gateway", gateway));
                var dns = props.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).Take(2).ToList();
                if (dns.Count > 0) device.Details.Add(new SpecRow("DNS", string.Join(", ", dns)));
            }
            catch (NetworkInformationException ex) { Log.Error("Reading IP properties", ex); }
        }
    }

    private static string FormatSpeed(long bitsPerSecond) => bitsPerSecond >= 1_000_000_000
        ? $"{bitsPerSecond / 1e9:0.#} Gbps"
        : $"{bitsPerSecond / 1e6:0} Mbps";

    private sealed record WifiConnection(string Ssid, string Signal, string Radio, string Band);

    /// <summary>Parses `netsh wlan show interfaces`; values like "802.11ax" and "%" are language-independent.</summary>
    private static WifiConnection? ReadWifiConnection()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", "wlan show interfaces") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            string ssid = Regex.Match(output, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
            string signal = Regex.Match(output, @":\s*(\d{1,3}\s?%)").Groups[1].Value.Replace(" ", "");
            string radio = Regex.Match(output, @"802\.11[a-z]{1,2}").Value;
            string band = Regex.Match(output, @":\s*(2\.4|5|6)\s*GHz", RegexOptions.IgnoreCase).Success
                ? Regex.Match(output, @":\s*(2\.4|5|6)\s*GHz", RegexOptions.IgnoreCase).Groups[1].Value + " GHz"
                : "";
            return new WifiConnection(ssid, signal, radio, band);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("netsh wlan show interfaces", ex);
            return null;
        }
    }

    /// <summary>Device Manager problem codes (CM_PROB_*), in plain English.</summary>
    public static string ProblemText(int code) => code switch
    {
        0 => "Working",
        1 => "Not configured correctly (code 1)",
        3 => "Driver may be corrupted or memory is low (code 3)",
        10 => "Device cannot start (code 10)",
        12 => "Not enough free resources (code 12)",
        14 => "Restart required (code 14)",
        18 => "Drivers need to be reinstalled (code 18)",
        19 => "Registry configuration is damaged (code 19)",
        21 => "Windows is removing this device (code 21)",
        22 => "Disabled",
        24 => "Not present or not working properly (code 24)",
        28 => "Driver not installed (code 28)",
        29 => "Disabled by firmware (code 29)",
        31 => "Driver could not load (code 31)",
        32 => "Driver service disabled (code 32)",
        37 => "Driver failed to initialize (code 37)",
        39 => "Driver corrupted or missing (code 39)",
        43 => "Stopped after reporting a problem (code 43)",
        45 => "Not currently connected",
        48 => "Driver blocked as incompatible (code 48)",
        52 => "Driver signature can't be verified (code 52)",
        _ => $"Problem code {code}",
    };
}
