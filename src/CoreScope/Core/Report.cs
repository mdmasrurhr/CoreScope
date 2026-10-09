using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using CoreScope.Core.Hardware;

namespace CoreScope.Core;

/// <summary>Full system report as a single self-contained HTML page (light/dark aware) or compact plain text.</summary>
public static class Report
{
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static string ToHtml(SystemSpec spec, IReadOnlyList<SensorReading> readings, IReadOnlyList<Insight> insights, BenchmarkResult? bench = null)
    {
        var machine = spec.Upgrade?.MachineName ?? $"{spec.Board.SystemVendor} {spec.Board.SystemModel}";
        var sb = new StringBuilder();
        sb.Append("""
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>CoreScope report</title>
<style>
:root{--bg:#f6f7f9;--card:#fff;--text:#1b1f24;--muted:#5d6670;--line:#e3e6ea;--accent:#2563eb;--crit:#e5484d;--warn:#d97706;--good:#10b981;--info:#3b82f6}
@media (prefers-color-scheme:dark){:root{--bg:#16181c;--card:#202329;--text:#e8eaed;--muted:#9aa3ad;--line:#30343b;--accent:#60a5fa}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif}
main{max-width:1040px;margin:0 auto;padding:32px 24px}h1{font-size:24px;margin:4px 0 4px}h2{font-size:20px;margin:32px 0 12px}h3{font-size:16px;margin:0 0 8px}
.over{font-size:12px;font-weight:600;color:var(--muted);letter-spacing:.04em}.sub{color:var(--muted);margin:0 0 24px}
.card{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:16px 20px;margin:0 0 12px}
table{border-collapse:collapse;width:100%}td{padding:4px 0;vertical-align:top}td:first-child{color:var(--muted);width:240px;padding-right:16px}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:12px}.tile .v{font-size:24px;font-weight:600}
.ins{border-left:4px solid var(--info)}.ins.Critical{border-color:var(--crit)}.ins.Warning{border-color:var(--warn)}.ins.Good{border-color:var(--good)}
.chip{display:inline-block;border:1px solid var(--line);border-radius:4px;padding:2px 8px;margin:0 4px 4px 0;font-size:12px}
.muted{color:var(--muted)}footer{color:var(--muted);font-size:12px;margin-top:32px}
@media print{body{background:#fff}.card{break-inside:avoid}}
</style></head><body><main>
""");
        sb.Append($"<div class=\"over\">COREScope SYSTEM REPORT</div><h1>{E(machine)}</h1>");
        sb.Append($"<p class=\"sub\">{E(spec.Os.ComputerName)} · {E(spec.Os.Name)} {E(spec.Os.DisplayVersion)} (build {E(spec.Os.Build)}) · generated {DateTime.Now:yyyy-MM-dd HH:mm}</p>");

        // Summary tiles
        sb.Append("<div class=\"grid\">");
        Tile(sb, "Processor", spec.Cpu.Name, $"{spec.Cpu.Cores} cores / {spec.Cpu.Threads} threads");
        Tile(sb, "Memory", $"{Format.Bytes(spec.TotalRamBytes)}", spec.MemoryTypeSummary);
        Tile(sb, "Graphics", spec.Gpus.FirstOrDefault()?.Name ?? "—", spec.Gpus.FirstOrDefault() is { VramBytes: > 0 } g ? $"{Format.Bytes(g.VramBytes)} VRAM" : "");
        Tile(sb, "Storage", spec.Disks.FirstOrDefault()?.Name ?? "—", string.Join(" + ", spec.Disks.Select(d => Format.Bytes(d.SizeBytes))));
        sb.Append("</div>");

        if (insights.Count > 0)
        {
            sb.Append("<h2>Insights</h2>");
            foreach (var i in insights)
            {
                sb.Append($"<div class=\"card ins {i.Severity}\"><h3>{E(i.Title)}</h3><div class=\"muted\">{E(i.Category)} · {i.Severity}</div><p>{E(i.Detail)}</p>");
                if (i.Action is { Length: > 0 }) sb.Append($"<p><b>→ {E(i.Action)}</b></p>");
                if (i.HasEvidence) sb.Append($"<p class=\"muted\">Measured: {E(i.Measured)}<br>Rule: {E(i.Rule)}<br>Source: {E(i.Source)}</p>");
                sb.Append("</div>");
            }
        }

        if (bench is not null)
        {
            sb.Append("<h2>Benchmark</h2><div class=\"card\"><table>");
            Row(sb, "CPU single-core", $"{bench.CpuSingle:0} points");
            Row(sb, "CPU all cores", $"{bench.CpuMulti:0} points");
            Row(sb, "Memory copy", $"{bench.MemoryCopyGBs:0.0} GB/s");
            Row(sb, "Memory latency", $"{bench.MemoryLatencyNs:0} ns");
            Row(sb, "Disk sequential write / read", $"{bench.DiskSeqWriteMBs:0} / {bench.DiskSeqReadMBs:0} MB/s");
            Row(sb, "Disk 4K random read (QD1)", $"{bench.Disk4kReadMBs:0.0} MB/s");
            sb.Append("</table></div>");
        }

        Sections(sb, "Processor", spec.CpuSections);
        Sections(sb, "Memory", spec.MemorySections);
        Sections(sb, "Graphics & displays", spec.GpuSections);
        Sections(sb, "Storage", spec.StorageSections);
        Sections(sb, "Motherboard & firmware", spec.BoardSections);
        Sections(sb, "Windows & battery", spec.SystemSections);

        if (spec.Upgrade is { } up)
        {
            sb.Append("<h2>Upgrade guide</h2>");
            foreach (var item in up.Items)
            {
                sb.Append($"<div class=\"card\"><h3>{E(item.Component)} · {E(item.StatusText)}</h3><p>{E(item.Summary)}</p>");
                if (item.Buy.Count > 0)
                {
                    sb.Append("<table>");
                    foreach (var r in item.Buy) Row(sb, r.Label, r.Value);
                    sb.Append("</table>");
                }
                foreach (var n in item.Notes) sb.Append($"<p class=\"muted\">{E(n)}</p>");
                sb.Append("</div>");
            }
        }

        if (spec.Devices.Count > 0)
        {
            sb.Append("<h2>Devices</h2>");
            foreach (var c in spec.Devices)
            {
                sb.Append($"<div class=\"card\"><h3>{E(c.Title)}</h3><table>");
                foreach (var d in c.Devices) Row(sb, d.Name, d.Subtitle);
                sb.Append("</table></div>");
            }
        }

        if (readings.Count > 0)
        {
            sb.Append("<h2>Sensor snapshot</h2>");
            foreach (var group in readings.GroupBy(r => r.GroupName))
            {
                sb.Append($"<div class=\"card\"><h3>{E(group.Key)}</h3><table>");
                foreach (var r in group.Where(r => r.Value.HasValue)) Row(sb, $"{r.Name} ({r.Type})", Format.Sensor(r.Type, r.Value));
                sb.Append("</table></div>");
            }
        }

        sb.Append($"<footer>Generated by CoreScope {E(typeof(Report).Assembly.GetName().Version?.ToString(3))}. Serial numbers are included; remove them before sharing publicly if you prefer.</footer></main></body></html>");
        return sb.ToString();
    }

