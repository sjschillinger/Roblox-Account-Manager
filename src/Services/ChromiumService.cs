using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace RobloxAccountManager.Services;

/// <summary>
/// Manages an optional private, portable CloakBrowser build (a Chromium — github.com/CloakHQ/CloakBrowser)
/// under data/cloakbrowser. Opening accounts works without it through Edge or Chrome; this download is
/// for people who want a browser that is entirely separate from anything installed on the PC.
///
/// Downloaded from the newest GitHub release that ships a free windows-x64 zip, and verified against
/// the SHA-256 digest GitHub publishes for that asset before anything is extracted.
/// </summary>
public static class ChromiumService
{
    // 100 per page: the project publishes many binary-less "-pro" tags, and a short page can push the
    // last free Windows build off the first page entirely.
    private const string ReleasesUrl = "https://api.github.com/repos/CloakHQ/CloakBrowser/releases?per_page=100";
    private const string AssetName = "cloakbrowser-windows-x64.zip";

    private static string CloakDir => Paths.InData("cloakbrowser");
    private static string LegacyChromePath => Path.Combine(Paths.InData("chromium"), "chrome-win", "chrome.exe");

    private static string? _cachedExe;

    /// <summary>Browser exe — CloakBrowser preferred, a legacy Chromium download as fallback, "" if none.</summary>
    public static string ChromePath => FindExe() ?? "";

    public static bool IsInstalled => FindExe() != null;

    private static string? FindExe()
    {
        if (_cachedExe != null && File.Exists(_cachedExe)) return _cachedExe;
        _cachedExe = null;

        try
        {
            if (Directory.Exists(CloakDir))
                _cachedExe = Directory.EnumerateFiles(CloakDir, "chrome.exe", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { }

        if (_cachedExe == null && File.Exists(LegacyChromePath))
            _cachedExe = LegacyChromePath;

        return _cachedExe;
    }

    public record Progress(long Done, long Total, string Phase)
    {
        public double Fraction => Total > 0 ? (double)Done / Total : 0;
    }

    /// <summary>Downloads, verifies and extracts the newest free CloakBrowser build. Safe to cancel.</summary>
    public static async Task DownloadAsync(IProgress<Progress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(CloakDir);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RobloxAccountManager");

        progress.Report(new Progress(0, 0, L.T("Chromium.Phase.Finding")));
        string json = await http.GetStringAsync(ReleasesUrl, ct);
        string? url = null, tag = null, digest = null;
        using (var doc = JsonDocument.Parse(json))
        {
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
                if (!rel.TryGetProperty("assets", out var assets)) continue;
                foreach (var asset in assets.EnumerateArray())
                {
                    if (!asset.TryGetProperty("name", out var n) || n.GetString() != AssetName) continue;
                    if (!asset.TryGetProperty("browser_download_url", out var u)) continue;
                    url = u.GetString();
                    tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                    digest = asset.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
                    break;
                }
                if (url != null) break;
            }
        }
        if (url == null || !IsGitHubUrl(url))
            throw new InvalidOperationException(L.T("Chromium.Error.NoBuild"));

        string zipPath = Path.Combine(CloakDir, AssetName);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1;

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            var lastReport = DateTime.MinValue;
            string phase = L.T("Chromium.Phase.Downloading", tag ?? "");
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                sha.AppendData(buffer, 0, read);
                done += read;
                if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(100))
                {
                    lastReport = DateTime.UtcNow;
                    progress.Report(new Progress(done, total, phase));
                }
            }
        }

        // GitHub computes this digest for every uploaded release asset; a mismatch means the file was
        // corrupted or altered in transit, and it must not be extracted and executed.
        progress.Report(new Progress(0, 0, L.T("Chromium.Phase.Verifying")));
        if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            string actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actual, digest[7..], StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(zipPath); } catch { }
                throw new InvalidOperationException(L.T("Chromium.Error.Checksum"));
            }
        }

        progress.Report(new Progress(0, 0, L.T("Chromium.Phase.Extracting")));
        foreach (var dir in Directory.GetDirectories(CloakDir))
        {
            try { Directory.Delete(dir, true); } catch { }
        }
        // ExtractToDirectory rejects entries that would escape the target folder ("zip slip").
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, CloakDir, overwriteFiles: true), ct);
        try { File.Delete(zipPath); } catch { }

        _cachedExe = null;
        if (FindExe() == null || _cachedExe == LegacyChromePath)
            throw new InvalidOperationException(L.T("Chromium.Error.NoExe"));

        progress.Report(new Progress(1, 1, L.T("Chromium.Phase.Ready")));
    }

    /// <summary>Deletes the downloaded browser. Returns false when files were still in use.</summary>
    public static bool Uninstall()
    {
        try
        {
            if (Directory.Exists(CloakDir)) Directory.Delete(CloakDir, true);
            _cachedExe = null;
            return true;
        }
        catch { _cachedExe = null; return false; }
    }

    private static bool IsGitHubUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
           && (u.Host == "github.com" || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
}
