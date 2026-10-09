using System;
using Microsoft.Win32;

namespace CoreScope.Core.Hardware;

public sealed record CpuIdentity(string Codename, string Microarchitecture, string ProcessNode);

/// <summary>
/// Maps CPUID family/model to codename, core architecture and manufacturing process — the "Code Name"
/// and "Technology" fields CPU-Z shows. Covers mainstream client/server parts from ~2013 onward.
/// </summary>
public static class CpuDatabase
{
    public static CpuIdentity? Lookup(string vendor, int family, int model) => vendor switch
    {
        "AMD" => Amd(family, model),
        "Intel" when family == 6 => Intel(model),
        _ => null,
    };

    private static CpuIdentity? Amd(int family, int model) => family switch
    {
        0x17 => model switch
        {
            0x01 => new("Summit Ridge / Naples", "Zen", "14 nm"),
            0x08 => new("Pinnacle Ridge / Colfax", "Zen+", "12 nm"),
            0x11 => new("Raven Ridge", "Zen", "14 nm"),
            0x18 => new("Picasso", "Zen+", "12 nm"),
            0x20 => new("Dali / Pollock", "Zen", "14 nm"),
            0x31 => new("Rome / Castle Peak", "Zen 2", "7 nm"),
            0x60 => new("Renoir", "Zen 2", "7 nm"),
            0x68 => new("Lucienne", "Zen 2", "7 nm"),
            0x71 => new("Matisse", "Zen 2", "7 nm"),
            0x90 or 0x91 => new("Van Gogh", "Zen 2", "7 nm"),
            0xA0 => new("Mendocino", "Zen 2", "6 nm"),
            _ => new("", "Zen / Zen+ / Zen 2", ""),
        },
        0x19 => model switch
        {
            0x01 => new("Milan", "Zen 3", "7 nm"),
            0x08 => new("Chagall", "Zen 3", "7 nm"),
            >= 0x10 and <= 0x1F => new("Genoa / Storm Peak", "Zen 4", "5 nm"),
            0x21 => new("Vermeer", "Zen 3", "7 nm"),
            >= 0x40 and <= 0x4F => new("Rembrandt", "Zen 3+", "6 nm"),
            0x50 => new("Cezanne / Barcelo", "Zen 3", "7 nm"),
            >= 0x60 and <= 0x6F => new("Raphael", "Zen 4", "5 nm"),
            >= 0x70 and <= 0x7F => new("Phoenix / Hawk Point", "Zen 4", "4 nm"),
            >= 0xA0 and <= 0xAF => new("Bergamo / Siena", "Zen 4c", "5 nm"),
            _ => new("", "Zen 3 / Zen 4", ""),
        },
        0x1A => model switch
        {
            <= 0x1F => new("Turin", "Zen 5", "4 nm / 3 nm"),
            >= 0x20 and <= 0x2F => new("Strix Point", "Zen 5", "4 nm"),
            >= 0x40 and <= 0x4F => new("Granite Ridge", "Zen 5", "4 nm"),
            >= 0x60 and <= 0x6F => new("Krackan Point", "Zen 5", "4 nm"),
            >= 0x70 and <= 0x7F => new("Strix Halo", "Zen 5", "4 nm"),
            _ => new("", "Zen 5", ""),
        },
        _ => null,
    };

    private static CpuIdentity? Intel(int model) => model switch
    {
        0x3C or 0x3F or 0x45 or 0x46 => new("Haswell", "Haswell", "22 nm"),
        0x3D or 0x47 or 0x4F or 0x56 => new("Broadwell", "Broadwell", "14 nm"),
        0x4E or 0x5E or 0x55 => new("Skylake", "Skylake", "14 nm"),
        0x8E => new("Kaby / Whiskey / Amber / Comet Lake", "Skylake-derived", "14 nm"),
        0x9E => new("Kaby / Coffee Lake", "Skylake-derived", "14 nm"),
        0xA5 or 0xA6 => new("Comet Lake", "Skylake-derived", "14 nm"),
        0xA7 => new("Rocket Lake", "Cypress Cove", "14 nm"),
        0x7D or 0x7E => new("Ice Lake", "Sunny Cove", "10 nm"),
        0x6A or 0x6C => new("Ice Lake-SP", "Sunny Cove", "10 nm"),
        0x8C or 0x8D => new("Tiger Lake", "Willow Cove", "10 nm SuperFin"),
        0x8F => new("Sapphire Rapids", "Golden Cove", "Intel 7"),
        0x97 or 0x9A => new("Alder Lake", "Golden Cove + Gracemont", "Intel 7"),
        0xBE => new("Alder Lake-N", "Gracemont", "Intel 7"),
        0xB7 or 0xBA or 0xBF => new("Raptor Lake", "Raptor Cove + Gracemont", "Intel 7"),
        0xAA or 0xAC => new("Meteor Lake", "Redwood Cove + Crestmont", "Intel 4"),
        0xBD => new("Lunar Lake", "Lion Cove + Skymont", "TSMC N3B"),
        0xC5 or 0xC6 or 0xB5 => new("Arrow Lake", "Lion Cove + Skymont", "TSMC N3B"),
        0xCC => new("Panther Lake", "Cougar Cove + Darkmont", "Intel 18A"),
        0x9C or 0x96 => new("Jasper / Elkhart Lake", "Tremont", "10 nm"),
        _ => null,
    };

    /// <summary>Microcode revision loaded by Windows, e.g. "0x0A50000C".</summary>
    public static string ReadMicrocode()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("Update Revision") is byte[] { Length: >= 8 } revision)
            {
                // Intel stores the revision in the high DWORD, AMD in the low DWORD.
                uint low = BitConverter.ToUInt32(revision, 0), high = BitConverter.ToUInt32(revision, 4);
                uint value = high != 0 ? high : low;
                return value != 0 ? $"0x{value:X8}" : "";
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            Log.Error("Reading microcode revision", ex);
        }
        return "";
    }
}
