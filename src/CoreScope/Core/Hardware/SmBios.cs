using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace CoreScope.Core.Hardware;

/// <summary>One raw SMBIOS structure: the formatted area plus its trailing string set.</summary>
public sealed class SmBiosStructure
{
    public SmBiosStructure(byte type, ushort handle, byte[] data, List<string> strings)
    {
        Type = type;
        Handle = handle;
        Data = data;
        Strings = strings;
    }

    public byte Type { get; }
    public ushort Handle { get; }
    public byte[] Data { get; }
    public List<string> Strings { get; }
    public int Length => Data.Length;

    public byte Byte(int offset) => offset < Data.Length ? Data[offset] : (byte)0;
    public ushort Word(int offset) => offset + 1 < Data.Length ? BitConverter.ToUInt16(Data, offset) : (ushort)0;
    public uint DWord(int offset) => offset + 3 < Data.Length ? BitConverter.ToUInt32(Data, offset) : 0;
    public ulong QWord(int offset) => offset + 7 < Data.Length ? BitConverter.ToUInt64(Data, offset) : 0;

    /// <summary>SMBIOS strings are referenced by 1-based index; 0 means "no string".</summary>
    public string String(int offset)
    {
        int index = Byte(offset);
        return index >= 1 && index <= Strings.Count ? Strings[index - 1].Trim() : "";
    }
}

public sealed record SmBiosMemoryDevice(
    string Locator,
    string Bank,
    bool Installed,
    ulong SizeBytes,
    string FormFactor,
    string Type,
    int SpeedMts,
    int ConfiguredSpeedMts,
    string Manufacturer,
    string PartNumber,
    int Rank,
    int ConfiguredVoltageMv,
    int TotalWidth = 0,
    int DataWidth = 0)
{
    /// <summary>ECC modules carry extra check bits: total width (e.g. 72) exceeds data width (64).</summary>
    public bool IsEcc => TotalWidth > DataWidth && DataWidth > 0 && TotalWidth != 0xFFFF;

    /// <summary>Soldered when firmware describes chips rather than a module, or for LPDDR (never socketed).</summary>
    public bool IsSoldered =>
        FormFactor is "Row of chips" or "Chip" or "Die"
        || Type.StartsWith("LPDDR", StringComparison.Ordinal);
}

public sealed record SmBiosSlot(string Designation, string Kind, string Usage);

public sealed record SmBiosBattery(string Location, string Manufacturer, string DeviceName, string Chemistry, uint DesignCapacityMwh, uint DesignVoltageMv, string ManufactureDate);

