using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CoreScope.Core.Hardware;

public enum Upgradeability { Upgradeable = 0, Partial = 1, CheckManual = 2, NotUpgradeable = 3 }

/// <summary>A link that opens in the default browser. Nothing is sent anywhere until the user clicks.</summary>
public sealed class UpgradeLink
{
    public UpgradeLink(string label, string url)
    {
        Label = label;
        Url = url;
        OpenCommand = new RelayCommand(() => Shell.Open(url));
    }

    public string Label { get; }
    public string Url { get; }
    public RelayCommand OpenCommand { get; }
}

/// <summary>One component's upgrade verdict: what you have, what to buy, caveats and lookup links.</summary>
public sealed class UpgradeItem
{
    public UpgradeItem(string component, string glyph)
    {
        Component = component;
        Glyph = glyph;
    }

    public string Component { get; }
    public string Glyph { get; }
    public Upgradeability Status { get; set; } = Upgradeability.CheckManual;
    public string StatusText { get; set; } = "Check manual";
    public string Summary { get; set; } = "";
    public List<SpecRow> Current { get; } = new();
    public List<SpecRow> Buy { get; } = new();
    public List<string> Notes { get; } = new();
    public List<UpgradeLink> Links { get; } = new();
    public string StatusName => Status.ToString();

    public UpgradeItem Have(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) Current.Add(new SpecRow(label, value.Trim()));
        return this;
    }

    public UpgradeItem Need(string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) Buy.Add(new SpecRow(label, value.Trim()));
        return this;
    }

    public UpgradeItem Set(Upgradeability status, string statusText, string summary)
    {
        Status = status;
        StatusText = statusText;
        Summary = summary;
        return this;
    }
}

public sealed class UpgradeReport
{
    public string MachineName = "";
    public string MachineDetail = "";
    public bool IsLaptop;
    public List<UpgradeItem> Items = new();
    public List<UpgradeLink> Links = new();
    public string ShoppingList = "";
}

public static class Shell
{
    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Opening {url}", ex);
        }
    }
}

/// <summary>
/// Builds the upgrade guide. Every verdict is derived from detected facts; anything software can't
/// see (free M.2 slots, SSD length, panel connector) is stated as such and backed by a lookup link.
/// </summary>
public static class UpgradeAdvisor
{
    private const string GlyphMemory = "", GlyphStorage = "", GlyphBattery = "",
                         GlyphDisplay = "", GlyphWifi = "", GlyphCpu = "", GlyphGpu = "";

