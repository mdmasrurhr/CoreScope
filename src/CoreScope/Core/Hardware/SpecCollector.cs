using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CoreScope.Core.Hardware;

/// <summary>
/// One-shot collection of static hardware facts (WMI, registry, CPUID). Each area is isolated so a
/// failure in one never blanks the others; failures are written to the log.
/// </summary>
public static class SpecCollector
{
    public static SystemSpec Collect()
    {
        var spec = new SystemSpec();
        var watch = Stopwatch.StartNew();
        spec.Os.IsAdmin = Native.IsAdministrator();
        // The driver query behind the device list is slow (seconds), so start it first and run it in parallel.
        var devicesTask = System.Threading.Tasks.Task.Run(() =>
        {
            var w = Stopwatch.StartNew();
            try { return DeviceCatalog.Collect(); }
            catch (Exception ex) { Log.Error("Collecting Devices", ex); return new List<DeviceCategory>(); }
            finally { Log.Info($"Devices (parallel): {w.ElapsedMilliseconds} ms"); }
        });
        Run("SMBIOS", () => spec.SmBios = SmBios.Read());
        Run("CPU", () => CollectCpu(spec));
        Run("Memory", () => CollectMemory(spec));
        Run("Board", () => CollectBoard(spec));
        Run("GPU", () => CollectGpus(spec));
        Run("Storage", () => CollectStorage(spec));
        Run("OS", () => CollectOs(spec));
        Run("Battery", () => CollectBattery(spec));
        Run("Devices (wait)", () => spec.Devices = devicesTask.GetAwaiter().GetResult());
        Run("Upgrade advisor", () => spec.Upgrade = UpgradeAdvisor.Build(spec));
        Run("Sections", () => BuildSections(spec));
        Log.Info($"Spec collection finished in {watch.ElapsedMilliseconds} ms");
        return spec;
    }

    private static void Run(string area, Action action)
    {
        var watch = Stopwatch.StartNew();
        try { action(); }
        catch (Exception ex) { Log.Error($"Collecting {area}", ex); }
        Log.Info($"{area}: {watch.ElapsedMilliseconds} ms");
    }

    // ───────────────────────────── CPU ─────────────────────────────

    private static void CollectCpu(SystemSpec spec)
    {
        var cpu = spec.Cpu;
        var wmi = Wmi.First("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation, VirtualizationFirmwareEnabled, Architecture FROM Win32_Processor");
        cpu.Name = wmi.Str("Name");
        cpu.Socket = wmi.Str("SocketDesignation");
        cpu.Cores = wmi.Int("NumberOfCores");
        cpu.Threads = wmi.Int("NumberOfLogicalProcessors");
        cpu.BaseClockMhz = wmi.Int("MaxClockSpeed");
        cpu.L2TotalKb = (long)wmi.ULong("L2CacheSize");
        cpu.L3TotalKb = (long)wmi.ULong("L3CacheSize");
        cpu.VirtualizationFirmwareEnabled = wmi.Bool("VirtualizationFirmwareEnabled");
        cpu.Architecture = wmi.Int("Architecture") switch
        {
            0 => "x86", 5 => "ARM", 9 => "x64", 12 => "ARM64", _ => "",
        };

        CpuId.Fill(cpu); // overrides the name with the cleaner CPUID brand string when available
        cpu.Name = Format.CleanName(cpu.Name);

        var topology = Native.GetCoreTopology();
        if (topology is not null)
        {
            cpu.Cores = topology.Cores;
            cpu.Threads = topology.Threads;
            cpu.PerformanceCores = topology.PerformanceCores;
            cpu.EfficiencyCores = topology.EfficiencyCores;
            cpu.HasSmt = topology.Smt;
        }
        if (cpu.Threads == 0) cpu.Threads = Environment.ProcessorCount;

        if (CpuDatabase.Lookup(cpu.Vendor, cpu.Family, cpu.Model) is { } id)
        {
            cpu.Codename = id.Codename;
            cpu.Microarchitecture = id.Microarchitecture;
            cpu.ProcessNode = id.ProcessNode;
        }
        cpu.Microcode = CpuDatabase.ReadMicrocode();
        if (spec.SmBios?.ProcessorSocket is { } socket)
        {
            if (socket.Socket.Length > 0) cpu.Socket = socket.Socket;
            if (socket.Upgrade is not ("" or "Other" or "Unknown" or "None")) cpu.Package = socket.Upgrade;
        }
    }

    // ───────────────────────────── Memory ─────────────────────────────

    private static readonly Regex KnownSpeeds = new(
        @"(?<!\d)(2133|2400|2666|2667|2800|2933|3000|3200|3333|3466|3600|3733|3800|3866|4000|4133|4266|4400|4600|4800|5200|5600|6000|6200|6400|6600|6800|7000|7200|7600|8000|8200|8400)(?!\d)",
        RegexOptions.Compiled);

    /// <summary>
    /// Many enthusiast kits encode their XMP/EXPO speed in the part number
    /// (F4-3600C16…, CMK32GX4M2E3200C16, KF436C17…, BL8G32C16…). Returns null when it can't tell.
    /// </summary>
    public static int? SpeedFromPartNumber(string part)
    {
        if (string.IsNullOrWhiteSpace(part)) return null;
        var p = part.Trim().ToUpperInvariant();

        var kingston = Regex.Match(p, @"^KF(\d)(\d{2})C");
        if (kingston.Success) return int.Parse(kingston.Groups[2].Value, CultureInfo.InvariantCulture) * 100;

        var ballistix = Regex.Match(p, @"^BL\d+G(\d{2})C");
        if (ballistix.Success) return int.Parse(ballistix.Groups[1].Value, CultureInfo.InvariantCulture) * 100;

        var known = KnownSpeeds.Match(p);
        return known.Success ? int.Parse(known.Value, CultureInfo.InvariantCulture) : null;
    }

    private static string MemoryTypeName(int smbiosType) => smbiosType switch
    {
        20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2",
        29 => "LPDDR3", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", _ => "",
    };

    /// <summary>CIM_PhysicalMemory.FormFactor (note: a different numbering from raw SMBIOS).</summary>
    private static string FormFactorName(int formFactor) => formFactor switch
    {
        8 => "DIMM", 12 => "SO-DIMM", 13 => "SRIMM", 14 or 21 or 22 => "Soldered", _ => "",
    };

