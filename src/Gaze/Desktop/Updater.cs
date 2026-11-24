using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Gaze.Core;

namespace Gaze.Desktop;

public sealed record UpdateInfo(string Tag, string DownloadUrl, long Size);

/// <summary>
/// Self-update from the latest GitHub release. Only talks to api.github.com / github.com and sends no personal data.
/// A running exe cannot be overwritten but it can be renamed, so: rename self to .old, drop the new exe in its place, start it.
/// </summary>
public static class Updater
{
    const string Api = "https://api.github.com/repos/Willel09/Gaze/releases/latest";
    static readonly HttpClient Http = Create();

    public static Version Current { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Gaze-updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Returns the newer release, or null when already up to date.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var res = await Http.GetAsync(Api, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        if (!VersionCompare.IsNewer(tag, Current)) return null;

        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (name.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase))
                return new UpdateInfo(tag!, a.GetProperty("browser_download_url").GetString()!, a.GetProperty("size").GetInt64());
        }
        return null;
    }

    /// <summary>Downloads and swaps the exe, then starts the new one. The caller quits afterwards.</summary>
    public static async Task InstallAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct = default)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the running exe.");
        var dir = Path.Combine(SettingsStore.Dir, "update");
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "Gaze-new.exe");

        using (var res = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? info.Size;
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            var buf = new byte[81920];
            long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress.Report((double)done / total);
            }
        }

        if (new FileInfo(tmp).Length < 1_000_000) throw new InvalidDataException("The download looks incomplete.");

        var old = self + ".old";
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(self, old);
            File.Move(tmp, self);
        }
        catch (Exception ex)
        {
            if (!File.Exists(self) && File.Exists(old)) File.Move(old, self); // put it back
            throw new IOException("Could not replace the app file. Is Gaze installed in a protected folder? " + ex.Message, ex);
        }

        Log.Write($"updated {Current} -> {info.Tag}");
        Process.Start(new ProcessStartInfo(self, "--updated") { UseShellExecute = false });
    }

    public static void CleanUp()
    {
        try
        {
            var self = Environment.ProcessPath;
            if (self != null && File.Exists(self + ".old")) File.Delete(self + ".old");
            var dir = Path.Combine(SettingsStore.Dir, "update");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { /* old exe may still be closing; next start retries */ }
    }
}
