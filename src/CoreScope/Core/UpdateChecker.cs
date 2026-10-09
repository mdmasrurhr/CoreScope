using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CoreScope.Core;

public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? InstallerUrl, long InstallerBytes, string Notes);

/// <summary>
/// Checks the GitHub releases of mdmasrurhr/CoreScope for a newer version and installs it on request
/// (downloads the Setup.exe, starts it, and lets the caller exit). No data is sent beyond a normal web request.
/// </summary>
public static class UpdateChecker
{
    public const string Repo = "mdmasrurhr/CoreScope";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    public static Version CurrentVersion => typeof(UpdateChecker).Assembly.GetName().Version is { } v
        ? new Version(v.Major, v.Minor, Math.Max(0, v.Build))
        : new Version(1, 0, 0);

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"CoreScope/{CurrentVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Returns the latest release if it's newer than this build, otherwise null. Never throws.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancel = default)
    {
        try
        {
            using var http = NewClient();
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", cancel));
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
            latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
            AppSettings.Current.LastUpdateCheck = DateTime.Now;
            AppSettings.Current.Save();
            if (latest <= CurrentVersion) return null;

            string? url = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        url = a.GetProperty("browser_download_url").GetString();
                        size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    }
                }
            }
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            var page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? ReleasesPage : ReleasesPage;
            Log.Info($"Update available: {latest} (running {CurrentVersion})");
            return new UpdateInfo(latest, tag, page, url, size, notes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Info($"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Downloads the installer to %TEMP% and starts it. Returns true when the installer is running (caller should exit).</summary>
    public static async Task<(bool Started, string Message)> DownloadAndRunAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        if (update.InstallerUrl is null) return (false, "This release has no installer attached. Opening the download page instead.");
        var path = Path.Combine(Path.GetTempPath(), $"CoreScope-{update.Version}-Setup.exe");
        try
        {
            using var http = NewClient();
            using var response = await http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancel);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? update.InstallerBytes;
            await using (var src = await response.Content.ReadAsStreamAsync(cancel))
            await using (var dst = File.Create(path))
            {
                var buffer = new byte[128 * 1024];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, cancel)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), cancel);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            if (update.InstallerBytes > 0 && new FileInfo(path).Length != update.InstallerBytes)
                return (false, "The download was incomplete. Please try again.");

            Log.Info($"Update downloaded: {path} sha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}");
            // Normal (non-silent) setup: the user sees the wizard; CoreScope exits so files can be replaced.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return (true, "Installing the update…");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Error("Update download", ex);
            return (false, "The update couldn't be downloaded. You can get it from the download page instead.");
        }
    }
}