    /// <summary>JEDEC manufacturer IDs that firmware sometimes reports instead of a name.</summary>
    private static string MemoryVendor(string raw)
    {
        var key = raw.Trim().ToUpperInvariant();
        if (key.StartsWith("0X", StringComparison.Ordinal)) key = key[2..];
        if (key.Length > 4) key = key[..4];
        return key switch
        {
            "80CE" or "CE00" => "Samsung",
            "80AD" or "AD00" => "SK hynix",
            "802C" or "2C00" => "Micron",
            "859B" => "Crucial",
            "0198" or "9801" => "Kingston",
            "04CD" or "CD04" => "G.Skill",
            "029E" or "9E02" => "Corsair",
            "04CB" => "ADATA",
            "0B48" => "Lexar",
            "8551" => "Qimonda",
            _ => raw.Trim(),
        };
    }

    private static void CollectMemory(SystemSpec spec)
    {
        foreach (var m in Wmi.Query("SELECT BankLabel, DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, SerialNumber, SMBIOSMemoryType, FormFactor, ConfiguredVoltage FROM Win32_PhysicalMemory"))
        {
            var module = new MemoryModule
            {
                Slot = m.Str("DeviceLocator"),
                Bank = m.Str("BankLabel"),
                CapacityBytes = m.ULong("Capacity"),
                RatedMts = m.Int("Speed"),
                ConfiguredMts = m.Int("ConfiguredClockSpeed"),
                Manufacturer = MemoryVendor(m.Str("Manufacturer")),
                PartNumber = m.Str("PartNumber"),
                Serial = m.Str("SerialNumber"),
                TypeName = MemoryTypeName(m.Int("SMBIOSMemoryType")),
                FormFactor = FormFactorName(m.Int("FormFactor")),
                ConfiguredVoltageMv = m.Int("ConfiguredVoltage"),
            };
            module.PartNumberMts = SpeedFromPartNumber(module.PartNumber);
            spec.Modules.Add(module);
        }

        var array = Wmi.First("SELECT MemoryDevices, MaxCapacityEx FROM Win32_PhysicalMemoryArray");
        spec.MemorySlots = array.Int("MemoryDevices");
        spec.MemoryMaxCapacityBytes = array.ULong("MaxCapacityEx") * 1024; // reported in KB

        spec.TotalRamBytes = spec.Modules.Aggregate(0UL, (sum, m) => sum + m.CapacityBytes);
        if (spec.TotalRamBytes == 0 && Native.GetMemoryStatus() is { } status) spec.TotalRamBytes = status.Total;
    }

    // ───────────────────────────── Board / firmware ─────────────────────────────

