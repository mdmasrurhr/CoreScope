using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CoreScope.Core.Hardware;

/// <summary>PCI Express link state of a device and of the slot/port it is plugged into.</summary>
public sealed record PcieLink(int CurrentGen, int CurrentWidth, int DeviceMaxGen, int DeviceMaxWidth, int SlotMaxGen, int SlotMaxWidth)
{
    public static string GenText(int gen) => gen > 0 ? $"PCIe {gen}.0" : "?";
    public string Current => CurrentGen > 0 ? $"{GenText(CurrentGen)} x{CurrentWidth}" : "";
    public string Device => DeviceMaxGen > 0 ? $"{GenText(DeviceMaxGen)} x{DeviceMaxWidth}" : "";
    public string Slot => SlotMaxGen > 0 ? $"{GenText(SlotMaxGen)} x{SlotMaxWidth}" : "";
}

public sealed record DiskLink(string Model, string PnpId, PcieLink? Link);

public sealed record WifiInfo(string Name, string Vendor, string Bus, IReadOnlyList<string> RadioTypes, string Generation, bool IsCnvi);

public sealed record PanelInfo(
    string Vendor,
    string ProductCode,
    string PartNumber,
    string Name,
    int Width,
    int Height,
    double RefreshHz,
    int BitsPerColor,
    string Interface,
    double? DiagonalInches,
    bool IsInternal,
    int Year);

