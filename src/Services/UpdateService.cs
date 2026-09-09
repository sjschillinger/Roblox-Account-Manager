using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RobloxAccountManager.Services;

/// <summary>A newer release discovered on GitHub.</summary>
public sealed record UpdateInfo(
    Version Version, string DownloadUrl, long Size,
    string? Notes = null, DateTime? PublishedAt = null, string? ReleasePageUrl = null,
    string? Sha256 = null, bool IsPrerelease = false)
{
    /// <summary>Short display form, e.g. "v1.1.0".</summary>
    public string VersionText => $"v{Version.ToString(3)}";

    /// <summary>Download size, e.g. "154.9 MB"; empty when GitHub didn't report one.</summary>
    public string SizeText => Size > 0 ? $"{Size / 1024.0 / 1024.0:0.#} MB" : "";
}

/// <summary>
/// Portable self-updater, stage 1: checks GitHub for a newer release and hands the actual
/// swap over to a copy of this exe running from %TEMP% (see App.OnStartup / UpdaterWindow).
/// </summary>
public static class UpdateService
{
    private const string Repo = "Vaelixx/Roblox-Account-Manager";
    private const string LatestReleaseUrl = $"https://api.github.com/repos/{Repo}/releases/latest";

    /// <summary>Release list, used when pre-releases are opted into (latest/ hides them).</summary>
    private const string ReleaseListUrl = $"https://api.github.com/repos/{Repo}/releases?per_page=15";

    /// <summary>Folder the updater copy runs from; --post-update deletes it afterwards.</summary>
    public static string UpdateTempDir => Path.Combine(Path.GetTempPath(), "RobloxAccountManagerUpdate");

    /// <summary>Suffix of the backup the updater leaves behind so a bad build can be rolled back.</summary>
    public const string BackupSuffix = ".previous.exe";

    // Tags aren't guaranteed to be "vX.Y.Z" (the first release is tagged "roblox_account_manager"),
    // so pull the first dotted number group out of the tag, falling back to the release name.
    private static readonly Regex VersionPattern = new(@"\d+(\.\d+){1,3}", RegexOptions.Compiled);

    /// <summary>A bare SHA-256 digest anywhere in the release body.</summary>
    private static readonly Regex Sha256Pattern = new(@"\b[0-9a-fA-F]{64}\b", RegexOptions.Compiled);

    // ---- conditional requests -------------------------------------------------------------
    // The background poll asks the same question over and over. GitHub answers an unauthenticated
    // client only 60 times an hour per IP, and a 304 response does NOT count against that budget —
    // so remembering the ETag turns a recurring poll from something that can exhaust the limit
    // (and then fail for an hour) into something essentially free.
    private static readonly Dictionary<string, string> _etags = new();
    private static readonly Dictionary<string, UpdateInfo?> _cached = new();

    /// <summary>Set when GitHub reported the rate limit as spent; no request is made until it passes.</summary>
    private static DateTimeOffset _rateLimitedUntil = DateTimeOffset.MinValue;

    /// <summary>True while a 403 rate-limit backoff is in effect (surfaced on the Settings card).</summary>
    public static bool IsRateLimited => DateTimeOffset.UtcNow < _rateLimitedUntil;

    /// <summary>When the GitHub rate limit resets, in local time; null when not limited.</summary>
    public static DateTime? RateLimitResetsAt =>
        IsRateLimited ? _rateLimitedUntil.LocalDateTime : null;

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RobloxAccountManager"); // GitHub API requires a UA
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>
    /// Returns the available update, or null when up to date / on any error (silent).
    /// Honours the user's update settings: the pre-release channel and the skipped version.
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync(bool ignoreSkip = false)
    {
        var s = SettingsService.Current;
        string url = s.IncludePrereleases ? ReleaseListUrl : LatestReleaseUrl;

        // A spent rate limit answers every request with 403 for up to an hour. Asking anyway
        // just burns time and produces a misleading "check failed" — wait it out instead.
        // The cache is keyed by URL so a user who just turned pre-releases off is not handed a
        // pre-release that was cached while the channel was on.
        if (IsRateLimited)
            return Applicable(_cached.TryGetValue(url, out var stale) ? stale : null, ignoreSkip);

        try
        {
            using var http = NewClient(TimeSpan.FromSeconds(20));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (_etags.TryGetValue(url, out var etag))
                req.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using var resp = await http.SendAsync(req).ConfigureAwait(false);

            // Nothing changed since the last poll — reuse what we parsed then, for free.
            if (resp.StatusCode == HttpStatusCode.NotModified)
                return Applicable(_cached.TryGetValue(url, out var hit) ? hit : null, ignoreSkip);

            if (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == (HttpStatusCode)429)
            {
                NoteRateLimit(resp);
                return null;
            }
            if (!resp.IsSuccessStatusCode) return null;

            if (resp.Headers.ETag?.Tag is { Length: > 0 } tag) _etags[url] = tag;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));