    private static void Tile(StringBuilder sb, string label, string value, string detail) =>
        sb.Append($"<div class=\"card tile\"><div class=\"over\">{E(label)}</div><div class=\"v\">{E(value)}</div><div class=\"muted\">{E(detail)}</div></div>");

    private static void Row(StringBuilder sb, string label, string value) =>
        sb.Append($"<tr><td>{E(label)}</td><td>{E(value)}</td></tr>");

    private static void Sections(StringBuilder sb, string title, IEnumerable<SpecSection> sections)
    {
        var list = sections.ToList();
        if (list.Count == 0) return;
        sb.Append($"<h2>{E(title)}</h2>");
        foreach (var s in list)
        {
            sb.Append($"<div class=\"card\"><h3>{E(s.Title)}</h3>");
            if (s.Subtitle is { Length: > 0 }) sb.Append($"<div class=\"muted\">{E(s.Subtitle)}</div>");
            if (s.Rows.Count > 0)
            {
                sb.Append("<table>");
                foreach (var r in s.Rows) Row(sb, r.Label, r.Value);
                sb.Append("</table>");
            }
            foreach (var t in s.Tags) sb.Append($"<span class=\"chip\">{E(t)}</span>");
            sb.Append("</div>");
        }
    }

    /// <summary>Short spec summary for forums, store chats and support tickets (no serial numbers).</summary>
    public static string ToText(SystemSpec spec)
    {
        var sb = new StringBuilder();
        var machine = spec.Upgrade?.MachineName ?? $"{spec.Board.SystemVendor} {spec.Board.SystemModel}";
        sb.AppendLine($"{machine}");
        sb.AppendLine($"OS:       {spec.Os.Name} {spec.Os.DisplayVersion} (build {spec.Os.Build})");
        sb.AppendLine($"CPU:      {spec.Cpu.Name} ({spec.Cpu.Cores}C/{spec.Cpu.Threads}T{(spec.Cpu.Codename.Length > 0 ? $", {spec.Cpu.Codename}" : "")})");
        sb.AppendLine($"Memory:   {Format.Bytes(spec.TotalRamBytes)} {spec.MemoryTypeSummary} ({spec.Modules.Count} module(s))");
        foreach (var g in spec.Gpus) sb.AppendLine($"GPU:      {g.Name}{(g.VramBytes > 0 ? $" {Format.Bytes(g.VramBytes)}" : "")} · driver {g.DriverVersion}");
        foreach (var d in spec.Disks) sb.AppendLine($"Storage:  {d.Name} {Format.Bytes(d.SizeBytes)} {d.BusType} {d.MediaType}{(d.WearPercent is { } w ? $" · {100 - w}% life" : "")}");
        sb.AppendLine($"Board:    {spec.Board.BoardVendor} {spec.Board.BoardModel} · BIOS {spec.Board.BiosVersion} ({Format.Date(spec.Board.BiosDate)})");
        if (spec.Battery is { } b) sb.AppendLine($"Battery:  {b.Name} · {b.WearPercent:0}% wear{(b.Cycles is { } c ? $" · {c} cycles" : "")}");
        sb.AppendLine("(CoreScope)");
        return sb.ToString();
    }
}