    public static UpgradeReport Build(SystemSpec spec)
    {
        var smbios = spec.SmBios ?? SmBios.Read();
        if (smbios is null) Log.Info("SMBIOS table unavailable — upgrade advisor falls back to WMI");
        else Log.Info($"SMBIOS {smbios.Version}: {smbios.Structures.Count} structures, {smbios.MemoryDevices.Count} memory device records, {smbios.Slots.Count} slot records");

        int chassis = smbios?.ChassisType ?? 0;
        bool isLaptop = SmBios.IsPortableChassis(chassis) || (chassis == 0 && spec.Battery is not null);
        var b = spec.Board;

        var report = new UpgradeReport { IsLaptop = isLaptop };
        report.MachineName = MachineName(b);
        report.MachineDetail = string.Join(" · ", new[]
        {
            SmBios.ChassisName(chassis),
            b.SystemVendor.Equals("LENOVO", StringComparison.OrdinalIgnoreCase) && b.SystemModel.Length > 0 ? $"Machine type {b.SystemModel}" : null,
            b.BoardModel.Length > 0 ? $"Board {b.BoardVendor} {b.BoardModel}".Trim() : null,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var query = SearchQuery(b);
        AddMachineLinks(report.Links, b, query);

        void Safe(string name, Func<UpgradeItem?> build)
        {
            try
            {
                if (build() is { } item) report.Items.Add(item);
            }
            catch (Exception ex)
            {
                Log.Error($"Upgrade advisor: {name}", ex);
            }
        }

        Safe("memory", () => Memory(spec, smbios, isLaptop, query));
        Safe("storage", () => Storage(spec, smbios, isLaptop, query));
        Safe("battery", () => Battery(spec, smbios, query));
        Safe("display", () => Display(isLaptop, query));
        Safe("wifi", () => Wifi(isLaptop, spec.Cpu.Vendor));
        Safe("cpu", () => Processor(spec, smbios, isLaptop));
        Safe("gpu", () => Graphics(spec, isLaptop));
        Safe("ports", () => Ports(spec, isLaptop));

        report.ShoppingList = BuildShoppingList(report);
        return report;
    }

    // ───────────────────────────── Identity & links ─────────────────────────────

    private static bool IsPlaceholder(string value) =>
        string.IsNullOrWhiteSpace(value)
        || Regex.IsMatch(value, @"To be filled|System Product Name|System manufacturer|Default string|O\.E\.M\.|^None$|Not Applicable", RegexOptions.IgnoreCase);

    private static string MachineName(BoardSpec b)
    {
        var vendor = IsPlaceholder(b.SystemVendor) ? "" : Title(b.SystemVendor);
        if (!IsPlaceholder(b.SystemVersion) && b.SystemVendor.Equals("LENOVO", StringComparison.OrdinalIgnoreCase))
            return $"{vendor} {b.SystemVersion}".Trim();
        if (!IsPlaceholder(b.SystemModel)) return $"{vendor} {b.SystemModel}".Trim();
        return $"Custom PC · {b.BoardVendor} {b.BoardModel}".Trim();
    }

    private static string Title(string vendor) => vendor.ToUpperInvariant() switch
    {
        "LENOVO" => "Lenovo", "HP" or "HEWLETT-PACKARD" => "HP", "DELL INC." => "Dell", "ASUSTEK COMPUTER INC." => "ASUS",
        "MICRO-STAR INTERNATIONAL CO., LTD." => "MSI", "ACER" => "Acer", "MICROSOFT CORPORATION" => "Microsoft",
        _ => vendor,
    };

    /// <summary>The most specific model string to search vendor databases with.</summary>
    private static string SearchQuery(BoardSpec b)
    {
        if (b.SystemVendor.Equals("LENOVO", StringComparison.OrdinalIgnoreCase) && !IsPlaceholder(b.SystemVersion))
            return b.SystemVersion;
        if (!IsPlaceholder(b.SystemModel)) return $"{Title(b.SystemVendor)} {b.SystemModel}".Trim();
        return $"{b.BoardVendor} {b.BoardModel}".Trim();
    }

    private static string Q(string text) => Uri.EscapeDataString(text);

    private static void AddMachineLinks(List<UpgradeLink> links, BoardSpec b, string query)
    {
        var vendor = b.SystemVendor.ToUpperInvariant();
        if (vendor.Contains("LENOVO"))
            links.Add(new UpgradeLink("Lenovo PSREF spec sheet", $"https://psref.lenovo.com/search?kw={Q(query)}"));
        else if (vendor.Contains("DELL"))
            links.Add(new UpgradeLink("Dell support & manuals", $"https://www.dell.com/support/search/en-us#q={Q(query)}"));
        else if (vendor.Contains("HP") || vendor.Contains("HEWLETT"))
            links.Add(new UpgradeLink("HP support & specs", $"https://support.hp.com/us-en/search?q={Q(query)}"));
        else if (vendor.Contains("ASUS"))
            links.Add(new UpgradeLink("ASUS product support", $"https://www.asus.com/searchresult?searchType=support&searchKey={Q(query)}"));

        links.Add(new UpgradeLink("Service / maintenance manual", $"https://www.google.com/search?q={Q(query + " hardware maintenance manual")}"));
        links.Add(new UpgradeLink("Crucial upgrade advisor", $"https://www.crucial.com/search?q={Q(query)}"));
    }

    // ───────────────────────────── Memory ─────────────────────────────

    private static string Pins(string type, string formFactor)
    {
        bool sodimm = formFactor == "SO-DIMM";
        return type switch
        {
            "DDR5" => sodimm ? "262-pin" : "288-pin",
            "DDR4" => sodimm ? "260-pin" : "288-pin",
            "DDR3" => sodimm ? "204-pin" : "240-pin",
            "DDR2" => sodimm ? "200-pin" : "240-pin",
            _ => "",
        };
    }

    private static string PcRating(string type, int mts) => type switch
    {
        "DDR5" => $"PC5-{mts * 8}",
        "DDR4" => $"PC4-{mts * 8}",
        "DDR3" => $"PC3-{mts * 8}",
        _ => "",
    };

    private static string Voltage(string type, int configuredMv) => type switch
    {
        "DDR5" => "1.1 V (JEDEC standard)",
        "DDR4" => "1.2 V (JEDEC standard)",
        "DDR3" => configuredMv is > 0 and <= 1350 ? "1.35 V — must be DDR3L (low-voltage)" : "1.5 V (or DDR3L 1.35 V, which is backward compatible)",
        _ => "",
    };

    private static UpgradeItem Memory(SystemSpec spec, SmBios? smbios, bool isLaptop, string query)
    {
        var item = new UpgradeItem("Memory (RAM)", GlyphMemory);
        var devices = smbios?.MemoryDevices ?? new List<SmBiosMemoryDevice>();

        // Fallback to WMI (installed modules only, so empty slots are invisible).
        if (devices.Count == 0)
        {
            devices = spec.Modules.Select(m => new SmBiosMemoryDevice(m.Slot, m.Bank, true, m.CapacityBytes,
                m.FormFactor == "Soldered" ? "Row of chips" : m.FormFactor, m.TypeName, m.RatedMts, m.ConfiguredMts,
                m.Manufacturer, m.PartNumber, 0, m.ConfiguredVoltageMv)).ToList();
            item.Notes.Add("Firmware table unavailable — empty slots can't be listed. Check the spec sheet.");
        }

        var installed = devices.Where(d => d.Installed).ToList();
        var soldered = installed.Where(d => d.IsSoldered).ToList();
        var moduleSlots = devices.Where(d => !d.IsSoldered && d.FormFactor is "DIMM" or "SO-DIMM" or "CAMM").ToList();
        var freeSlots = moduleSlots.Where(d => !d.Installed).ToList();
        var socketed = moduleSlots.Where(d => d.Installed).ToList();

        string type = installed.Select(d => d.Type).FirstOrDefault(t => t.Length > 0) ?? "";
        string formFactor = moduleSlots.Select(d => d.FormFactor).FirstOrDefault() ?? (isLaptop ? "SO-DIMM" : "DIMM");
        int runningMts = installed.Select(d => d.ConfiguredSpeedMts).Where(s => s > 0).DefaultIfEmpty(0).Min();
        int ratedMts = installed.Select(d => d.SpeedMts).Where(s => s > 0).DefaultIfEmpty(0).Max();
        ulong total = installed.Aggregate(0UL, (sum, d) => sum + d.SizeBytes);
        var (_, maxCapacity) = smbios?.MemoryArray ?? (0, 0);

        // ── What you have ──
        item.Have("Installed", $"{Format.Bytes(total)} {type}".Trim() + (runningMts > 0 ? $" running at {runningMts} MT/s" : ""));
        foreach (var d in devices.Where(d => d.Installed || moduleSlots.Contains(d)))
        {
            var label = d.Locator.Length > 0 ? d.Locator : "Memory";
            if (!d.Installed)
            {
                item.Have(label, $"EMPTY {d.FormFactor} slot");
                continue;
            }
            var details = new List<string> { $"{Format.Bytes(d.SizeBytes)} {d.Type}".Trim() };
            details.Add(d.IsSoldered ? "soldered on board" : d.FormFactor);
            if (d.Manufacturer.Length > 0 || d.PartNumber.Length > 0) details.Add($"{d.Manufacturer} {d.PartNumber}".Trim());
            if (d.SpeedMts > 0) details.Add($"rated {d.SpeedMts} MT/s");
            item.Have(label, string.Join(" · ", details));
        }
        if (maxCapacity > 0) item.Have("Max (firmware-reported)", Format.Bytes(maxCapacity));

        // ── Verdict ──
        if (installed.Count > 0 && moduleSlots.Count == 0 && soldered.Count < installed.Count)
        {
            // Firmware didn't say "chips" or "DIMM" (form factor Unknown/Other is common): don't guess "soldered".
            item.Set(Upgradeability.CheckManual, "Check manual",
                $"{Format.Bytes(total)} {type} installed, but the firmware doesn't say whether it's in slots or soldered. Check the spec sheet before buying.".Replace("  ", " "));
            item.Links.Add(new UpgradeLink("Spec sheet search", $"https://www.google.com/search?q={Q(query + " memory slots specifications")}"));
            return item;
        }

        if (installed.Count > 0 && moduleSlots.Count == 0)
        {
            item.Set(Upgradeability.NotUpgradeable, "Soldered",
                $"All {Format.Bytes(total)} of memory is soldered to the motherboard{(type.StartsWith("LPDDR", StringComparison.Ordinal) ? $" ({type} is never socketed)" : "")}. It can't be upgraded.");
            item.Notes.Add("If you need more RAM, the only options are a different configuration of this model or a new machine.");
            item.Links.Add(new UpgradeLink("Confirm on spec sheet", $"https://www.google.com/search?q={Q(query + " memory soldered slots specifications")}"));
            return item;
        }

        if (soldered.Count > 0)
        {
            item.Set(Upgradeability.Partial, "Partly soldered",
                $"{Format.Bytes(soldered.Aggregate(0UL, (s, d) => s + d.SizeBytes))} is soldered on board, plus {moduleSlots.Count} {formFactor} slot{(moduleSlots.Count == 1 ? "" : "s")}" +
                (freeSlots.Count > 0 ? $" ({freeSlots.Count} empty)." : $" (in use — replace the module to upgrade)."));
        }
        else if (freeSlots.Count > 0)
        {
            item.Set(Upgradeability.Upgradeable, "Upgradeable", $"{freeSlots.Count} of {moduleSlots.Count} {formFactor} slots are empty — you can add memory.");
        }
        else if (moduleSlots.Count > 0)
        {
            item.Set(Upgradeability.Upgradeable, "Upgradeable", $"All {moduleSlots.Count} {formFactor} slots are in use — upgrade by replacing modules with larger ones.");
        }

        // ── What to buy ──
        if (type.Length > 0)
        {
            var pins = Pins(type, formFactor);
            item.Need("Type", $"{type} {formFactor}{(pins.Length > 0 ? $" ({pins})" : "")}");
        }
        if (runningMts > 0 || ratedMts > 0)
        {
            int target = Math.Max(runningMts, ratedMts);
            var pc = PcRating(type, target);
            item.Need("Speed", $"{type}-{target}{(pc.Length > 0 ? $" ({pc})" : "")}. Faster modules also work but will run at {(runningMts > 0 ? runningMts : target)} MT/s.");
        }
        item.Need("Voltage", Voltage(type, installed.Select(d => d.ConfiguredVoltageMv).FirstOrDefault()));

        if (soldered.Count > 0 && moduleSlots.Count > 0)
        {
            var onboard = soldered.Aggregate(0UL, (s, d) => s + d.SizeBytes);
            item.Need("Capacity", $"{Format.Bytes(onboard)} matches the soldered memory for full dual-channel; larger modules work too (asymmetric \"flex\" mode).");
        }
        else if (socketed.Count > 0)
        {
            var each = socketed[0].SizeBytes;
            item.Need("Capacity", freeSlots.Count > 0
                ? $"Add {Format.Bytes(each)} to match the existing module for dual-channel, or replace all with a matched kit."
                : $"Buy a matched kit ({moduleSlots.Count} × same size). Mixing kits can work but isn't guaranteed.");
        }
        if (moduleSlots.Count > 0)
        {
            var (perModule, note) = LargestModule(type, formFactor);
            ulong onboard = soldered.Aggregate(0UL, (s, d) => s + d.SizeBytes);
            ulong physical = perModule > 0 ? onboard + perModule * (ulong)moduleSlots.Count : 0;
            var lines = new List<string>();
            if (perModule > 0) lines.Add($"{Format.Bytes(perModule)} per {formFactor} module is the largest {type} size sold{note}");
            if (physical > 0) lines.Add($"{moduleSlots.Count} slot{(moduleSlots.Count == 1 ? "" : "s")}{(onboard > 0 ? $" + {Format.Bytes(onboard)} soldered" : "")} → up to {Format.Bytes(physical)}");
            if (maxCapacity > 0) lines.Add($"firmware reports {Format.Bytes(maxCapacity)} (often a conservative figure written years before larger modules existed)");
            item.Need("Maximum capacity", string.Join("; ", lines) + ".");
        }
        else if (maxCapacity > 0)
        {
            item.Need("Maximum", $"{Format.Bytes(maxCapacity)} as reported by firmware — often conservative; check the spec sheet.");
        }

        if (isLaptop) item.Notes.Add("Laptops usually ignore XMP/EXPO profiles — memory runs at its standard (JEDEC) speed.");
        if (socketed.Any(d => d.PartNumber.Length > 0))
            item.Notes.Add($"For the safest match, search the existing part number: {socketed.First(d => d.PartNumber.Length > 0).PartNumber}.");
        item.Links.Add(new UpgradeLink("Crucial compatible memory", $"https://www.crucial.com/search?q={Q(query)}"));
        return item;
    }

    /// <summary>Largest consumer module per type/form factor on the market (late 2026).</summary>
    private static (ulong Bytes, string Note) LargestModule(string type, string formFactor)
    {
        const ulong gb = 1024UL * 1024 * 1024;
        bool sodimm = formFactor == "SO-DIMM";
        return type switch
        {
            "DDR5" => sodimm ? (64 * gb, " (48 GB is the most widely available; 64 GB needs a recent BIOS)") : (64 * gb, ""),
            "DDR4" => (32 * gb, ""),
            "DDR3" => (sodimm ? 8 * gb : 8 * gb, " (16 GB DDR3 modules exist but most platforms reject them)"),
            _ => (0, ""),
        };
    }

    /// <summary>Practical sequential throughput of an NVMe link (protocol overhead included).</summary>
    private static string LinkThroughput(int gen, int width)
    {
        double perLane = gen switch { 1 => 0.25, 2 => 0.5, 3 => 0.985, 4 => 1.97, 5 => 3.94, _ => 0 };
        double gbps = perLane * Math.Max(width, 1) * 0.88;
        return gbps > 0 ? $"≈ {gbps:0.#} GB/s" : "";
    }

    // ───────────────────────────── Storage ─────────────────────────────

    private static string Normalize(string s) => Regex.Replace(s.ToUpperInvariant(), @"[^A-Z0-9]", "");

    private static UpgradeItem Storage(SystemSpec spec, SmBios? smbios, bool isLaptop, string query)
    {
        var item = new UpgradeItem("Storage (SSD)", GlyphStorage);
        var links = DeviceProbe.GetDiskLinks();
        var bitlocker = spec.Os.IsAdmin ? DeviceProbe.GetBitLockerStatus() : null; // admin-only namespace
        var internalDisks = spec.Disks.Where(d => d.BusType is not ("USB" or "Virtual" or "Storage Spaces")).ToList();

        PcieLink? primaryLink = null;
        foreach (var d in internalDisks)
        {
            var link = links.FirstOrDefault(l => Normalize(l.Model).Contains(Normalize(d.Name)) || Normalize(d.Name).Contains(Normalize(l.Model)))?.Link;
            primaryLink ??= link;
            var details = new List<string> { Format.Bytes(d.SizeBytes), $"{d.BusType} {d.MediaType}".Trim() };
            if (link is { CurrentGen: > 0 }) details.Add($"running at {link.Current}");
            if (link is { DeviceMaxGen: > 0 } && link.Device != link.Current) details.Add($"drive max {link.Device}");
            if (d.WearPercent is { } wear) details.Add($"{100 - wear}% life left");
            item.Have(d.Name, string.Join(" · ", details));
        }
        if (primaryLink is { SlotMaxGen: > 0 }) item.Have("Slot supports", primaryLink.Slot);

        var used = spec.Volumes.Sum(v => v.TotalBytes - v.FreeBytes);
        if (used > 0) item.Have("Space used", $"{Format.Bytes(used)} across {spec.Volumes.Count} volume(s)");

        var m2Slots = smbios?.Slots.Where(s => s.Kind.StartsWith("M.2", StringComparison.Ordinal) && !s.Kind.Contains("Wi-Fi", StringComparison.Ordinal)).ToList() ?? new();
        var mKey = m2Slots.Where(s => s.Kind.Contains("Key M", StringComparison.Ordinal) || s.Kind == "M.2").ToList();
        var bKey = m2Slots.Where(s => s.Kind.Contains("Key B", StringComparison.Ordinal)).ToList();
        int nvmeDrives = internalDisks.Count(d => d.BusType == "NVMe");
        // Many OEM firmwares mark every slot "Empty" even with a drive installed — detect that and don't trust it.
        bool usageUnreliable = nvmeDrives > 0 && mKey.Count > 0 && mKey.All(s => s.Usage != "In use");
        foreach (var slot in m2Slots)
        {
            var label = slot.Designation.Length > 0 ? slot.Designation : slot.Kind;
            item.Have(label, usageUnreliable ? $"{slot.Kind} (listed by firmware)" : $"{slot.Kind} · {slot.Usage} (firmware-reported)");
        }

        var boot = internalDisks.FirstOrDefault();
        if (boot is null)
        {
            item.Set(Upgradeability.CheckManual, "Unknown", "No internal drive could be identified.");
            return item;
        }

        if (boot.BusType is "SD" or "MMC" or "UFS")
        {
            item.Set(Upgradeability.NotUpgradeable, "Soldered", $"Storage is {boot.BusType switch { "UFS" => "UFS", _ => "eMMC" }} flash soldered to the motherboard — it can't be replaced.");
            item.Notes.Add("Expand storage with a microSD card or a USB-C external SSD instead.");
            return item;
        }

        if (boot.BusType == "NVMe")
        {
            item.Set(Upgradeability.Upgradeable, "Replaceable", "Your SSD is an M.2 NVMe drive — it can be swapped for a larger or faster one.");
            item.Need("Type", "M.2 NVMe SSD (PCIe, M-key)");
            if (primaryLink is { SlotMaxGen: > 0 } known)
            {
                item.Need("PCIe generation", $"Best value: a PCIe {known.SlotMaxGen}.0 drive, the slot's maximum ({known.Slot}). " +
                                             $"PCIe {known.SlotMaxGen + 1}.0 drives fit and work, but only at PCIe {known.SlotMaxGen}.0 speed.");
            }
            else if (primaryLink is { CurrentGen: > 0 } current)
            {
                item.Need("PCIe generation", $"Your current drive runs at {current.Current}" +
                    (current.DeviceMaxGen > current.CurrentGen || current.DeviceMaxWidth > current.CurrentWidth ? $" even though it supports {current.Device}, so the slot is the limit" : "") +
                    $". A PCIe {current.CurrentGen}.0 drive is the best value; faster drives work but will be held to the same link.");
            }
            item.Need("Length", "2280, 2242 or 2230 — not detectable by software. Check the spec sheet or measure the current drive (22 mm wide × 30/42/80 mm long).");
            item.Need("Thickness", isLaptop ? "Single-sided drives are safest in thin laptops." : "Any; use the motherboard's M.2 heatsink if it has one.");
        }
        else if (boot.BusType is "SATA" or "ATA")
        {
            item.Set(Upgradeability.Upgradeable, "Replaceable", boot.MediaType == "HDD"
                ? "Your system drive is a SATA hard disk — replacing it with an SSD is the single biggest speed upgrade."
                : "Your system drive is a SATA SSD — it can be replaced.");
            item.Need("Type", isLaptop ? "2.5-inch SATA III SSD, 7 mm thick (or M.2 SATA if the drive is an M.2 stick)" : "2.5-inch SATA III SSD — or an M.2 NVMe SSD if the board has an M.2 slot");
        }
        else
        {
            item.Set(Upgradeability.CheckManual, "Check manual", $"Drive interface reported as '{boot.BusType}'.");
        }

        // Maximum capacity: firmware/partitioning limit first, then the physical (form factor) limit.
        var firmware = spec.Board.FirmwareType;
        string capacityLimit;
        if (firmware == "Legacy BIOS")
            capacityLimit = "Legacy BIOS mode can only boot from the first 2 TB (MBR). A larger drive works as a data drive; switch the firmware to UEFI to boot from more.";
        else if (boot.PartitionStyle == "MBR")
            capacityLimit = "Your boot drive uses MBR partitioning, which stops at 2 TB. Your firmware is UEFI, so convert the new drive to GPT (Windows 'mbr2gpt' tool or a clean install) and there's no limit.";
        else
            capacityLimit = "No firmware limit: UEFI + GPT handles drives far larger than any SSD sold.";
        item.Need("Max capacity (firmware)", capacityLimit);
        item.Need("Max capacity (size)", boot.BusType == "NVMe"
            ? "Set by the drive's length: 2230 and 2242 up to 2 TB (a few 4 TB 2230 models exist) · 2280 single-sided up to 4 TB · 2280 double-sided up to 8 TB, but often too thick for thin laptops."
            : "2.5-inch SATA SSDs go up to 8 TB.");
        if (primaryLink is { CurrentGen: > 0 } bootLink)
        {
            var slotGen = bootLink.SlotMaxGen > 0 ? bootLink.SlotMaxGen : bootLink.CurrentGen;
            var slotWidth = bootLink.SlotMaxWidth > 0 ? bootLink.SlotMaxWidth : bootLink.CurrentWidth;
            item.Need("Max speed (slot)", $"PCIe {slotGen}.0 x{slotWidth} → {LinkThroughput(slotGen, slotWidth)} sequential" +
                (slotWidth < 4 ? ". This slot has only " + slotWidth + " lane(s), so even the fastest SSD is capped here." : ". Faster drives won't exceed this."));
        }
        item.Need("Capacity", used > 0 ? $"More than {Format.Bytes(used)} (your used space) if you plan to clone the current drive." : "Any size you need.");
        string Names(List<SmBiosSlot> slots) => string.Join(", ", slots.Select(s => s.Designation.Length > 0 ? s.Designation : s.Kind));
        string secondSlot;
        if (mKey.Count > nvmeDrives && nvmeDrives > 0)
            secondSlot = $"Firmware lists {mKey.Count} M.2 SSD slots ({Names(mKey)}) for {nvmeDrives} installed drive(s), so a free one is likely. You could ADD a drive instead of replacing. Confirm in the service manual.";
        else if (mKey.Count > 0 && mKey.Count == nvmeDrives)
            secondSlot = $"Firmware lists {mKey.Count} M.2 SSD slot{(mKey.Count == 1 ? "" : "s")} ({Names(mKey)}), already used by your current drive, so plan to replace rather than add." +
                         (usageUnreliable ? " (The firmware labels it 'empty', a common Lenovo/OEM quirk, so that flag is ignored.)" : "");
        else
            secondSlot = "A free M.2 slot can't be detected reliably on this system. Check the spec sheet / service manual.";
        if (bKey.Count > 0)
            secondSlot += $" {Names(bKey)} is an M.2 Key B slot, usually for a 4G/5G modem (WWAN). Some accept a 2242 SATA or PCIe x2 SSD; check the manual before buying.";
        item.Need("Second slot", secondSlot);

        if (bitlocker is not null && bitlocker.TryGetValue("C:", out var cProtected) && cProtected)
            item.Notes.Add("BitLocker / Device Encryption is ON for C:. Back up your recovery key first (account.microsoft.com/devices/recoverykey) — you'll need it if the drive or firmware changes.");
        else if (bitlocker is null)
            item.Notes.Add("Couldn't read BitLocker status — check Settings › Privacy & security › Device encryption before swapping drives.");
        item.Notes.Add("Moving Windows: clone the old drive with a USB NVMe enclosure (e.g. Macrium Reflect, Clonezilla), or clean-install from a Windows USB.");
        item.Links.Add(new UpgradeLink("SSD slot & length (spec sheet)", $"https://www.google.com/search?q={Q(query + " M.2 SSD slot 2280 2242 specifications")}"));
        return item;
    }

    // ───────────────────────────── Battery ─────────────────────────────

    private static UpgradeItem? Battery(SystemSpec spec, SmBios? smbios, string query)
    {
        var bat = spec.Battery;
        var sm = smbios?.Battery;
        if (bat is null && sm is null) return null;

        var item = new UpgradeItem("Battery", GlyphBattery);
        var model = !string.IsNullOrWhiteSpace(bat?.Name) ? bat!.Name : sm?.DeviceName ?? "";
        uint designMwh = bat?.DesignMwh > 0 ? bat.DesignMwh : sm?.DesignCapacityMwh ?? 0;
        uint voltageMv = sm?.DesignVoltageMv ?? 0;

        item.Have("Battery model", model)
            .Have("Manufacturer", !string.IsNullOrWhiteSpace(bat?.Manufacturer) ? bat!.Manufacturer : sm?.Manufacturer)
            .Have("Chemistry", !string.IsNullOrWhiteSpace(bat?.Chemistry) ? bat!.Chemistry : sm?.Chemistry)
            .Have("Design capacity", designMwh > 0 ? $"{designMwh / 1000.0:0.0} Wh" : null)
            .Have("Design voltage", voltageMv > 0 ? $"{voltageMv / 1000.0:0.00} V  (≈ {Math.Round(voltageMv / 3850.0)} cells in series)" : null)
            .Have("Holds now", bat is { FullChargeMwh: > 0 } ? $"{bat.FullChargeMwh / 1000.0:0.0} Wh ({bat.WearPercent:0}% wear)" : null)
            .Have("Charge cycles", bat?.Cycles?.ToString(CultureInfo.InvariantCulture))
            .Have("Manufactured", sm?.ManufactureDate is "0" ? null : sm?.ManufactureDate); // firmware reports "0" when unset

        var wear = bat?.WearPercent ?? 0;
        item.Set(wear >= 40 ? Upgradeability.Upgradeable : Upgradeability.Partial,
                 "Replaceable",
                 wear >= 40 ? $"Battery has lost {wear:0}% of its capacity — a replacement would noticeably extend runtime."
                            : "Internal battery — replaceable with basic tools on most laptops (some are glued).");

        item.Need("Part / model", model.Length > 0 ? $"{model} — search this exact code" : null)
            .Need("Must match", voltageMv > 0 ? $"{voltageMv / 1000.0:0.00} V and the same connector; capacity ≥ {designMwh / 1000.0:0.0} Wh" : "Same voltage and connector as the original")
            .Need("Prefer", "Genuine OEM / FRU part — cheap third-party cells often overstate capacity.");
        item.Notes.Add("Manufacturers often sell one battery under several part codes; the spec sheet lists the compatible ones.");
        if (model.Length > 0) item.Links.Add(new UpgradeLink($"Find {model}", $"https://www.google.com/search?q={Q(model + " battery")}"));
        item.Links.Add(new UpgradeLink("Compatible battery FRUs", $"https://www.google.com/search?q={Q(query + " battery FRU part number")}"));
        return item;
    }

    // ───────────────────────────── Display ─────────────────────────────

    private static UpgradeItem? Display(bool isLaptop, string query)
    {
        var panels = DeviceProbe.GetPanels();
        var panel = panels.FirstOrDefault(p => p.IsInternal) ?? (isLaptop ? panels.FirstOrDefault() : null);
        if (panel is null) return null;

        var item = new UpgradeItem("Display panel", GlyphDisplay);
        var size = panel.DiagonalInches is { } inch ? $"{inch:0.#}\"" : "";
        item.Have("Panel maker", DeviceProbe.PanelVendorName(panel.Vendor))
            .Have("Panel part number", panel.PartNumber)
            .Have("EDID ID", $"{panel.Vendor}{panel.ProductCode}")
            .Have("Size", size)
            .Have("Native resolution", panel.Width > 0 ? $"{panel.Width} × {panel.Height}" : null)
            .Have("Refresh rate", panel.RefreshHz > 0 ? $"{panel.RefreshHz:0} Hz" : null)
            .Have("Color depth", panel.BitsPerColor > 0 ? $"{panel.BitsPerColor}-bit per channel{(panel.BitsPerColor == 6 ? " (budget panel, often 45% NTSC)" : "")}" : null)
            .Have("Interface", panel.Interface)
            .Have("Panel year", panel.Year > 0 ? panel.Year.ToString(CultureInfo.InvariantCulture) : null);

        item.Set(Upgradeability.Partial, "Replaceable",
            "The screen panel can be replaced or upgraded (e.g. to a brighter or higher-refresh panel) if the replacement matches exactly.");
        item.Need("Exact match", panel.PartNumber.Length > 0 ? $"{panel.PartNumber} (or a compatible panel listed for it)" : $"Panel with EDID {panel.Vendor}{panel.ProductCode}")
            .Need("Size & resolution", panel.Width > 0 ? $"{size} {panel.Width} × {panel.Height}".Trim() : null)
            .Need("Connector", "eDP — match the pin count (30-pin or 40-pin). Not detectable by software; it's printed on the old panel's label.")
            .Need("Mounting", "Same bracket style (with or without screw tabs) and touch / non-touch.");
        item.Notes.Add("Higher refresh-rate panels (120/144 Hz) only work if this laptop's eDP lanes and firmware support them — 40-pin panels usually need a 40-pin cable.");
        if (panel.PartNumber.Length > 0)
        {
            item.Links.Add(new UpgradeLink("Panelook datasheet", $"https://www.panelook.com/modelsearch.php?keyword={Q(panel.PartNumber)}"));
            item.Links.Add(new UpgradeLink("Compatible panels", $"https://www.google.com/search?q={Q(panel.PartNumber + " replacement panel compatible")}"));
        }
        else
        {
            item.Links.Add(new UpgradeLink("Find replacement panel", $"https://www.google.com/search?q={Q(query + " LCD panel replacement part number")}"));
        }
        return item;
    }

    // ───────────────────────────── Wi-Fi ─────────────────────────────

    private static UpgradeItem? Wifi(bool isLaptop, string cpuVendor)
    {
        var wifi = DeviceProbe.GetWifi();
        if (wifi is null) return null;

        var item = new UpgradeItem("Wi-Fi card", GlyphWifi);
        item.Have("Adapter", wifi.Name)
            .Have("Chip vendor", wifi.Vendor)
            .Have("Standard", wifi.Generation)
            .Have("Radio modes", wifi.RadioTypes.Count > 0 ? string.Join(", ", wifi.RadioTypes) : null)
            .Have("Connected via", wifi.Bus);

        if (wifi.Bus is "USB" or "SDIO")
        {
            item.Set(Upgradeability.NotUpgradeable, "Built in", $"The Wi-Fi chip connects over {wifi.Bus} and is almost always soldered. A USB Wi-Fi adapter is the alternative.");
            return item;
        }

        if (wifi.Generation == "Wi-Fi 7")
        {
            item.Set(Upgradeability.Partial, "Up to date", "You already have the newest Wi-Fi standard — no upgrade needed.");
            return item;
        }

        item.Set(isLaptop ? Upgradeability.Partial : Upgradeability.Upgradeable, isLaptop ? "Usually replaceable" : "Replaceable",
            isLaptop ? $"{wifi.Generation} card — in most laptops an M.2 2230 module that can be swapped (some thin models solder it)."
                     : $"{wifi.Generation} adapter — a PCIe card or M.2 Key-E module can replace it.");
        item.Need("Form factor", isLaptop ? "M.2 2230, Key E (PCIe)" : "M.2 2230 Key E, or a PCIe x1 add-in card")
            .Need("Upgrade target", cpuVendor == "AMD"
                ? "Wi-Fi 6E (e.g. Intel AX210, MediaTek MT7922) or an AMD-compatible Wi-Fi 7 card (MediaTek/Qualcomm). Intel BE200 does not support AMD platforms."
                : "Wi-Fi 6E (e.g. Intel AX210) or Wi-Fi 7 (e.g. Intel BE200)");
        if (wifi.IsCnvi)
            item.Notes.Add("Your card is an Intel CNVi module (part of the radio lives in the Intel chipset). A replacement must be another CNVi module for the same platform — or a full PCIe card such as the AX210.");
        item.Notes.Add("Move both antenna leads (MHF4 connectors) to the new card. Some older Lenovo/HP laptops use a BIOS whitelist that rejects third-party cards.");
        return item;
    }

    // ───────────────────────────── CPU & GPU ─────────────────────────────

    private static UpgradeItem Processor(SystemSpec spec, SmBios? smbios, bool isLaptop)
    {
        var item = new UpgradeItem("Processor (CPU)", GlyphCpu);
        var (socket, upgrade) = smbios?.ProcessorSocket ?? ("", "");
        if (socket.Length == 0) socket = spec.Cpu.Socket;

        item.Have("Processor", spec.Cpu.Name)
            .Have("Socket designation", socket)
            .Have("Package (firmware)", upgrade is "" or "Other" or "Unknown" or "None" ? null : upgrade);

        bool bga = upgrade.StartsWith("BGA", StringComparison.Ordinal)
                   || Regex.IsMatch(socket, @"^(FP\d|FT\d|BGA)", RegexOptions.IgnoreCase);
        string socketName = upgrade is "AM4" or "AM5" || upgrade.StartsWith("LGA", StringComparison.Ordinal) ? upgrade
                          : Regex.Match(socket, @"\b(AM[45]|LGA\s?\d{3,4}|sTRX4|SP[3-6])\b", RegexOptions.IgnoreCase).Value;

        if (isLaptop || bga)
        {
            item.Set(Upgradeability.NotUpgradeable, "Soldered", "Laptop processors are soldered to the motherboard (BGA package) and can't be swapped.");
        }
        else if (socketName.Length > 0)
        {
            item.Set(Upgradeability.Upgradeable, "Socketed", $"Socket {socketName} — you can install another {socketName} processor that your motherboard's BIOS supports.");
            item.Need("Socket", socketName)
                .Need("Compatibility", "Check the motherboard's CPU support list and update the BIOS first if the new CPU needs it.");
            item.Links.Add(new UpgradeLink("Motherboard CPU support list", $"https://www.google.com/search?q={Q($"{spec.Board.BoardVendor} {spec.Board.BoardModel} CPU support list")}"));
        }
        else
        {
            item.Set(Upgradeability.CheckManual, "Check manual", "The firmware doesn't identify the socket clearly.");
        }
        return item;
    }

    /// <summary>Upgrades that don't need a screwdriver: external storage, eGPU, docks — limited by the ports.</summary>
    private static UpgradeItem? Ports(SystemSpec spec, bool isLaptop)
    {
        var usbDevices = spec.Devices.FirstOrDefault(c => c.Title.StartsWith("USB", StringComparison.Ordinal))?.Devices ?? new List<DeviceEntry>();
        var controllers = usbDevices.Where(d => Regex.IsMatch(d.Name, "host controller|xhci|usb4|thunderbolt|router", RegexOptions.IgnoreCase)).ToList();
        if (controllers.Count == 0) return null;

        bool usb4 = controllers.Any(d => Regex.IsMatch(d.Name, "usb4|thunderbolt", RegexOptions.IgnoreCase));
        var item = new UpgradeItem("Ports & external upgrades", "\uE88E");
        foreach (var c in controllers) item.Have("Controller", c.Name);
        item.Have("Fastest port", usb4 ? "USB4 / Thunderbolt (40 Gbps)" : "USB 3.x (5–20 Gbps)");

        if (usb4)
        {
            item.Set(Upgradeability.Upgradeable, "Expandable", "USB4/Thunderbolt lets you add an external GPU, a near-internal-speed SSD and a single-cable dock.");
            item.Need("External SSD", "USB4 / Thunderbolt NVMe enclosure ≈ 3 GB/s (40 Gbps)")
                .Need("External GPU", "Thunderbolt/USB4 eGPU enclosure: expect roughly 70–85% of the card's desktop performance")
                .Need("Dock", "USB4 / Thunderbolt dock for displays, Ethernet and charging over one cable");
        }
        else
        {
            item.Set(Upgradeability.Partial, "USB only", "No USB4/Thunderbolt was detected, so external upgrades are limited to USB 3.x speeds; external GPUs aren't practical.");
            item.Need("External SSD", "USB 3.2 Gen 2 (10 Gbps) NVMe enclosure ≈ 1 GB/s; a 20 Gbps (Gen 2x2) one ≈ 2 GB/s only if your port supports it")
                .Need("Dock", "USB-C dock with DisplayPort Alt Mode, if your USB-C port supports video (look for a DP logo next to it)");
        }
        item.Notes.Add("Exact port speeds depend on each physical port. The spec sheet lists which ports are USB4, 10 Gbps, or 5 Gbps.");
        return item;
    }

    private static UpgradeItem Graphics(SystemSpec spec, bool isLaptop)
    {
        var item = new UpgradeItem("Graphics (GPU)", GlyphGpu);
        var discrete = spec.Gpus.Where(g => !g.IsIntegrated && g.Vendor is "NVIDIA" or "AMD" or "Intel").ToList();
        foreach (var g in spec.Gpus.Where(g => g.Vendor != "Microsoft"))
        {
            var link = g.PnpId.Length > 0 && !g.IsIntegrated ? DeviceProbe.GetPcieLink(g.PnpId) : null;
            var detail = g.IsIntegrated ? "integrated in the CPU" : "discrete";
            if (g.VramBytes > 0) detail += $" · {Format.Bytes(g.VramBytes)}";
            if (link is { CurrentGen: > 0 }) detail += $" · running at {link.Current}";
            if (link is { SlotMaxGen: > 0 }) detail += $" · slot {link.Slot}";
            item.Have(g.Name, detail);
        }

        if (discrete.Count == 0)
        {
            item.Set(isLaptop ? Upgradeability.NotUpgradeable : Upgradeability.Partial,
                isLaptop ? "Integrated" : "Add-in possible",
                isLaptop ? "Graphics are built into the processor. For more GPU power, use an external GPU (USB4/Thunderbolt) if the laptop supports it."
                         : "Graphics are integrated. If the motherboard has a PCIe x16 slot and the power supply has PCIe power connectors, a graphics card can be added.");
            return item;
        }

        if (isLaptop)
        {
            item.Set(Upgradeability.NotUpgradeable, "Soldered", "Laptop graphics chips are soldered to the motherboard.");
            return item;
        }

        item.Set(Upgradeability.Upgradeable, "Replaceable", "Desktop graphics card in a PCIe slot — it can be replaced.");
        item.Need("Slot", "PCIe x16 (any generation works; newer cards are backward compatible)")
            .Need("Power supply", "Check the PSU wattage and its PCIe 8-pin / 12V-2x6 connectors against the new card's requirement.")
            .Need("Space", "Measure card length and slot width (2- / 2.5- / 3-slot) against the case.");
        return item;
    }

    // ───────────────────────────── Export ─────────────────────────────

    private static string BuildShoppingList(UpgradeReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Upgrade sheet — {report.MachineName}");
        if (report.MachineDetail.Length > 0) sb.AppendLine(report.MachineDetail);
        sb.AppendLine($"Generated by CoreScope on {DateTime.Now:yyyy-MM-dd}");

        foreach (var item in report.Items)
        {
            sb.AppendLine();
            sb.AppendLine($"== {item.Component}: {item.StatusText} ==");
            sb.AppendLine(item.Summary);
            if (item.Current.Count > 0)
            {
                sb.AppendLine("You have:");
                foreach (var r in item.Current) sb.AppendLine($"  {r.Label}: {r.Value}");
            }
            if (item.Buy.Count > 0)
            {
                sb.AppendLine("To buy:");
                foreach (var r in item.Buy) sb.AppendLine($"  {r.Label}: {r.Value}");
            }
            foreach (var n in item.Notes) sb.AppendLine($"  Note: {n}");
        }

        if (report.Links.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Look-ups:");
            foreach (var l in report.Links) sb.AppendLine($"  {l.Label}: {l.Url}");
        }
        return sb.ToString();
    }
}
