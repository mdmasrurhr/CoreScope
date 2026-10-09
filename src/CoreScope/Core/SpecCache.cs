using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CoreScope.Core.Hardware;

namespace CoreScope.Core;

/// <summary>
/// The last finished hardware scan, saved as JSON so the next launch can show every page instantly while the
/// fresh scan (about 3 seconds) runs in the background. Hardware rarely changes between launches.
/// </summary>
public sealed class SpecCache
{
    public string AppVersion { get; set; } = "";
    public DateTime Saved { get; set; }
    public string MachineName { get; set; } = "";
    public string MachineDetail { get; set; } = "";
    public Dictionary<string, CachedPage> Pages { get; set; } = new();

    public sealed class CachedPage
    {
        public string Headline { get; set; } = "";
        public string SubHeadline { get; set; } = "";
        public List<CachedSection> Sections { get; set; } = new();
    }

    public sealed class CachedSection
    {
        public string Title { get; set; } = "";
        public string? Subtitle { get; set; }
        public List<string[]> Rows { get; set; } = new();
        public List<string> Tags { get; set; } = new();

        public static CachedSection From(SpecSection s) => new()
        {
            Title = s.Title, Subtitle = s.Subtitle,
            Rows = s.Rows.Select(r => new[] { r.Label, r.Value }).ToList(),
            Tags = s.Tags.ToList(),
        };

        public SpecSection ToSection()
        {
            var section = new SpecSection(Title, Subtitle);
            foreach (var r in Rows.Where(r => r.Length == 2)) section.Add(r[0], r[1]);
            section.Tags.AddRange(Tags);
            return section;
        }
    }

    private static string FilePath => Path.Combine(AppPaths.DataFolder, "last-scan.json");

    public static SpecCache? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var cache = JsonSerializer.Deserialize<SpecCache>(File.ReadAllText(FilePath));
            // A different CoreScope version may lay pages out differently: rescan only.
            return cache is not null && cache.AppVersion == UpdateChecker.CurrentVersion.ToString() ? cache : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error("Reading last scan", ex);
            return null;
        }
    }

    public void Save()
    {
        try
        {
            AppVersion = UpdateChecker.CurrentVersion.ToString();
            Saved = DateTime.Now;
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Saving last scan", ex);
        }
    }
}