/// <summary>Extra hardware probes used by the upgrade advisor (PCIe links, BitLocker, Wi-Fi, display EDID).</summary>
public static class DeviceProbe
{
    // ───────────────────────── PCIe link via Configuration Manager ─────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FmtId;
        public uint Pid;
    }

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("CfgMgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int bufferLength, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint propertyType, byte[]? buffer, ref uint bufferSize, uint flags);

    private const int CrSuccess = 0;
    private static readonly Guid PciDeviceFmtId = new("3AB22E31-8264-4B4E-9AF5-A8D2D8E33E62");
    private const uint PidCurrentLinkSpeed = 9, PidCurrentLinkWidth = 10, PidMaxLinkSpeed = 11, PidMaxLinkWidth = 12;

    private static uint? ReadUInt32(uint devInst, uint pid)
    {
        var key = new DevPropKey { FmtId = PciDeviceFmtId, Pid = pid };
        var buffer = new byte[4];
        uint size = 4;
        return CM_Get_DevNode_PropertyW(devInst, ref key, out _, buffer, ref size, 0) == CrSuccess && size >= 4
            ? BitConverter.ToUInt32(buffer, 0)
            : null;
    }

    private static string DeviceId(uint devInst)
    {
        var sb = new StringBuilder(512);
        return CM_Get_Device_IDW(devInst, sb, sb.Capacity, 0) == CrSuccess ? sb.ToString() : "";
    }

    /// <summary>
    /// Walks up from a device (e.g. a disk or GPU) to the first PCI function, then reads its link
    /// state and the maximum link of its parent port — the port's maximum is what the slot supports.
    /// </summary>
    public static PcieLink? GetPcieLink(string pnpDeviceId)
    {
        try
        {
            if (CM_Locate_DevNodeW(out uint node, pnpDeviceId, 0) != CrSuccess) return null;
            for (int hop = 0; hop < 4; hop++)
            {
                if (DeviceId(node).StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)
                    && ReadUInt32(node, PidMaxLinkSpeed) is { } maxSpeed)
                {
                    int slotGen = 0, slotWidth = 0;
                    if (CM_Get_Parent(out uint port, node, 0) == CrSuccess)
                    {
                        slotGen = (int)(ReadUInt32(port, PidMaxLinkSpeed) ?? 0);
                        slotWidth = (int)(ReadUInt32(port, PidMaxLinkWidth) ?? 0);
                    }
                    return new PcieLink(
                        (int)(ReadUInt32(node, PidCurrentLinkSpeed) ?? 0),
                        (int)(ReadUInt32(node, PidCurrentLinkWidth) ?? 0),
                        (int)maxSpeed,
                        (int)(ReadUInt32(node, PidMaxLinkWidth) ?? 0),
                        slotGen,
                        slotWidth);
                }
                if (CM_Get_Parent(out uint parent, node, 0) != CrSuccess) break;
                node = parent;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Error("Reading PCIe link", ex);
        }
        return null;
    }

    public static List<DiskLink> GetDiskLinks()
    {
        var result = new List<DiskLink>();
        foreach (var d in Wmi.Query("SELECT Model, PNPDeviceID FROM Win32_DiskDrive"))
        {
            var pnp = d.Str("PNPDeviceID");
            result.Add(new DiskLink(d.Str("Model"), pnp, pnp.Length > 0 ? GetPcieLink(pnp) : null));
        }
        return result;
    }

    // ───────────────────────── BitLocker ─────────────────────────

    /// <summary>Drive letter → protection on/off, or null when the BitLocker provider isn't readable.</summary>
    public static Dictionary<string, bool>? GetBitLockerStatus()
    {
        var volumes = Wmi.Query("SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume",
            @"root\CIMV2\Security\MicrosoftVolumeEncryption");
        if (volumes.Count == 0) return null;
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in volumes)
        {
            var letter = v.Str("DriveLetter");
            if (letter.Length > 0) result[letter] = v.Int("ProtectionStatus") == 1;
        }
        return result;
    }

    // ───────────────────────── Wi-Fi ─────────────────────────

    private static readonly Regex WifiName = new(@"wi-?fi|wireless|wlan|802\.11", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SixE = new(@"AX21[01]|AX411|6E|MT7922|RZ616|RTL8852C|WCN685|QCNCM865|NCM865|BE20", RegexOptions.IgnoreCase);
    private static readonly Regex IntelCnvi = new(@"AX101|AX201|AX203|AX211|BE201|BE202|9560|9462|9461", RegexOptions.IgnoreCase);

    public static WifiInfo? GetWifi()
    {
        var adapter = Wmi.Query("SELECT Name, PNPDeviceID, Manufacturer FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE")
            .FirstOrDefault(a => WifiName.IsMatch(a.Str("Name")));
        if (adapter is null) return null;

        var name = adapter.Str("Name");
        var pnp = adapter.Str("PNPDeviceID").ToUpperInvariant();
        var radios = ReadRadioTypes();

        string generation =
            radios.Contains("802.11be") || Regex.IsMatch(name, @"BE2\d\d|Wi-?Fi 7", RegexOptions.IgnoreCase) ? "Wi-Fi 7" :
            radios.Contains("802.11ax") || Regex.IsMatch(name, @"\bAX\d|Wi-?Fi 6|802\.11ax|MT792|RZ6|RTL885", RegexOptions.IgnoreCase)
                ? (SixE.IsMatch(name) ? "Wi-Fi 6E" : "Wi-Fi 6") :
            radios.Contains("802.11ac") || Regex.IsMatch(name, @"\bAC\b|Wireless-AC|802\.11ac", RegexOptions.IgnoreCase) ? "Wi-Fi 5" :
            radios.Contains("802.11n") ? "Wi-Fi 4" : "Unknown";

        string bus = pnp.StartsWith(@"PCI\", StringComparison.Ordinal) ? "PCIe"
                   : pnp.StartsWith(@"USB\", StringComparison.Ordinal) ? "USB"
                   : pnp.StartsWith(@"SD\", StringComparison.Ordinal) ? "SDIO" : "";
        string vendor = pnp.Contains("VEN_8086") ? "Intel"
                      : pnp.Contains("VEN_10EC") ? "Realtek"
                      : pnp.Contains("VEN_14C3") ? "MediaTek"
                      : pnp.Contains("VEN_17CB") || pnp.Contains("VEN_168C") ? "Qualcomm"
                      : pnp.Contains("VEN_14E4") ? "Broadcom"
                      : adapter.Str("Manufacturer");

        return new WifiInfo(name, vendor, bus, radios, generation, vendor == "Intel" && IntelCnvi.IsMatch(name));
    }

    /// <summary>"802.11xx" tokens from netsh are locale-independent, unlike the labels around them.</summary>
    private static List<string> ReadRadioTypes()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", "wlan show drivers")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return new List<string>();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return Regex.Matches(output, @"802\.11[a-z]{1,2}\b").Select(m => m.Value).Distinct().ToList();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("netsh wlan show drivers", ex);
            return new List<string>();
        }
    }

    // ───────────────────────── Display panels (EDID) ─────────────────────────

    /// <summary>Reads the EDID of every active display; internal (eDP/LVDS) panels are flagged.</summary>
    public static List<PanelInfo> GetPanels()
    {
        var result = new List<PanelInfo>();
        foreach (var conn in Wmi.Query("SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams", Wmi.RootWmi))
        {
            var instance = conn.Str("InstanceName"); // e.g. DISPLAY\BOE0A2A\4&1234&0&UID8388688_0
            var technology = (uint)conn.ULong("VideoOutputTechnology");
            // D3DKMDT_VIDEO_OUTPUT_TECHNOLOGY: 6 = LVDS, 11 = DisplayPort embedded (eDP), 13 = UDI embedded, 0x80000000 = internal.
            bool isInternal = technology is 0x80000000 or 6 or 11 or 13;
            var registryPath = Regex.Replace(instance, @"_\d+$", "");
            var edid = ReadEdid(registryPath);
            if (edid is null) continue;
            var panel = ParseEdid(edid, isInternal);
            if (panel is not null) result.Add(panel);
        }
        return result;
    }

    private static byte[]? ReadEdid(string instancePath)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instancePath}\Device Parameters");
            return key?.GetValue("EDID") as byte[];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static PanelInfo? ParseEdid(byte[] e, bool isInternal)
    {
        if (e.Length < 128 || e[0] != 0x00 || e[1] != 0xFF) return null;

        int mfg = (e[8] << 8) | e[9];
        string vendor = new(new[]
        {
            (char)('A' + ((mfg >> 10) & 0x1F) - 1),
            (char)('A' + ((mfg >> 5) & 0x1F) - 1),
            (char)('A' + (mfg & 0x1F) - 1),
        });
        string product = BitConverter.ToUInt16(e, 10).ToString("X4", CultureInfo.InvariantCulture);
        int year = e[17] > 0 ? 1990 + e[17] : 0;
        double? diagonal = e[21] > 0 && e[22] > 0 ? Math.Sqrt(e[21] * e[21] + e[22] * e[22]) / 2.54 : null;

        int bits = 0;
        string iface = "";
        if ((e[20] & 0x80) != 0)
        {
            bits = ((e[20] >> 4) & 0x7) switch { 1 => 6, 2 => 8, 3 => 10, 4 => 12, 5 => 14, 6 => 16, _ => 0 };
            iface = (e[20] & 0x0F) switch { 1 => "DVI", 2 or 3 => "HDMI", 5 => isInternal ? "eDP (embedded DisplayPort)" : "DisplayPort", _ => isInternal ? "eDP / LVDS" : "" };
        }

        int width = 0, height = 0;
        double refresh = 0;
        string name = "", partNumber = "";
        var texts = new List<string>();
        for (int d = 54; d <= 108; d += 18)
        {
            if (e[d] != 0 || e[d + 1] != 0)
            {
                if (width != 0) continue; // only the first (preferred) detailed timing
                int pixelClock = (e[d] | (e[d + 1] << 8)) * 10_000;
                width = e[d + 2] | ((e[d + 4] & 0xF0) << 4);
                int hBlank = e[d + 3] | ((e[d + 4] & 0x0F) << 8);
                height = e[d + 5] | ((e[d + 7] & 0xF0) << 4);
                int vBlank = e[d + 6] | ((e[d + 7] & 0x0F) << 8);
                long total = (long)(width + hBlank) * (height + vBlank);
                if (total > 0) refresh = pixelClock / (double)total;
                continue;
            }
            string text = Encoding.ASCII.GetString(e, d + 5, 13).Split('\n')[0].Trim();
            switch (e[d + 3])
            {
                case 0xFC: name = text; break;
                case 0xFE: if (text.Length > 0) texts.Add(text); break;
            }
        }
        // Laptop panels usually store maker + part number as two 0xFE strings (e.g. "BOE CQ", "NE156FHM-NX1").
        partNumber = texts.LastOrDefault(t => Regex.IsMatch(t, @"\d") && t.Length >= 6) ?? texts.LastOrDefault() ?? "";

        return new PanelInfo(vendor, product, partNumber, name, width, height, refresh, bits, iface, diagonal, isInternal, year);
    }

    public static string PanelVendorName(string pnpId) => pnpId switch
    {
        "BOE" => "BOE", "AUO" => "AU Optronics", "LGD" => "LG Display", "SDC" => "Samsung Display",
        "SHP" => "Sharp", "CMN" => "Innolux (Chimei)", "IVO" => "InfoVision", "CSO" => "CSOT", "NCP" => "Najing CEC Panda",
        "LEN" => "Lenovo", "SAM" => "Samsung", "DEL" => "Dell", "GSM" => "LG", "ACR" => "Acer", "AUS" => "ASUS",
        "HPN" => "HP", "MSI" => "MSI", "AOC" => "AOC", "BNQ" => "BenQ", "PHL" => "Philips", "TMA" => "Tianma",
        "APP" => "Apple", "SNY" => "Sony", "VSC" => "ViewSonic", "GBT" => "Gigabyte",
        _ => pnpId,
    };
}
