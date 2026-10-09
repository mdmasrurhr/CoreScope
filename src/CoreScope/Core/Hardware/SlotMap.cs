using System;
using System.Collections.Generic;
using System.Linq;

namespace CoreScope.Core.Hardware;

public enum SlotState { Filled, Empty, Soldered, Unknown }

/// <summary>One box in the slot diagram (a RAM slot, a soldered memory package, an M.2 slot).</summary>
public sealed record SlotVisual(string Label, string Detail, SlotState State)
{
    public string StateName => State.ToString();
    public string StateText => State switch
    {
        SlotState.Filled => "In use",
        SlotState.Empty => "Empty — free to use",
        SlotState.Soldered => "Soldered",
        _ => "Status not reported",
    };
}

/// <summary>Builds the RAM and M.2 slot diagrams from the firmware (SMBIOS) tables.</summary>
public static class SlotMap
{
    public static List<SlotVisual> Memory(SystemSpec spec)
    {
        var list = new List<SlotVisual>();
        var devices = spec.SmBios?.MemoryDevices ?? new List<SmBiosMemoryDevice>();
        if (devices.Count == 0)
        {
            // WMI only lists installed modules: show those, empty slots stay unknown.
            foreach (var m in spec.Modules)
                list.Add(new SlotVisual(m.Slot.Length > 0 ? m.Slot : "Module",
                    $"{Format.Bytes(m.CapacityBytes)} {m.TypeName}".Trim() + (m.ConfiguredMts > 0 ? $" · {m.ConfiguredMts} MT/s" : ""),
                    m.FormFactor == "Soldered" ? SlotState.Soldered : SlotState.Filled));
            return list;
        }
        foreach (var d in devices)
        {
            var label = d.Locator.Length > 0 ? d.Locator : "Memory";
            if (d.IsSoldered && d.Installed)
                list.Add(new SlotVisual(label, $"{Format.Bytes(d.SizeBytes)} {d.Type} on the board", SlotState.Soldered));
            else if (d.Installed)
                list.Add(new SlotVisual(label, $"{Format.Bytes(d.SizeBytes)} {d.Type} {d.FormFactor}".Trim()
                    + (d.ConfiguredSpeedMts > 0 ? $" · {d.ConfiguredSpeedMts} MT/s" : ""), SlotState.Filled));
            else if (d.FormFactor is "DIMM" or "SO-DIMM" or "CAMM")
                list.Add(new SlotVisual(label, $"Empty {d.FormFactor} slot", SlotState.Empty));
        }
        return list;
    }

    /// <summary>
    /// M.2 slots. Many laptops report every slot as "Empty" even with an SSD fitted; when that's detected the
    /// slots show "Status not reported" instead of a wrong "Empty".
    /// </summary>
    public static (List<SlotVisual> Slots, string? Note) M2(SystemSpec spec)
    {
        var list = new List<SlotVisual>();
        var slots = spec.SmBios?.Slots.Where(s => s.Kind.StartsWith("M.2", StringComparison.Ordinal)).ToList() ?? new();
        if (slots.Count == 0) return (list, null);
        int nvme = spec.Disks.Count(d => d.BusType == "NVMe");
        var storageSlots = slots.Where(s => !s.Kind.Contains("Wi-Fi", StringComparison.Ordinal)).ToList();
        bool unreliable = nvme > 0 && storageSlots.Count > 0 && storageSlots.All(s => s.Usage != "In use");
        foreach (var s in slots)
        {
            var label = s.Designation.Length > 0 ? s.Designation : s.Kind;
            var state = s.Kind.Contains("Wi-Fi", StringComparison.Ordinal) ? (s.Usage == "In use" ? SlotState.Filled : SlotState.Unknown)
                : unreliable ? SlotState.Unknown
                : s.Usage == "In use" ? SlotState.Filled : s.Usage == "Empty" ? SlotState.Empty : SlotState.Unknown;
            list.Add(new SlotVisual(label, s.Kind, state));
        }
        string? note = unreliable
            ? $"This PC has {nvme} NVMe drive{(nvme == 1 ? "" : "s")}, but its firmware marks every M.2 slot as empty, so slot use can't be confirmed. Check the service manual before buying a second SSD."
            : null;
        return (list, note);
    }
}