    private static void CollectBoard(SystemSpec spec)
    {
        var b = spec.Board;
        var system = Wmi.First("SELECT Manufacturer, Model, SystemFamily, SystemSKUNumber, HypervisorPresent FROM Win32_ComputerSystem");
        b.SystemVendor = system.Str("Manufacturer");
        b.SystemModel = system.Str("Model");
        b.SystemFamily = system.Str("SystemFamily");
        b.Sku = system.Str("SystemSKUNumber");
        b.SystemVersion = Wmi.First("SELECT Version FROM Win32_ComputerSystemProduct").Str("Version");
        spec.Os.HypervisorPresent = system.Bool("HypervisorPresent");

        var board = Wmi.First("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard");
        b.BoardVendor = board.Str("Manufacturer");
        b.BoardModel = board.Str("Product");
        b.BoardVersion = board.Str("Version");

        var bios = Wmi.First("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
        b.BiosVendor = bios.Str("Manufacturer");
        b.BiosVersion = bios.Str("SMBIOSBIOSVersion");
        b.BiosDate = bios.Date("ReleaseDate");

        b.FirmwareType = Native.GetFirmwareTypeName();
        b.SecureBoot = ReadDword(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled") is { } sb ? sb == 1 : null;

        // The TPM namespace is admin-only; without elevation WMI takes ~5 s just to say "access denied".
        var tpm = spec.Os.IsAdmin ? Wmi.First("SELECT SpecVersion, IsEnabled_InitialValue, ManufacturerIdTxt FROM Win32_Tpm", Wmi.Tpm) : null;
        if (tpm is null)
        {
            b.TpmPresent = spec.Os.IsAdmin ? false : null; // without admin rights the class is not readable
        }
        else
        {
            b.TpmPresent = tpm.Bool("IsEnabled_InitialValue") ?? true;
            b.TpmVersion = tpm.Str("SpecVersion").Split(',')[0].Trim();
            b.TpmManufacturer = tpm.Str("ManufacturerIdTxt");
        }
    }

    // ───────────────────────────── GPU / displays ─────────────────────────────

    private static void CollectGpus(SystemSpec spec)
    {
        var vramByName = ReadVramFromRegistry();
        foreach (var g in Wmi.Query("SELECT Name, PNPDeviceID, AdapterRAM, DriverVersion, DriverDate, VideoProcessor, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController"))
        {
            var name = Format.CleanName(g.Str("Name"));
            if (name.Length == 0) continue;
            var pnp = g.Str("PNPDeviceID").ToUpperInvariant();
            var gpu = new GpuSpec
            {
                Name = name,
                DriverVersion = g.Str("DriverVersion"),
                DriverDate = g.Date("DriverDate"),
                Processor = g.Str("VideoProcessor"),
                PnpId = g.Str("PNPDeviceID"),
                RefreshHz = g.Int("CurrentRefreshRate"),
                Vendor = pnp.Contains("VEN_10DE") ? "NVIDIA"
                       : pnp.Contains("VEN_1002") ? "AMD"
                       : pnp.Contains("VEN_8086") ? "Intel"
                       : pnp.Contains("VEN_1414") ? "Microsoft" : "",
            };
            int w = g.Int("CurrentHorizontalResolution"), h = g.Int("CurrentVerticalResolution");
            if (w > 0 && h > 0) gpu.Resolution = $"{w} × {h}";

            if (vramByName.TryGetValue(name, out var reg))
            {
                gpu.VramBytes = reg.Vram;
                gpu.BiosVersion = reg.Bios;
                gpu.ChipType = reg.Chip;
                gpu.MemoryKind = reg.MemoryKind;
            }
            if (gpu.VramBytes == 0) gpu.VramBytes = g.ULong("AdapterRAM");
            gpu.IsIntegrated = gpu.Vendor switch
            {
                "Intel" => !Regex.IsMatch(name, @"Arc\s*(\(TM\)\s*)?[AB]\d", RegexOptions.IgnoreCase),
                "AMD" => !Regex.IsMatch(name, @"\b(RX|Pro|FirePro|Vega \d{2})\b", RegexOptions.IgnoreCase),
                _ => false,
            };
            spec.Gpus.Add(gpu);
        }

        // Discrete GPUs first: they are what people usually mean by "the GPU".
        spec.Gpus.Sort((a, b) => a.IsIntegrated.CompareTo(b.IsIntegrated));
        foreach (var gpu in spec.Gpus.Where(g => !g.IsIntegrated && g.PnpId.Length > 0)) gpu.Link = DeviceProbe.GetPcieLink(gpu.PnpId);
        spec.Os.GpuSchedulingEnabled = ReadDword(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode") is { } hags ? hags == 2 : null;

        var sizes = Wmi.Query("SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize FROM WmiMonitorBasicDisplayParams", Wmi.RootWmi);
        foreach (var mon in Wmi.Query("SELECT InstanceName, UserFriendlyName, ManufacturerName, YearOfManufacture FROM WmiMonitorID", Wmi.RootWmi))
        {
            var display = new DisplaySpec
            {
                Name = mon.CharArray("UserFriendlyName"),
                Manufacturer = mon.CharArray("ManufacturerName"),
                Year = mon.Int("YearOfManufacture"),
            };
            var instance = mon.Str("InstanceName");
            var size = sizes.FirstOrDefault(s => s.Str("InstanceName") == instance);
            int cmW = size.Int("MaxHorizontalImageSize"), cmH = size.Int("MaxVerticalImageSize");
            if (cmW > 0 && cmH > 0) display.DiagonalInches = Math.Sqrt(cmW * cmW + cmH * cmH) / 2.54;
            if (display.Name.Length == 0) display.Name = "Built-in / generic display";
            spec.Displays.Add(display);
        }
    }

    /// <summary>Win32_VideoController.AdapterRAM is 32-bit and caps at 4 GB; the driver's registry value is exact.</summary>
    private sealed record GpuRegistryInfo(ulong Vram, string Bios, string Chip, string MemoryKind);

    /// <summary>HardwareInformation.* values are REG_SZ on some drivers and UTF-16 REG_BINARY on others.</summary>
    private static string RegText(object? value) => value switch
    {
        string s => s.Trim('\0', ' '),
        byte[] b => System.Text.Encoding.Unicode.GetString(b).Trim('\0', ' '),
        _ => "",
    };

    private static Dictionary<string, GpuRegistryInfo> ReadVramFromRegistry()
    {
        var result = new Dictionary<string, GpuRegistryInfo>(StringComparer.OrdinalIgnoreCase);
        const string classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(classKey);
            if (root is null) return result;
            foreach (var subName in root.GetSubKeyNames())
            {
                if (!Regex.IsMatch(subName, @"^\d{4}$")) continue;
                try
                {
                    using var sub = root.OpenSubKey(subName);
                    if (sub?.GetValue("DriverDesc") is not string desc) continue;
                    ulong size = sub.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long l => (ulong)l,
                        byte[] bytes when bytes.Length >= 8 => BitConverter.ToUInt64(bytes, 0),
                        _ => sub.GetValue("HardwareInformation.MemorySize") switch
                        {
                            int i => unchecked((uint)i),
                            byte[] bytes when bytes.Length >= 4 => BitConverter.ToUInt32(bytes, 0),
                            _ => 0,
                        },
                    };
                    var bios = RegText(sub.GetValue("HardwareInformation.BiosString"));
                    var chip = RegText(sub.GetValue("HardwareInformation.ChipType"));
                    var memKind = RegText(sub.GetValue("HardwareInformation.MemoryType"));
                    if (size > 0 || bios.Length > 0) result[Format.CleanName(desc)] = new GpuRegistryInfo(size, bios, chip, memKind);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            Log.Error("Reading VRAM from registry", ex);
        }
        return result;
    }

    // ───────────────────────────── Storage ─────────────────────────────

    private static string BusTypeName(int bus) => bus switch
    {
        1 => "SCSI", 3 => "ATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA", 12 => "SD",
        13 => "MMC", 14 or 15 => "Virtual", 16 => "Storage Spaces", 17 => "NVMe", 19 => "UFS", _ => "",
    };

    private static void CollectStorage(SystemSpec spec)
    {
        var counters = spec.Os.IsAdmin
            ? Wmi.Query("SELECT DeviceId, Temperature, TemperatureMax, Wear, PowerOnHours, ReadErrorsTotal FROM MSFT_StorageReliabilityCounter", Wmi.Storage)
            : new List<System.Management.ManagementBaseObject>();
        var disks = Wmi.Query("SELECT DeviceId, FriendlyName, MediaType, BusType, HealthStatus, Size, SerialNumber, FirmwareVersion FROM MSFT_PhysicalDisk", Wmi.Storage);

        for (int i = 0; i < disks.Count; i++)
        {
            var d = disks[i];
            var disk = new DiskSpec
            {
                Name = d.Str("FriendlyName"),
                SizeBytes = d.ULong("Size"),
                Serial = d.Str("SerialNumber"),
                Firmware = d.Str("FirmwareVersion"),
                BusType = BusTypeName(d.Int("BusType")),
                MediaType = d.Int("MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "" },
                Health = d.Int("HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" },
            };
            if (disk.MediaType == "" && disk.BusType == "NVMe") disk.MediaType = "SSD";

            var id = d.Str("DeviceId");
            var counter = counters.FirstOrDefault(c => c.Str("DeviceId") == id)
                          ?? (counters.Count == disks.Count ? counters[i] : null);
            if (counter is not null)
            {
                disk.TemperatureC = NonZero(counter.Int("Temperature"));
                disk.TemperatureMaxC = NonZero(counter.Int("TemperatureMax"));
                disk.PowerOnHours = counter.ULong("PowerOnHours") is var h and > 0 ? h : null;
                disk.ReadErrors = counter.ULong("ReadErrorsTotal");
                // "Wear" is percentage of rated endurance used. 0 is ambiguous (new drive, or not reported).
                if (disk.MediaType == "SSD") disk.WearPercent = counter.Int("Wear");
            }
            spec.Disks.Add(disk);
        }

        var logical = Wmi.Query("SELECT Number, PartitionStyle, IsBoot, IsSystem, FriendlyName FROM MSFT_Disk", Wmi.Storage);
        var links = DeviceProbe.GetDiskLinks();
        foreach (var disk in spec.Disks)
        {
            var match = logical.FirstOrDefault(l => l.Str("FriendlyName").Equals(disk.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                disk.DiskNumber = match.Int("Number");
                disk.PartitionStyle = match.Int("PartitionStyle") switch { 1 => "MBR", 2 => "GPT", 0 => "Not initialized", _ => "" };
                disk.IsBoot = match.Bool("IsBoot") == true || match.Bool("IsSystem") == true;
            }
            var norm = Regex.Replace(disk.Name.ToUpperInvariant(), "[^A-Z0-9]", "");
            disk.Link = links.FirstOrDefault(l =>
            {
                var m = Regex.Replace(l.Model.ToUpperInvariant(), "[^A-Z0-9]", "");
                return m.Length > 0 && (m.Contains(norm) || norm.Contains(m));
            })?.Link;
        }
        spec.Os.TrimEnabled = ReadTrimEnabled();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                spec.Volumes.Add(new VolumeSpec
                {
                    Name = drive.Name.TrimEnd('\\'),
                    Label = drive.VolumeLabel,
                    Format = drive.DriveFormat,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace,
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static int? NonZero(int value) => value > 0 ? value : null;

    /// <summary>"DisableDeleteNotify = 0" means TRIM is enabled; the "= n" part is locale-independent.</summary>
    private static bool? ReadTrimEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo("fsutil", "behavior query DisableDeleteNotify")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            var m = Regex.Match(output, @"NTFS\s+DisableDeleteNotify\s*=\s*(\d)");
            return m.Success ? m.Groups[1].Value == "0" : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("fsutil TRIM query", ex);
            return null;
        }
    }

    // ───────────────────────────── OS ─────────────────────────────

    private static void CollectOs(SystemSpec spec)
    {
        var os = spec.Os;
        var w = Wmi.First("SELECT Caption, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime, CSName FROM Win32_OperatingSystem");
        os.Name = w.Str("Caption").Replace("Microsoft ", "");
        os.Architecture = w.Str("OSArchitecture");
        os.InstallDate = w.Date("InstallDate");
        os.LastBoot = w.Date("LastBootUpTime");
        os.ComputerName = w.Str("CSName");

        const string ntKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        os.DisplayVersion = ReadString(ntKey, "DisplayVersion") ?? "";
        var build = w.Str("BuildNumber");
        var ubr = ReadDword(ntKey, "UBR");
        os.Build = ubr is { } u ? $"{build}.{u}" : build;

        // Windows 11 still reports "Windows 10" in some legacy fields; the caption is reliable, but double-check the build.
        if (int.TryParse(build, out var buildNumber) && buildNumber >= 22000 && os.Name.Contains("Windows 10"))
            os.Name = os.Name.Replace("Windows 10", "Windows 11");

        os.PowerPlan = ReadString(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", "ActivePowerScheme")?.ToLowerInvariant() switch
        {
            "381b4222-f694-41f0-9685-ff5bb260df2e" => "Balanced",
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => "High performance",
            "a1841308-3541-4fab-bc81-f71556f20b4a" => "Power saver",
            "e9a42b02-d5df-448d-aa00-03f14749eb61" => "Ultimate Performance",
            null => "",
            _ => "Custom",
        };
    }

    // ───────────────────────────── Battery ─────────────────────────────

    private static void CollectBattery(SystemSpec spec)
    {
        var win32 = Wmi.First("SELECT Name, EstimatedChargeRemaining FROM Win32_Battery");
        if (win32 is null) return;

        var battery = new BatterySpec
        {
            Name = win32.Str("Name"),
            ChargePercent = win32.Int("EstimatedChargeRemaining"),
        };
        var stat = Wmi.First("SELECT DesignedCapacity, ManufactureName, DeviceName, Chemistry FROM BatteryStaticData", Wmi.RootWmi);
        if (stat is not null)
        {
            battery.DesignMwh = (uint)stat.ULong("DesignedCapacity");
            battery.Manufacturer = stat.Str("ManufactureName");
            var deviceName = stat.Str("DeviceName");
            if (deviceName.Length > 0) battery.Name = deviceName;
            battery.Chemistry = DecodeChemistry((uint)stat.ULong("Chemistry"));
        }
        battery.FullChargeMwh = (uint)Wmi.First("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", Wmi.RootWmi).ULong("FullChargedCapacity");
        var cycles = Wmi.First("SELECT CycleCount FROM BatteryCycleCount", Wmi.RootWmi);
        if (cycles is not null && cycles.Int("CycleCount") > 0) battery.Cycles = cycles.Int("CycleCount");
        spec.Battery = battery;
    }

    private static string DecodeChemistry(uint code)
    {
        if (code == 0) return "";
        var chars = new[] { (char)(code & 0xFF), (char)((code >> 8) & 0xFF), (char)((code >> 16) & 0xFF), (char)((code >> 24) & 0xFF) };
        var text = new string(chars).Trim('\0', ' ').ToUpperInvariant();
        return text switch
        {
            "LION" or "LI-I" => "Lithium-ion",
            "LIP" or "LIPO" => "Lithium-polymer",
            "NIMH" => "NiMH",
            _ => text.All(char.IsLetterOrDigit) ? text : "",
        };
    }

    // ───────────────────────────── Registry helpers ─────────────────────────────

    private static string? ReadString(string path, string name)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(path);
            return key?.GetValue(name)?.ToString();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return null; }
    }

    private static int? ReadDword(string path, string name)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(path);
            return key?.GetValue(name) is int value ? value : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return null; }
    }

    public static bool IsServiceInstalled(string serviceName)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }

    // ───────────────────────────── Display sections ─────────────────────────────

    private static void BuildSections(SystemSpec spec)
    {
        BuildCpuSections(spec);
        BuildMemorySections(spec);
        BuildGpuSections(spec);
        BuildStorageSections(spec);
        BuildBoardSections(spec);
        BuildSystemSections(spec);
    }

    private static void BuildCpuSections(SystemSpec spec)
    {
        var cpu = spec.Cpu;
        var identity = new SpecSection("Processor")
            .Add("Name", cpu.Name)
            .Add("Vendor", cpu.Vendor)
            .Add("Code name", cpu.Codename)
            .Add("Core architecture", cpu.Microarchitecture)
            .Add("Process technology", cpu.ProcessNode)
            .Add("Instruction set", cpu.Architecture)
            .Add("Socket / package", cpu.Package.Length > 0 && cpu.Package != cpu.Socket ? $"{cpu.Socket} ({cpu.Package})" : cpu.Socket)
            .Add("Family · Model · Stepping", cpu.Family > 0
                ? $"{cpu.Family} · {cpu.Model} · {cpu.Stepping}   (0x{cpu.Family:X2}_{cpu.Model:X2}h)"
                : null)
            .Add("Base clock", cpu.BaseClockMhz > 0 ? Format.Mhz(cpu.BaseClockMhz) : null)
            .Add("Microcode revision", cpu.Microcode)
            .Add("Integrated graphics", spec.Gpus.FirstOrDefault(g => g.IsIntegrated)?.Name)
            .Add("Virtualization in firmware", cpu.VirtualizationFirmwareEnabled is null ? null : Format.YesNo(cpu.VirtualizationFirmwareEnabled, "Enabled", "Disabled"));
        spec.CpuSections.Add(identity);

        var topology = new SpecSection("Cores & threads")
            .Add("Physical cores", cpu.Cores > 0 ? cpu.Cores.ToString(CultureInfo.InvariantCulture) : null)
            .Add("Logical processors (threads)", cpu.Threads.ToString(CultureInfo.InvariantCulture));
        if (cpu.IsHybrid)
        {
            topology.Add("Performance cores (P)", cpu.PerformanceCores.ToString(CultureInfo.InvariantCulture))
                    .Add("Efficiency cores (E)", cpu.EfficiencyCores.ToString(CultureInfo.InvariantCulture))
                    .Add("Design", "Hybrid (big.LITTLE-style)");
        }
        topology.Add(cpu.Vendor == "Intel" ? "Hyper-Threading" : "SMT", cpu.HasSmt ? "Enabled" : "Not active");
        spec.CpuSections.Add(topology);

        var cache = new SpecSection("Cache", cpu.Caches.Count > 0 ? "Read directly from CPUID — per-instance size and geometry" : null);
        foreach (var c in cpu.Caches)
        {
            string label = c.Type == "Unified" ? $"L{c.Level}" : $"L{c.Level} {c.Type}";
            string shared = c.SharedByThreads <= 2 ? "per core" : $"shared by up to {c.SharedByThreads} threads";
            cache.Add(label, $"{Format.Kb(c.SizeKb)} · {c.Ways}-way · {c.LineSize}-byte lines · {shared}");
        }
        cache.Add("L2 total", cpu.L2TotalKb > 0 ? Format.Kb(cpu.L2TotalKb) : null)
             .Add("L3 total", cpu.L3TotalKb > 0 ? Format.Kb(cpu.L3TotalKb) : null);
        if (cache.Rows.Count > 0) spec.CpuSections.Add(cache);

        if (cpu.Features.Count > 0)
        {
            var isa = new SpecSection("Instruction sets", $"{cpu.Features.Count} extensions supported");
            isa.Tags.AddRange(cpu.Features);
            spec.CpuSections.Add(isa);
        }
    }

    private static void BuildMemorySections(SystemSpec spec)
    {
        var summary = new SpecSection("Summary")
            .Add("Installed", spec.TotalRamBytes > 0 ? Format.Bytes(spec.TotalRamBytes) : null);

        if (spec.Modules.Count > 0)
        {
            var first = spec.Modules[0];
            summary.Add("Type", $"{first.TypeName} {first.FormFactor}".Trim());
            if (first.ConfiguredMts > 0)
                summary.Add("Running at", $"{first.ConfiguredMts} MT/s  ({first.ConfiguredMts / 2} MHz actual clock)");
            if (first.RatedMts > 0) summary.Add("Rated speed (firmware)", $"{first.RatedMts} MT/s");
            var kitSpeed = spec.Modules.Select(m => m.PartNumberMts ?? 0).Max();
            if (kitSpeed > 0) summary.Add("Kit speed (from part number)", $"{kitSpeed} MT/s");
        }
        bool allSoldered = spec.SmBios?.MemoryDevices.Where(d => d.Installed).ToList() is { Count: > 0 } installedDevices && installedDevices.All(d => d.IsSoldered);
        summary.Add(allSoldered ? "Memory packages" : "Slots used",
                    allSoldered ? $"{spec.Modules.Count} (soldered — no slots)"
                    : spec.MemorySlots > 0 ? $"{spec.Modules.Count} of {spec.MemorySlots}" : spec.Modules.Count.ToString(CultureInfo.InvariantCulture))
               .Add("Channel mode (estimate)", spec.Modules.Count switch
               {
                   0 => null,
                   1 => "Single-channel",
                   _ => allSoldered ? "Multi-channel (soldered packages are wired in parallel)" : "Dual-channel or better (multiple modules)",
               })
               .Add("Max supported by board", spec.MemoryMaxCapacityBytes > 0 ? Format.Bytes(spec.MemoryMaxCapacityBytes) : null);

        var smDevices = spec.SmBios?.MemoryDevices.Where(d => d.Installed).ToList() ?? new List<SmBiosMemoryDevice>();
        if (smDevices.Count > 0)
        {
            var soldered = smDevices.Count(d => d.IsSoldered);
            summary.Add("Physical layout", soldered == smDevices.Count
                ? $"{smDevices.Count} × {Format.Bytes(smDevices[0].SizeBytes)} soldered package{(smDevices.Count == 1 ? "" : "s")} (on the motherboard)"
                : soldered > 0 ? $"{soldered} soldered + {smDevices.Count - soldered} socketed module(s)" : $"{smDevices.Count} socketed module(s)");
            var width = smDevices[0].DataWidth;
            if (width is > 0 and < 0xFFFF) summary.Add("Data width per device", $"{width}-bit{(width < 64 ? " channel (LPDDR uses narrow independent channels)" : "")}");
            summary.Add("ECC (error correction)", smDevices.Any(d => d.IsEcc) ? "Yes" : "No");
            var ranks = smDevices.Select(d => d.Rank).Where(r => r > 0).Distinct().ToList();
            if (ranks.Count == 1) summary.Add("Ranks per module", ranks[0] switch { 1 => "1 (single-rank)", 2 => "2 (dual-rank)", var r => r.ToString(CultureInfo.InvariantCulture) });
        }
        spec.MemorySections.Add(summary);
        spec.MemorySections.Add(BuildTimingsSection(spec, null));

        foreach (var m in spec.Modules)
        {
            var title = string.IsNullOrEmpty(m.Slot) ? "Module" : m.Slot;
            spec.MemorySections.Add(new SpecSection(title, Format.Bytes(m.CapacityBytes))
                .Add("Manufacturer", m.Manufacturer)
                .Add("Part number", m.PartNumber)
                .Add("Type", $"{m.TypeName} {m.FormFactor}".Trim())
                .Add("Rated speed", m.RatedMts > 0 ? $"{m.RatedMts} MT/s" : null)
                .Add("Configured speed", m.ConfiguredMts > 0 ? $"{m.ConfiguredMts} MT/s" : null)
                .Add("Voltage", m.ConfiguredVoltageMv > 0 ? $"{m.ConfiguredVoltageMv / 1000.0:0.00} V" : null)
                .Add("Bank", m.Bank)
                .Add("Serial", m.Serial));
        }
    }

    private static void BuildGpuSections(SystemSpec spec)
    {
        foreach (var g in spec.Gpus)
        {
            spec.GpuSections.Add(new SpecSection(g.Name, g.IsIntegrated ? "Integrated graphics" : "Discrete graphics")
                .Add("Vendor", g.Vendor)
                .Add("Video memory", g.VramBytes > 0 ? Format.Bytes(g.VramBytes) + (g.IsIntegrated ? " (carved out of system RAM)" : "") : null)
                .Add("Memory type", g.MemoryKind)
                .Add("Chip", g.ChipType == g.Name ? null : g.ChipType)
                .Add("Video BIOS", g.BiosVersion)
                .Add("Bus interface", g.IsIntegrated ? "On-die (shares the CPU's memory controller)"
                    : g.Link is { CurrentGen: > 0 } l ? $"{l.Current}{(l.SlotMaxGen > 0 ? $" (slot supports {l.Slot}, card supports {l.Device})" : "")}" : null)
                .Add("Driver version", g.DriverVersion)
                .Add("Driver date", g.DriverDate is null ? null : $"{Format.Date(g.DriverDate)}  ({Format.Age(g.DriverDate)})")
                .Add("Current mode", g.Resolution.Length > 0 ? $"{g.Resolution}{(g.RefreshHz > 1 ? $" @ {g.RefreshHz} Hz" : "")}" : null)
                .Add("Processor", g.Processor == g.Name ? null : g.Processor));
        }
        if (spec.Os.GpuSchedulingEnabled is { } hags && spec.GpuSections.Count > 0)
            spec.GpuSections[0].Add("Hardware-accelerated GPU scheduling", hags ? "On" : "Off");

        if (spec.Displays.Count > 0)
        {
            var displays = new SpecSection("Displays", $"{spec.Displays.Count} connected");
            foreach (var d in spec.Displays)
            {
                var details = new List<string>();
                if (d.DiagonalInches is { } inch) details.Add($"{inch:0.#}\"");
                if (d.Manufacturer.Length > 0) details.Add(d.Manufacturer);
                if (d.Year > 1990) details.Add($"made {d.Year}");
                displays.Add(d.Name, details.Count > 0 ? string.Join(" · ", details) : "Connected");
            }
            spec.GpuSections.Add(displays);
        }
    }

    private static void BuildStorageSections(SystemSpec spec)
    {
        foreach (var d in spec.Disks)
        {
            var kind = $"{d.BusType} {d.MediaType}".Trim();
            var section = new SpecSection(d.Name, $"{kind}{(kind.Length > 0 ? " · " : "")}{Format.Bytes(d.SizeBytes)}")
                .Add("Health (Windows)", d.Health)
                .Add("Role", d.IsBoot ? "System / boot drive" : null)
                .Add("Interface link", d.Link is { CurrentGen: > 0 } l ? $"{l.Current}{(l.SlotMaxGen > 0 ? $" · slot max {l.Slot} · drive max {l.Device}" : "")}" : null)
                .Add("Partition style", d.PartitionStyle.Length > 0 ? d.PartitionStyle + (d.PartitionStyle == "MBR" ? " (limited to 2 TB)" : "") : null)
                .Add("Life remaining", d.WearPercent is { } wear ? $"{Math.Max(0, 100 - wear)} %" : null)
                .Add("Temperature", d.TemperatureC is { } t ? $"{t} °C" + (d.TemperatureMaxC is { } tm ? $"  (rated max {tm} °C)" : "") : null)
                .Add("Power-on time", d.PowerOnHours is { } hours ? $"{hours:N0} hours  (~{hours / 24.0 / 365.25:0.0} years)" : null)
                .Add("Read errors", d.ReadErrors is { } re and > 0 ? re.ToString("N0", CultureInfo.InvariantCulture) : null)
                .Add("Firmware", d.Firmware)
                .Add("Serial", d.Serial);
            spec.StorageSections.Add(section);
        }

        if (spec.Volumes.Count > 0)
        {
            var volumes = new SpecSection("Volumes");
            foreach (var v in spec.Volumes)
            {
                var label = v.Label.Length > 0 ? $"{v.Name}  {v.Label}" : v.Name;
                volumes.Add(label, $"{Format.Bytes(v.FreeBytes)} free of {Format.Bytes(v.TotalBytes)}  ·  {v.Format}  ·  {v.FreeFraction:P0} free");
            }
            spec.StorageSections.Add(volumes);
        }
        if (spec.Os.TrimEnabled is { } trim)
            spec.StorageSections.Add(new SpecSection("Windows storage features").Add("TRIM for SSDs", trim ? "Enabled" : "Disabled (SSD performance will degrade over time)"));
    }

    /// <summary>
    /// RAM timings card. Built at startup as a placeholder and rebuilt once SPD data arrives from the
    /// sensor thread. Explains plainly when timings can't be read (soldered LPDDR has no SPD chip).
    /// </summary>
    public static SpecSection BuildTimingsSection(SystemSpec spec, IReadOnlyList<SpdModuleTimings>? spd)
    {
        var soldered = spec.SmBios?.MemoryDevices.Where(d => d.Installed).All(d => d.IsSoldered) == true
                       || spec.Modules.All(m => m.TypeName.StartsWith("LP", StringComparison.Ordinal));
        var running = spec.Modules.Select(m => m.ConfiguredMts).Where(v => v > 0).DefaultIfEmpty(0).Min();
        var partCl = spec.Modules.Select(m => SpdReader.ClFromPartNumber(m.PartNumber)).FirstOrDefault(c => c is not null);

        if (spd is { Count: > 0 })
        {
            // One table per module. SPD holds the module's JEDEC (default) timings; XMP/EXPO profiles and
            // BIOS overrides can run it tighter or faster, so the running values may differ.
            var section = new SpecSection("Timings", "JEDEC timings from each module's SPD chip · cycles (nanoseconds)");
            string T(int cycles, double tck) => cycles <= 0 ? "" : tck > 0 ? $"{cycles}  ({cycles * tck:0.##} ns)" : $"{cycles}";
            foreach (var m in spd)
            {
                var prefix = spd.Count > 1 ? $"0x{m.SmBusAddress:X2} · " : "";
                section.Add($"{prefix}Module", $"{m.ModuleMaker} {m.PartNumber}".Trim());
                section.Add($"{prefix}JEDEC speed", $"{m.Type}-{m.JedecMts}" + (m.TckNs > 0 ? $" (tCK {m.TckNs:0.###} ns)" : ""));
                section.Add($"{prefix}Primary timings", $"CL{m.Cl}-{m.Trcd}-{m.Trp}-{m.Tras}  (tCL-tRCD-tRP-tRAS)");
                section.Add($"{prefix}CAS latency (tCL)", T(m.Cl, m.TckNs));
                section.Add($"{prefix}RAS to CAS (tRCD)", T(m.Trcd, m.TckNs));
                section.Add($"{prefix}Row precharge (tRP)", T(m.Trp, m.TckNs));
                section.Add($"{prefix}Row active (tRAS)", T(m.Tras, m.TckNs));
                section.Add($"{prefix}Row cycle (tRC)", T(m.Trc, m.TckNs));
                section.Add($"{prefix}Refresh cycle (tRFC)", T(m.Trfc, m.TckNs));
                section.Add($"{prefix}Write recovery (tWR)", T(m.Twr, m.TckNs));
                section.Add($"{prefix}Four-activate window (tFAW)", T(m.Tfaw, m.TckNs));
                if (m.TrrdS > 0) section.Add($"{prefix}Activate to activate (tRRD_S / _L)", $"{m.TrrdS} / {m.TrrdL}");
                section.Add($"{prefix}CAS to CAS, same group (tCCD_L)", T(m.TccdL, m.TckNs));
                section.Add($"{prefix}First-word latency", m.FirstWordNs > 0 ? $"{m.FirstWordNs:0.0} ns  (lower is snappier)" : null);
                if (m.SupportedCl.Count > 0) section.Add($"{prefix}Supported CAS latencies", string.Join(", ", m.SupportedCl.Select(c => $"CL{c}")));
                section.Add($"{prefix}DRAM chips by", m.DramMaker.Length > 0 && m.DramMaker != m.ModuleMaker ? m.DramMaker : null);
                section.Add($"{prefix}Manufactured", m.Manufactured is { } d ? d.ToString("MMMM yyyy", CultureInfo.InvariantCulture) : null);
                section.Add($"{prefix}Module temperature", m.TemperatureC is { } t ? Format.Temperature(t) : null);
            }
            if (partCl is { } xmpCl && running > 0)
                section.Add("XMP/EXPO rating (part number)", $"CL{xmpCl} · first-word latency {xmpCl * 2000.0 / running:0.0} ns at {running} MT/s");
            if (running > 0 && spd.Max(m => m.JedecMts) is var jedec && jedec > 0 && running != jedec)
                section.Add("Running speed", $"{running} MT/s (JEDEC default is {jedec}; XMP/EXPO or BIOS settings change the timings too)");
            return section;
        }

        var fallback = new SpecSection("Timings");
        if (soldered)
        {
            fallback.Add("CAS latency & timings", "Not readable on this system");
            fallback.Add("Why", "Soldered LPDDR memory has no SPD chip. Its timings are programmed into the CPU's memory controller by the firmware and aren't exposed to Windows.");
        }
        else
        {
            fallback.Add("CAS latency & timings", "SPD not accessible");
            fallback.Add("Why", "Exact timings live in each module's SPD chip, which can only be read through a kernel driver. CoreScope deliberately installs no drivers, so it shows the rated CAS latency from the part number instead.");
        }
        if (partCl is { } cl && running > 0)
            fallback.Add("Rated CAS latency (part number)", $"CL{cl} · first-word latency ≈ {cl * 2000.0 / running:0.0} ns at {running} MT/s");
        return fallback;
    }

    /// <summary>
    /// Drive health from the drive's own SMART / NVMe health log (via LibreHardwareMonitor sensors) plus Windows'
    /// reliability counters: wear, spare blocks, lifetime writes, power-on time, errors and a remaining-life estimate.
    /// </summary>
    public static List<SpecSection> BuildStorageLifetimeSections(IReadOnlyList<SensorReading> readings, SystemSpec? spec = null)
    {
        var sections = new List<SpecSection>();
        var storage = readings.Where(r => r.Kind == "Storage" && r.Value.HasValue).ToList();
        foreach (var group in storage.GroupBy(r => r.GroupName))
        {
            double? Find(params string[] names) => group.FirstOrDefault(r => names.Any(n => r.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))?.Value;
            var disk = spec?.Disks.FirstOrDefault(d => d.Name.Contains(group.Key, StringComparison.OrdinalIgnoreCase) || group.Key.Contains(d.Name, StringComparison.OrdinalIgnoreCase));

            double? used = Find("Percentage Used") ?? (Find("Remaining Life", "Life Remaining") is { } left ? 100 - left : (double?)null) ?? disk?.WearPercent;
            double? spare = Find("Available Spare");
            double? spareThreshold = Find("Available Spare Threshold");
            double? writtenGb = Find("Data Written", "Total Bytes Written", "Host Writes");
            double? readGb = Find("Data Read", "Total Bytes Read", "Host Reads");
            double? hours = Find("Power On Hours", "Power-On Hours") ?? (disk?.PowerOnHours is { } poh ? poh : (double?)null);
            double? cycles = Find("Power On Count", "Power Cycle Count", "Power Cycles");
            double? warnTemp = Find("Warning Temperature");
            double? critTemp = Find("Critical Temperature");

            var section = new SpecSection($"Health · {group.Key}", "From the drive's own SMART / NVMe health log");
            if (used is { } u)
            {
                var health = Math.Clamp(100 - u, 0, 100);
                section.Add("Health", $"{health:0}% left" + (health >= 90 ? " · excellent" : health >= 70 ? " · good" : health >= 30 ? " · worn, keep backups" : " · near end of rated life — replace soon"));
            }
            if (spare is { } sp) section.Add("Spare blocks", $"{sp:0}% available" + (spareThreshold is { } th ? $" (warning below {th:0}%)" : ""));
            if (writtenGb is { } w) section.Add("Data written (lifetime)", w >= 1000 ? $"{w / 1000:0.00} TB" : $"{w:0} GB");
            if (readGb is { } r) section.Add("Data read (lifetime)", r >= 1000 ? $"{r / 1000:0.00} TB" : $"{r:0} GB");
            if (hours is { } h) section.Add("Powered on", $"{h:N0} hours (≈ {h / 24:N0} days)");
            if (cycles is { } c) section.Add("Power cycles", $"{c:N0}");
            if (disk?.ReadErrors is { } errors) section.Add("Read errors (Windows counter)", errors == 0 ? "0 · none" : $"{errors:N0} · back up your data");
            if (warnTemp is { } wt) section.Add("Slows down at", Format.Temperature(wt) + (critTemp is { } ct ? $" · critical at {Format.Temperature(ct)}" : ""));

            // Remaining-life estimate: wear so far vs powered-on time so far, extrapolated at the same usage.
            if (used is { } usedPct && hours is { } hrs && hrs > 100)
            {
                if (usedPct < 1)
                    section.Add("Estimated life left", "Under 1% of rated endurance used — decades at this pace");
                else
                {
                    var hoursLeft = hrs * (100 - usedPct) / usedPct;
                    var yearsAt8h = hoursLeft / (8 * 365.0);
                    section.Add("Estimated life left", $"≈ {hoursLeft:N0} more powered-on hours (about {yearsAt8h:0} years at 8 h/day), at your current pace");
                }
            }
            else if (writtenGb is { } wgb && disk?.SizeBytes is { } size && size > 0)
            {
                // No wear counter: compare with a typical consumer rating of ~600 TB written per TB of capacity (TLC).
                var typicalTbw = size / 1e12 * 600;
                section.Add("Endurance (estimate)", $"{wgb / 1000:0.0} TB of a typical ~{typicalTbw:0} TB rating used ({wgb / 1000 / typicalTbw * 100:0.0}%)");
            }

            if (section.Rows.Count > 0) sections.Add(section);
        }
        return sections;
    }

    private static void BuildBoardSections(SystemSpec spec)
    {
        var b = spec.Board;
        spec.BoardSections.Add(new SpecSection("System")
            .Add("Manufacturer", b.SystemVendor)
            .Add("Model", b.SystemModel)
            .Add("Product name", b.SystemVersion)
            .Add("Family", b.SystemFamily)
            .Add("SKU", b.Sku));
        spec.BoardSections.Add(new SpecSection("Motherboard")
            .Add("Manufacturer", b.BoardVendor)
            .Add("Model", b.BoardModel)
            .Add("Revision", b.BoardVersion));
        spec.BoardSections.Add(new SpecSection("Firmware")
            .Add("Type", b.FirmwareType)
            .Add("Vendor", b.BiosVendor)
            .Add("Version", b.BiosVersion)
            .Add("Release date", b.BiosDate is null ? null : $"{Format.Date(b.BiosDate)}  ({Format.Age(b.BiosDate)})")
            .Add("Secure Boot", Format.YesNo(b.SecureBoot, "On", "Off")));
        var io = new SpecSection("I/O & expansion");
        var usb = spec.Devices.FirstOrDefault(c => c.Title.StartsWith("USB", StringComparison.Ordinal))?.Devices
                  .Where(d => Regex.IsMatch(d.Name, "host controller|xhci|usb4|thunderbolt|router", RegexOptions.IgnoreCase)).ToList() ?? new();
        foreach (var c in usb) io.Add("USB controller", c.Name);
        io.Add("USB4 / Thunderbolt", usb.Any(d => Regex.IsMatch(d.Name, "usb4|thunderbolt", RegexOptions.IgnoreCase))
            ? "Yes (40 Gbps: eGPU, fast external SSDs, docks)"
            : "Not detected");
        foreach (var slot in spec.SmBios?.Slots ?? new List<SmBiosSlot>())
            io.Add(slot.Designation.Length > 0 ? slot.Designation : "Slot", $"{slot.Kind} · {slot.Usage}");
        var audio = spec.Devices.FirstOrDefault(c => c.Title == "Audio")?.Devices.Where(d => d.ClassName == "MEDIA").Select(d => d.Name).Distinct().ToList();
        if (audio is { Count: > 0 }) io.Add("Audio", string.Join(", ", audio));
        if (io.Rows.Count > 0) spec.BoardSections.Add(io);

        spec.BoardSections.Add(new SpecSection("Security chip")
            .Add("TPM", b.TpmPresent is null ? "Unknown (needs administrator)" : b.TpmPresent.Value ? $"Present · version {b.TpmVersion}" : "Not detected")
            .Add("TPM manufacturer", b.TpmManufacturer));
    }

    private static void BuildSystemSections(SystemSpec spec)
    {
        var os = spec.Os;
        var uptime = os.LastBoot is { } boot ? Format.Duration(DateTime.Now - boot) : null;
        spec.SystemSections.Add(new SpecSection("Windows")
            .Add("Edition", os.Name)
            .Add("Version", os.DisplayVersion)
            .Add("Build", os.Build)
            .Add("Architecture", os.Architecture)
            .Add("Computer name", os.ComputerName)
            .Add("Installed", os.InstallDate is null ? null : $"{Format.Date(os.InstallDate)}  ({Format.Age(os.InstallDate)})")
            .Add("Last boot", os.LastBoot is null ? null : $"{os.LastBoot:MMM d, HH:mm}  (up {uptime})")
            .Add("Power plan", os.PowerPlan)
            .Add("Hypervisor running", Format.YesNo(os.HypervisorPresent)));

        if (spec.Battery is { } bat)
        {
            spec.SystemSections.Add(new SpecSection("Battery", bat.Name)
                .Add("Manufacturer", bat.Manufacturer)
                .Add("Chemistry", bat.Chemistry)
                .Add("Design capacity", bat.DesignMwh > 0 ? $"{bat.DesignMwh / 1000.0:0.0} Wh" : null)
                .Add("Full-charge capacity", bat.FullChargeMwh > 0 ? $"{bat.FullChargeMwh / 1000.0:0.0} Wh" : null)
                .Add("Wear", bat.WearPercent is { } wear ? $"{wear:0.0} %" : null)
                .Add("Charge cycles", bat.Cycles?.ToString(CultureInfo.InvariantCulture))
                .Add("Charge (at startup)", bat.ChargePercent is { } c ? $"{c} %" : null));
        }

        spec.SystemSections.Add(new SpecSection("CoreScope")
            .Add("Running as administrator", Format.YesNo(os.IsAdmin))
            .Add("Log file", Log.Path));
    }
}
