using System;
using System.Collections.Generic;

namespace CoreScope.Core.Hardware;

public sealed record SpecRow(string Label, string Value)
{
    private const int LongValue = 56;

    /// <summary>First two " · " parts of a long value, so a row reads as one line plus a small caption.</summary>
    public string Main => Split().Main;

    /// <summary>The remainder of a long value (model numbers, ratings); empty for short values.</summary>
    public string Detail => Split().Detail;

    private (string Main, string Detail) Split()
    {
        if (Value.Length <= LongValue) return (Value, "");
        var parts = Value.Split(" · ");
        return parts.Length < 3 ? (Value, "") : (string.Join(" · ", parts[..2]), string.Join(" · ", parts[2..]));
    }
}

/// <summary>A titled card of label/value rows (plus optional tag chips) shown on spec pages.</summary>
public sealed class SpecSection
{
    public SpecSection(string title, string? subtitle = null)
    {
        Title = title;
        Subtitle = subtitle;
    }

    public string Title { get; }
    public string? Subtitle { get; }
    public List<SpecRow> Rows { get; } = new();
    public List<string> Tags { get; } = new();

    /// <summary>Adds a row, silently skipping empty values so cards never show blank fields.</summary>
    public SpecSection Add(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) Rows.Add(new SpecRow(label, value.Trim()));
        return this;
    }
}

public sealed record CacheInfo(int Level, string Type, long SizeKb, int Ways, int LineSize, int SharedByThreads);

public sealed class CpuSpec
{
    public string Name = "";
    public string Vendor = "";
    public string Socket = "";
    public string Architecture = "";
    public int Family, Model, Stepping;
    public int Cores, Threads, PerformanceCores, EfficiencyCores;
    public bool IsHybrid => PerformanceCores > 0 && EfficiencyCores > 0;
    public int BaseClockMhz;
    public long L2TotalKb, L3TotalKb;
    public bool? VirtualizationFirmwareEnabled;
    public string Codename = "", Microarchitecture = "", ProcessNode = "", Microcode = "", Package = "";
    public bool HasSmt;
    public List<string> Features = new();
    public List<CacheInfo> Caches = new();
}

public sealed class MemoryModule
{
    public string Slot = "";
    public string Bank = "";
    public ulong CapacityBytes;
    public int RatedMts;
    public int ConfiguredMts;
    public int? PartNumberMts;
    public string Manufacturer = "";
    public string PartNumber = "";
    public string Serial = "";
    public string TypeName = "";
    public string FormFactor = "";
    public int ConfiguredVoltageMv;
}

public sealed class BoardSpec
{
    public string SystemVendor = "", SystemModel = "", SystemFamily = "";
    /// <summary>Marketing name (Lenovo puts e.g. "IdeaPad Slim 3 15AMN8" here while Model holds the machine type).</summary>
    public string SystemVersion = "", Sku = "";
    public string BoardVendor = "", BoardModel = "", BoardVersion = "";
    public string BiosVendor = "", BiosVersion = "";
    public DateTime? BiosDate;
    public string FirmwareType = "";
    public bool? SecureBoot;
    public bool? TpmPresent;
    public string TpmVersion = "";
    public string TpmManufacturer = "";
}

public sealed class GpuSpec
{
    public string Name = "";
    public string Vendor = "";
    public string DriverVersion = "";
    public DateTime? DriverDate;
    public ulong VramBytes;
    public string Resolution = "";
    public int RefreshHz;
    public string Processor = "";
    public string PnpId = "";
    public bool IsIntegrated;
    public string BiosVersion = "", ChipType = "", MemoryKind = "";
    public PcieLink? Link;
}

public sealed class DisplaySpec
{
    public string Name = "";
    public string Manufacturer = "";
    public double? DiagonalInches;
    public int Year;
}

public sealed class DiskSpec
{
    public string Name = "";
    public string MediaType = "";
    public string BusType = "";
    public ulong SizeBytes;
    public string Health = "";
    public string Serial = "";
    public string Firmware = "";
    public int? WearPercent;
    public int? TemperatureC;
    public int? TemperatureMaxC;
    public ulong? PowerOnHours;
    public ulong? ReadErrors;
    public int DiskNumber = -1;
    public string PartitionStyle = "";
    public bool IsBoot;
    public PcieLink? Link;
}

public sealed class VolumeSpec
{
    public string Name = "";
    public string Label = "";
    public string Format = "";
    public long TotalBytes;
    public long FreeBytes;
    public double FreeFraction => TotalBytes > 0 ? (double)FreeBytes / TotalBytes : 1;
}

public sealed class BatterySpec
{
    public string Name = "";
    public string Manufacturer = "";
    public string Chemistry = "";
    public uint DesignMwh;
    public uint FullChargeMwh;
    public int? Cycles;
    public int? ChargePercent;
    public double? WearPercent => DesignMwh > 0 && FullChargeMwh > 0
        ? Math.Max(0, 100.0 * (1 - (double)FullChargeMwh / DesignMwh))
        : null;
}

public sealed class OsSpec
{
    public string Name = "";
    public string DisplayVersion = "";
    public string Build = "";
    public string Architecture = "";
    public string ComputerName = "";
    public DateTime? InstallDate;
    public DateTime? LastBoot;
    public string PowerPlan = "";
    public bool? HypervisorPresent;
    public bool IsAdmin;
    public bool? TrimEnabled;
    public bool? GpuSchedulingEnabled;
}

/// <summary>Everything collected once at startup. Sections are the display form; fields feed insights.</summary>
public sealed class SystemSpec
{
    public CpuSpec Cpu = new();
    public List<MemoryModule> Modules = new();
    public int MemorySlots;
    public ulong MemoryMaxCapacityBytes;
    public ulong TotalRamBytes;
    public BoardSpec Board = new();
    public List<GpuSpec> Gpus = new();
    public List<DisplaySpec> Displays = new();
    public List<DiskSpec> Disks = new();
    public List<VolumeSpec> Volumes = new();
    public BatterySpec? Battery;
    public OsSpec Os = new();
    public UpgradeReport? Upgrade;
    public SmBios? SmBios;
    public List<DeviceCategory> Devices = new();

    public List<SpecSection> CpuSections = new();
    public List<SpecSection> MemorySections = new();
    public List<SpecSection> GpuSections = new();
    public List<SpecSection> StorageSections = new();
    public List<SpecSection> BoardSections = new();
    public List<SpecSection> SystemSections = new();

    public string MemoryTypeSummary
    {
        get
        {
            if (Modules.Count == 0) return "";
            var m = Modules[0];
            var speed = m.ConfiguredMts > 0 ? m.ConfiguredMts : m.RatedMts;
            return speed > 0 ? $"{m.TypeName} {speed} MT/s".Trim() : m.TypeName;
        }
    }
}