            UpdateInfo? found = doc.RootElement.ValueKind == JsonValueKind.Array
                ? BestOf(doc.RootElement, s.IncludePrereleases)
                : ParseRelease(doc.RootElement);

            // Cache even a null: "there is no newer release" is the answer we want a 304 to reuse.
            _cached[url] = found;
            return Applicable(found, ignoreSkip);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateService] Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Drops an update the user chose to skip (unless the check was explicitly manual).</summary>
    private static UpdateInfo? Applicable(UpdateInfo? info, bool ignoreSkip)
    {
        if (info == null) return null;
        if (!ignoreSkip && SettingsService.Current.SkippedUpdateVersion == info.VersionText) return null;
        return info;
    }

    /// <summary>Remembers the reset time GitHub reports so the poll stops asking until then.</summary>
    private static void NoteRateLimit(HttpResponseMessage resp)
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(15);   // conservative default
        if (resp.Headers.TryGetValues("X-RateLimit-Reset", out var vals)
            && long.TryParse(vals.FirstOrDefault(), out long epoch))
        {
            var reported = DateTimeOffset.FromUnixTimeSeconds(epoch);
            // Clamp: a wrong clock on either end must not park the checker for days.
            if (reported > DateTimeOffset.UtcNow && reported < DateTimeOffset.UtcNow.AddHours(2))
                reset = reported;
        }
        _rateLimitedUntil = reset;
        Debug.WriteLine($"[UpdateService] GitHub rate limit hit; backing off until {reset.LocalDateTime:HH:mm}.");
    }

    /// <summary>Highest-versioned usable release out of a /releases listing.</summary>
    private static UpdateInfo? BestOf(JsonElement array, bool allowPrereleases)
    {
        UpdateInfo? best = null;
        foreach (var release in array.EnumerateArray())
        {
            // A draft is not published: its assets 404 for anyone but the author.
            if (release.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;

            bool pre = release.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;
            if (pre && !allowPrereleases) continue;

            var info = ParseRelease(release);
            if (info != null && (best == null || info.Version > best.Version)) best = info;
        }
        return best;
    }

    /// <summary>Turns one release object into an <see cref="UpdateInfo"/>, or null when unusable.</summary>
    private static UpdateInfo? ParseRelease(JsonElement root)
    {
        var remote = ParseVersion(
            root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null,
            root.TryGetProperty("name", out var name) ? name.GetString() : null);
        if (remote == null || remote <= CurrentVersion()) return null;

        // Release metadata for the richer update prompt (all optional — null on any miss).
        string? notes = root.TryGetProperty("body", out var body) ? body.GetString() : null;
        string? pageUrl = root.TryGetProperty("html_url", out var hu) ? hu.GetString() : null;
        bool prerelease = root.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True;
        DateTime? publishedAt =
            root.TryGetProperty("published_at", out var pa)
            && DateTime.TryParse(pa.GetString(), null,
                   System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt)
                ? dt : null;

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        var chosen = PickAsset(assets);
        if (chosen == null) return null;

        return new UpdateInfo(remote, chosen.Value.Url, chosen.Value.Size, notes, publishedAt, pageUrl,
                              Sha256For(notes, chosen.Value.Name), prerelease);
    }

    /// <summary>
    /// The application exe among the release's assets. Picking "the first .exe" was fine while a
    /// release carried exactly one, but any second exe attached later (a portable variant, an
    /// installer, a debug build) would silently become what everybody downloads. Prefer an asset
    /// whose name looks like this application, and only fall back to first-exe when none does.
    /// </summary>
    private static (string Url, long Size, string Name)? PickAsset(JsonElement assets)
    {
        (string Url, long Size, string Name)? fallback = null;

        foreach (var asset in assets.EnumerateArray())
        {
            string assetName = asset.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
            if (!assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

            string? url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(url)) continue;

            long size = asset.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long s) ? s : 0;
            var candidate = (url, size, assetName);

            string bare = assetName.Replace(" ", "").Replace("-", "").Replace("_", "");
            if (bare.StartsWith("RobloxAccountManager", StringComparison.OrdinalIgnoreCase))
                return candidate;

            fallback ??= candidate;
        }

        return fallback;
    }

    /// <summary>
    /// The SHA-256 of the download, when the release body publishes one. The release workflow
    /// appends a checksum line; a body with several digests (one per asset) is narrowed by
    /// looking for the line that names this asset.
    /// </summary>
    private static string? Sha256For(string? body, string assetName)
    {
        if (string.IsNullOrEmpty(body)) return null;

        var matches = Sha256Pattern.Matches(body);
        if (matches.Count == 0) return null;
        if (matches.Count == 1) return matches[0].Value.ToLowerInvariant();

        foreach (string line in body.Split('\n'))
        {
            if (line.IndexOf(assetName, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var m = Sha256Pattern.Match(line);
            if (m.Success) return m.Value.ToLowerInvariant();
        }

        return null; // ambiguous — better no check than the wrong one
    }

    /// <summary>
    /// Stage 1 hand-over: copies this exe to %TEMP%\RobloxAccountManagerUpdate\Updater.exe and starts
    /// it with the --apply-update contract. Returns true when the updater launched — the caller
    /// should then shut the application down so the exe file lock is released.
    /// </summary>
    public static bool BeginUpdate(UpdateInfo info)
    {
        try
        {
            // Single-file publish: Assembly.Location is empty; ProcessPath is the real exe path.
            string mainExe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine the running executable path.");

            Directory.CreateDirectory(UpdateTempDir);
            string updaterExe = Path.Combine(UpdateTempDir, "Updater.exe");
            File.Copy(mainExe, updaterExe, overwrite: true);

            var s = SettingsService.Current;

            // Contract: --apply-update "<mainExe>" <mainPid> "<url>" "<version>" <size> "<sha256|->" <verify> <keepBackup>
            // The trailing verification arguments were added in v1.7.0; the updater treats every
            // one of them as optional so a half-updated pair of binaries still works.
            var psi = new ProcessStartInfo(updaterExe) { UseShellExecute = false };
            psi.ArgumentList.Add("--apply-update");
            psi.ArgumentList.Add(mainExe);
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add(info.DownloadUrl);
            psi.ArgumentList.Add(info.VersionText);
            psi.ArgumentList.Add(info.Size.ToString());
            psi.ArgumentList.Add(string.IsNullOrEmpty(info.Sha256) ? "-" : info.Sha256);
            psi.ArgumentList.Add(s.VerifyUpdateDownload ? "1" : "0");
            psi.ArgumentList.Add(s.KeepUpdateBackup ? "1" : "0");
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateService] Failed to launch updater: {ex.Message}");
            return false;
        }
    }

    // ---- rollback -------------------------------------------------------------------------

    /// <summary>Path of the backup the last update left behind, next to the running exe.</summary>
    public static string? BackupPath
    {
        get
        {
            string? exe = Environment.ProcessPath;
            return string.IsNullOrEmpty(exe) ? null : exe + BackupSuffix;
        }
    }

    private static bool? _hasBackup;

    /// <summary>
    /// True when a previous build is on disk and can be restored. Memoised: the settings buttons
    /// bind their CanExecute to this, and WPF re-asks on every mouse move and focus change — an
    /// uncached File.Exists there would hit the disk continuously. The backup only appears during
    /// an update (which restarts the app) or disappears via <see cref="DiscardBackup"/>.
    /// </summary>
    public static bool HasBackup => _hasBackup ??= BackupPath is { } p && File.Exists(p);

    /// <summary>Version of the kept backup, e.g. "v1.6.0"; null when there is none / it is unreadable.</summary>
    public static string? BackupVersionText
    {
        get
        {
            try
            {
                if (BackupPath is not { } p || !File.Exists(p)) return null;
                var vi = FileVersionInfo.GetVersionInfo(p);
                string? raw = vi.FileVersion ?? vi.ProductVersion;
                if (string.IsNullOrWhiteSpace(raw)) return null;
                var m = VersionPattern.Match(raw);
                return m.Success && Version.TryParse(m.Value, out var v) ? $"v{Normalize(v).ToString(3)}" : null;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Puts the kept backup back in place. Reuses the same two-process dance as an update —
    /// a running exe cannot overwrite itself — but with a local file instead of a download.
    /// Returns true when the restorer launched; the caller must then shut down.
    /// </summary>
    public static bool BeginRollback()
    {
        try
        {
            if (BackupPath is not { } backup || !File.Exists(backup)) return false;
            string mainExe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine the running executable path.");

            Directory.CreateDirectory(UpdateTempDir);
            string updaterExe = Path.Combine(UpdateTempDir, "Updater.exe");
            File.Copy(mainExe, updaterExe, overwrite: true);

            // Same contract, with a local file:// source and no verification: the backup is a
            // build that already ran on this machine, and there is no checksum published for it.
            var psi = new ProcessStartInfo(updaterExe) { UseShellExecute = false };
            psi.ArgumentList.Add("--apply-update");
            psi.ArgumentList.Add(mainExe);
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add(new Uri(backup).AbsoluteUri);
            psi.ArgumentList.Add(BackupVersionText ?? "the previous version");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("0");   // the backup IS the rollback target; don't back it up again
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateService] Rollback failed to start: {ex.Message}");
            return false;
        }
    }

    /// <summary>Deletes the kept backup. Returns true when nothing is left on disk afterwards.</summary>
    public static bool DiscardBackup()
    {
        try
        {
            if (BackupPath is not { } p) return true;
            if (File.Exists(p)) File.Delete(p);
            _hasBackup = false;
            return true;
        }
        catch { _hasBackup = null; return false; }
    }

    /// <summary>Display version of the running build, e.g. "v1.3.0".</summary>
    public static string CurrentVersionText => $"v{CurrentVersion().ToString(3)}";

    /// <summary>
    /// The repository's whole CHANGELOG.md, compiled into the exe (see the EmbeddedResource in the
    /// csproj). Always available — no network, no GitHub release required. Null only when the build
    /// was produced without the file.
    /// </summary>
    public static string? EmbeddedChangelog => _changelog ??= LoadEmbeddedChangelog();

    private static string? _changelog;

    private static string? LoadEmbeddedChangelog()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Changelog.md");
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            string text = reader.ReadToEnd();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch { return null; }
    }

    /// <summary>Matches a version heading, e.g. "## v1.6.0 — 2026-08-27" or "## 1.6.0".</summary>
    private static readonly Regex SectionHeading =
        new(@"^##[ \t]+v?(\d+(?:\.\d+){0,3})\b", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// This build's own section of the changelog. One file holds every release, so the section for
    /// the running version is sliced out at runtime: everything from its "## vX.Y.Z" heading up to
    /// the next one. Returns null when no section matches, which is the signal to fall back to the
    /// GitHub release.
    /// </summary>
    public static string? EmbeddedReleaseNotes => ChangelogSection(CurrentVersion());

    /// <summary>The changelog section for a specific version, or null when it has none.</summary>
    public static string? ChangelogSection(Version version)
    {
        string? all = EmbeddedChangelog;
        if (all == null) return null;

        var matches = SectionHeading.Matches(all);
        for (int i = 0; i < matches.Count; i++)
        {
            if (!Version.TryParse(matches[i].Groups[1].Value, out var parsed)) continue;
            if (Normalize(parsed) != Normalize(version)) continue;

            int start = matches[i].Index;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : all.Length;
            string section = all[start..end].TrimEnd();

            // Trim the "---" rule the next section is separated by; it renders as stray text.
            if (section.EndsWith("---", StringComparison.Ordinal))
                section = section[..^3].TrimEnd();

            return section.Length == 0 ? null : section;
        }

        return null;
    }

    /// <summary>
    /// Notes for the running version, preferring the copy baked into this build and falling back
    /// to the GitHub release. That order matters: the embedded copy is guaranteed to describe the
    /// build the user is actually running, whereas the GitHub lookup fails whenever the release
    /// is unpublished, the machine is offline, or the API rate limit is spent.
    /// </summary>
    public static async Task<(string Notes, string PageUrl)?> GetNotesForCurrentVersionAsync()
    {
        string version = CurrentVersionText;
        string pageUrl = $"https://github.com/{Repo}/releases/tag/{version}";

        if (EmbeddedReleaseNotes is { } local) return (local, pageUrl);
        return await GetReleaseNotesAsync(version).ConfigureAwait(false);
    }

    /// <summary>
    /// Release notes + page URL for a given display version ("v1.3.0"), or null when the tag
    /// doesn't exist / the network is down. Used by the post-update "What's new" window.
    /// </summary>
    public static async Task<(string Notes, string PageUrl)?> GetReleaseNotesAsync(string versionText)
    {
        try
        {
            using var http = NewClient(TimeSpan.FromSeconds(15));
            using var resp = await http
                .GetAsync($"https://api.github.com/repos/{Repo}/releases/tags/{versionText}")
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = doc.RootElement;
            string notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            string url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            return (notes, url);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateService] Release notes fetch failed: {ex.Message}");
            return null;
        }
    }

    private static Version? ParseVersion(string? tagName, string? releaseName)
    {
        foreach (string? source in new[] { tagName, releaseName })
        {
            if (string.IsNullOrEmpty(source)) continue;
            var m = VersionPattern.Match(source);
            if (m.Success && Version.TryParse(m.Value, out var v)) return Normalize(v);
        }
        return null;
    }

    /// <summary>Missing components count as 0 so "1.1" compares as "1.1.0.0".</summary>
    private static Version Normalize(Version v)
        => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static Version CurrentVersion()
        => Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0));
}