/// <summary>
/// Reads the raw SMBIOS/DMI table straight from firmware. Unlike WMI it lists *empty* memory slots,
/// distinguishes soldered memory, and exposes the processor socket and portable-battery records.
/// </summary>
public sealed class SmBios
{
    private const uint Rsmb = 0x52534D42; // 'RSMB'

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, uint bufferSize);

    private SmBios(List<SmBiosStructure> structures, string version)
    {
        Structures = structures;
        Version = version;
    }

    public List<SmBiosStructure> Structures { get; }
    public string Version { get; }

    public static SmBios? Read()
    {
        try
        {
            uint size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
            if (size == 0) return null;
            var raw = new byte[size];
            if (GetSystemFirmwareTable(Rsmb, 0, raw, size) == 0) return null;
            return Parse(raw);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            Log.Error("Reading SMBIOS", ex);
            return null;
        }
    }

    /// <summary>Parses the RawSMBIOSData blob (8-byte header followed by the structure table).</summary>
    public static SmBios? Parse(byte[] raw)
    {
        if (raw.Length < 8) return null;
        string version = $"{raw[1]}.{raw[2]}";
        int tableLength = (int)Math.Min(BitConverter.ToUInt32(raw, 4), (uint)(raw.Length - 8));
        int pos = 8, end = 8 + tableLength;
        var list = new List<SmBiosStructure>();

        while (pos + 4 <= end)
        {
            byte type = raw[pos];
            byte length = raw[pos + 1];
            if (length < 4 || pos + length > end) break;
            ushort handle = BitConverter.ToUInt16(raw, pos + 2);
            var data = new byte[length];
            Array.Copy(raw, pos, data, 0, length);

            // String set: consecutive NUL-terminated strings, ended by an extra NUL.
            var strings = new List<string>();
            int p = pos + length;
            if (p + 1 < end && raw[p] == 0 && raw[p + 1] == 0)
            {
                p += 2;
            }
            else
            {
                while (p < end)
                {
                    int start = p;
                    while (p < end && raw[p] != 0) p++;
                    strings.Add(Encoding.ASCII.GetString(raw, start, p - start));
                    p++;
                    if (p < end && raw[p] == 0) { p++; break; }
                }
            }

            list.Add(new SmBiosStructure(type, handle, data, strings));
            pos = p;
            if (type == 127) break; // end-of-table
        }
        return new SmBios(list, version);
    }

    private IEnumerable<SmBiosStructure> OfType(byte type)
    {
        foreach (var s in Structures) if (s.Type == type) yield return s;
    }

    // ───────────── Type 3: chassis ─────────────

    public int ChassisType
    {
        get
        {
            foreach (var s in OfType(3)) return s.Byte(0x05) & 0x7F;
            return 0;
        }
    }

    public static bool IsPortableChassis(int chassis) => chassis is 8 or 9 or 10 or 11 or 14 or 30 or 31 or 32;

    public static string ChassisName(int chassis) => chassis switch
    {
        3 => "Desktop", 4 => "Low-profile desktop", 6 => "Mini tower", 7 => "Tower", 8 => "Portable",
        9 => "Laptop", 10 => "Notebook", 11 => "Handheld", 13 => "All-in-one", 14 => "Sub-notebook",
        15 => "Space-saving", 17 => "Server chassis", 23 => "Rack mount", 24 => "Sealed-case PC",
        30 => "Tablet", 31 => "Convertible", 32 => "Detachable", 34 => "Embedded PC", 35 => "Mini PC", 36 => "Stick PC",
        _ => "",
    };

    // ───────────── Type 4: processor socket ─────────────

    public (string Socket, string Upgrade) ProcessorSocket
    {
        get
        {
            foreach (var s in OfType(4))
            {
                if ((s.Byte(0x18) & 0x40) == 0) continue; // status bit 6: socket populated
                return (s.String(0x04), ProcessorUpgradeName(s.Byte(0x19)));
            }
            return ("", "");
        }
    }

    private static string ProcessorUpgradeName(byte code) => code switch
    {
        0x01 => "Other", 0x02 => "Unknown", 0x06 => "None",
        0x1F => "PGA988A", 0x20 => "BGA1288", 0x21 => "rPGA988B", 0x22 => "BGA1023", 0x23 => "BGA1224",
        0x24 => "LGA1155", 0x26 => "LGA2011", 0x27 => "FS1", 0x28 => "FS2", 0x29 => "FM1", 0x2A => "FM2",
        0x2B => "LGA2011-3", 0x2D => "LGA1150", 0x2E => "BGA1168", 0x2F => "BGA1234", 0x30 => "BGA1364",
        0x31 => "AM4", 0x32 => "LGA1151", 0x33 => "BGA1356", 0x34 => "BGA1440", 0x35 => "BGA1515",
        0x36 => "LGA3647-1", 0x37 => "SP3", 0x38 => "SP3r2", 0x39 => "LGA2066", 0x3A => "BGA1392",
        0x3B => "BGA1510", 0x3C => "BGA1528", 0x3D => "LGA4189", 0x3E => "LGA1200", 0x3F => "LGA4677",
        0x40 => "LGA1700", 0x41 => "BGA1744", 0x42 => "BGA1781", 0x43 => "BGA1211", 0x44 => "BGA2422",
        0x45 => "LGA1211", 0x46 => "LGA2422", 0x47 => "LGA5773", 0x48 => "BGA5773", 0x49 => "AM5",
        0x4A => "SP5", 0x4B => "SP6",
        _ => "",
    };

    // ───────────── Type 9: system slots ─────────────

    public List<SmBiosSlot> Slots
    {
        get
        {
            var result = new List<SmBiosSlot>();
            foreach (var s in OfType(9))
            {
                var designation = s.String(0x04);
                byte type = s.Byte(0x05);
                string kind = type switch
                {
                    0x13 => "M.2 Key A",
                    0x14 => "M.2 Key E (Wi-Fi)",
                    0x15 => "M.2 Key B",
                    0x16 => "M.2 Key M (SSD)",
                    >= 0xA5 and <= 0xAA => "PCI Express",
                    >= 0xAB and <= 0xB0 => "PCI Express Gen 2",
                    >= 0xB1 and <= 0xB6 => "PCI Express Gen 3",
                    >= 0xB8 and <= 0xBD => "PCI Express Gen 4",
                    >= 0xBE and <= 0xC3 => "PCI Express Gen 5",
                    _ => designation.Contains("M.2", StringComparison.OrdinalIgnoreCase) || designation.Contains("M2", StringComparison.OrdinalIgnoreCase) ? "M.2" : "Other",
                };
                string usage = s.Byte(0x07) switch { 3 => "Empty", 4 => "In use", 5 => "Unavailable", _ => "Unknown" };
                result.Add(new SmBiosSlot(designation, kind, usage));
            }
            return result;
        }
    }

    // ───────────── Types 16 & 17: memory ─────────────

    public (int Slots, ulong MaxCapacityBytes) MemoryArray
    {
        get
        {
            int slots = 0;
            ulong max = 0;
            foreach (var s in OfType(16))
            {
                if (s.Byte(0x05) != 3) continue; // use: system memory
                slots += s.Word(0x0D);
                uint kb = s.DWord(0x07);
                max += kb == 0x80000000 ? s.QWord(0x0F) : (ulong)kb * 1024;
            }
            return (slots, max);
        }
    }

    public List<SmBiosMemoryDevice> MemoryDevices
    {
        get
        {
            var result = new List<SmBiosMemoryDevice>();
            foreach (var s in OfType(17))
            {
                ushort size = s.Word(0x0C);
                bool installed = size != 0 && size != 0xFFFF;
                ulong bytes = size switch
                {
                    0 or 0xFFFF => 0,
                    0x7FFF => (ulong)(s.DWord(0x1C) & 0x7FFFFFFF) * 1024 * 1024,
                    _ when (size & 0x8000) != 0 => (ulong)(size & 0x7FFF) * 1024,
                    _ => (ulong)size * 1024 * 1024,
                };
                result.Add(new SmBiosMemoryDevice(
                    Locator: s.String(0x10),
                    Bank: s.String(0x11),
                    Installed: installed,
                    SizeBytes: bytes,
                    FormFactor: FormFactorName(s.Byte(0x0E)),
                    Type: MemoryTypeName(s.Byte(0x12)),
                    SpeedMts: s.Word(0x15),
                    ConfiguredSpeedMts: s.Length > 0x21 ? s.Word(0x20) : 0,
                    Manufacturer: s.String(0x17),
                    PartNumber: s.String(0x1A),
                    Rank: s.Byte(0x1B) & 0x0F,
                    ConfiguredVoltageMv: s.Length > 0x27 ? s.Word(0x26) : 0,
                    TotalWidth: s.Word(0x08),
                    DataWidth: s.Word(0x0A)));
            }
            return result;
        }
    }

    public static string FormFactorName(byte code) => code switch
    {
        0x05 => "Chip", 0x09 => "DIMM", 0x0B => "Row of chips", 0x0D => "SO-DIMM",
        0x0F => "FB-DIMM", 0x10 => "Die", 0x11 => "CAMM", _ => "Unknown",
    };

    public static string MemoryTypeName(byte code) => code switch
    {
        0x12 => "DDR", 0x13 => "DDR2", 0x18 => "DDR3", 0x1A => "DDR4", 0x1B => "LPDDR", 0x1C => "LPDDR2",
        0x1D => "LPDDR3", 0x1E => "LPDDR4", 0x22 => "DDR5", 0x23 => "LPDDR5", _ => "",
    };

    // ───────────── Type 22: portable battery ─────────────

    public SmBiosBattery? Battery
    {
        get
        {
            foreach (var s in OfType(22))
            {
                byte chemistryCode = s.Byte(0x09);
                string chemistry = chemistryCode switch
                {
                    3 => "Lead acid", 4 => "Nickel cadmium", 5 => "Nickel metal hydride", 6 => "Lithium-ion",
                    7 => "Zinc air", 8 => "Lithium-polymer", _ => s.String(0x14),
                };
                uint multiplier = Math.Max((uint)s.Byte(0x15), 1);
                return new SmBiosBattery(
                    Location: s.String(0x04),
                    Manufacturer: s.String(0x05),
                    DeviceName: s.String(0x08),
                    Chemistry: chemistry,
                    DesignCapacityMwh: s.Word(0x0A) * multiplier,
                    DesignVoltageMv: s.Word(0x0C),
                    ManufactureDate: s.String(0x06));
            }
            return null;
        }
    }
}
