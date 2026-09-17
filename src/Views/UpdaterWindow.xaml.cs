using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.Views;

/// <summary>
/// Portable self-updater, stage 2. This process is a copy of the app running from
/// %TEMP%\RobloxAccountManagerUpdate\Updater.exe (started with --apply-update): it waits for the
/// main app to exit, downloads the new exe, verifies it, swaps it in place — keeping the old
/// build as a backup — then relaunches the app with --post-update so the temp folder is cleaned up.
/// </summary>
public partial class UpdaterWindow : Window
{
    private const int MaxReplaceAttempts = 20;
    private const int MaxDownloadAttempts = 3;
    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MainExitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How often the progress line is repainted; the read loop ticks far faster than the eye.</summary>
    private static readonly TimeSpan ProgressRefresh = TimeSpan.FromMilliseconds(120);

    private readonly string _mainExePath;
    private readonly int _mainPid;
    private readonly string _downloadUrl;
    private readonly string _versionText;
    private readonly string _tempDir;

    /// <summary>Size GitHub reported for the asset; 0 when unknown (then it is not checked).</summary>
    private readonly long _expectedSize;

    /// <summary>SHA-256 published in the release body; null when the release didn't carry one.</summary>
    private readonly string? _expectedSha256;

    private readonly bool _verify;
    private readonly bool _keepBackup;

    private CancellationTokenSource? _downloadCts;
    private bool _running;

    /// <summary>Set once the old exe has been moved aside, so a failure can put it back.</summary>
    private string? _backupPath;

    public UpdaterWindow(string mainExePath, string mainPid, string downloadUrl, string versionText,
                         string? expectedSize = null, string? sha256 = null,
                         string? verify = null, string? keepBackup = null)
    {
        InitializeComponent();

        _mainExePath = mainExePath;
        _mainPid = int.TryParse(mainPid, out int pid) ? pid : 0;
        _downloadUrl = downloadUrl;
        _versionText = versionText;

        // Every verification argument is optional: an older build handing over to this one (or the
        // reverse, mid-rollout) must still produce a working update rather than a crash on startup.
        _expectedSize = long.TryParse(expectedSize, out long size) && size > 0 ? size : 0;
        _expectedSha256 = string.IsNullOrWhiteSpace(sha256) || sha256 == "-" ? null : sha256.ToLowerInvariant();
        _verify = verify != "0";
        _keepBackup = keepBackup != "0";

        // We live in the update temp dir; download next to ourselves so --post-update removes both.
        string? procDir = Path.GetDirectoryName(Environment.ProcessPath);
        _tempDir = string.IsNullOrEmpty(procDir)
            ? Path.Combine(Path.GetTempPath(), "RobloxAccountManagerUpdate")
            : procDir;

        HeaderText.Text = L.T("Updater.Title", _versionText);
        StatusText.Text = L.T("Updater.Preparing");
        CancelButton.Content = L.T("Common.Cancel");
        RetryButton.Content = L.T("Updater.Retry");
        StartAnywayButton.Content = L.T("Updater.StartAnyway");

        Loaded += async (_, _) => await RunAsync();
    }

    /// <summary>
    /// Where the new build is written. The version is part of the name so a partial download of a
    /// different release is never resumed into this one (that produced a file whose checksum could
    /// never match, and every retry failed the same way).
    /// </summary>
    private string DownloadPath
    {
        get
        {
            string safe = new string(_versionText.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-').ToArray());
            return Path.Combine(_tempDir, $"update-{(safe.Length == 0 ? "latest" : safe)}.exe");
        }
    }

    // ---- update pipeline ----
    private async Task RunAsync()
    {
        if (_running) return;
        _running = true;

        RetryButton.Visibility = Visibility.Collapsed;
        StartAnywayButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        CancelButton.IsEnabled = true;

        try
        {
            // 1) Wait for the main app to exit so its exe file lock is released.
            SetStatus(L.T("Updater.WaitingForApp"));
            await WaitForMainExitAsync();

            // 2) Fetch the new exe next to this updater, resuming a partial file if one is there.
            SetStatus(L.T("Updater.Downloading", _versionText));
            string downloadPath = DownloadPath;
            _downloadCts = new CancellationTokenSource();
            await FetchAsync(downloadPath, _downloadCts.Token);

            // 3) Make sure what arrived is actually the application before it replaces one.
            SetStatus(L.T("Updater.Verifying"));
            SetDetail("");
            await VerifyAsync(downloadPath, _downloadCts.Token);

            // 4) Swap the exe in place — point of no return, so cancel is disabled here.
            CancelButton.IsEnabled = false;
            SetStatus(L.T("Updater.Installing", _versionText));
            await ReplaceMainExeAsync(downloadPath);

            // 5) Relaunch the updated app; it cleans this temp folder up in the background.
            SetStatus(L.T("Updater.Starting"));
            var psi = new ProcessStartInfo(_mainExePath) { UseShellExecute = false };
            psi.ArgumentList.Add("--post-update");
            psi.ArgumentList.Add(_tempDir);
            Process.Start(psi);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            // User cancelled the download: put the old app back on screen. The partial file stays
            // on disk on purpose — a retry resumes from where this one stopped.
            StartOldAppAndExit();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Updater] Update failed: {ex}");
            RestoreBackupIfNeeded();
            SetStatus(L.T("Updater.Failed", Shorten(ex.Message)));
            SetDetail(L.T("Updater.Untouched"));
            Progress.Value = 0;
            PercentText.Text = "";
            CancelButton.Visibility = Visibility.Collapsed;
            RetryButton.Visibility = Visibility.Visible;
            StartAnywayButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _downloadCts?.Dispose();
            _downloadCts = null;
            _running = false;
        }
    }

    private async Task WaitForMainExitAsync()
    {
        if (_mainPid <= 0) return;
        try
        {
            using var proc = Process.GetProcessById(_mainPid);
            using var timeout = new CancellationTokenSource(MainExitTimeout);
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { /* still alive after 30s — try the swap anyway */ }
        }
        catch (ArgumentException) { }        // already exited
        catch (InvalidOperationException) { } // exited between lookup and wait
    }

    // ---- download ----

    /// <summary>
    /// Puts the new build at <paramref name="destination"/>. A rollback hands us a local file
    /// instead of a URL, in which case there is nothing to download.
    /// </summary>
    private async Task FetchAsync(string destination, CancellationToken ct)
    {
        if (Uri.TryCreate(_downloadUrl, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            // A rollback may only restore the backup this app itself kept next to the exe.
            string expected = Path.GetFullPath(_mainExePath + UpdateService.BackupSuffix);
            if (!string.Equals(Path.GetFullPath(uri.LocalPath), expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException(L.T("Updater.UntrustedSource"));

            SetStatus(L.T("Updater.Restoring", _versionText));
            File.Copy(uri.LocalPath, destination, overwrite: true);
            Progress.Value = 100;
            PercentText.Text = "100%";
            return;
        }

        // Only GitHub release downloads are installed; the URL arrives on the command line.
        if (!UpdateService.IsTrustedDownloadUrl(_downloadUrl))
            throw new IOException(L.T("Updater.UntrustedSource"));

        Exception? last = null;
        for (int attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
        {
            try
            {
                await DownloadAsync(destination, ct);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                // A dropped connection on a 58 MB download used to mean starting over. The bytes
                // already on disk are still good, so keep them and let the next attempt resume.
                last = ex;
                if (attempt == MaxDownloadAttempts) break;
                SetStatus(L.T("Updater.Retrying", attempt + 1, MaxDownloadAttempts));
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
        throw new IOException(L.T("Updater.DownloadFailed", MaxDownloadAttempts), last);
    }

    private async Task DownloadAsync(string destination, CancellationToken ct)
    {
        // No overall HttpClient timeout: large file on a slow line; cancel comes from the token.
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RobloxAccountManager");

        // Resume point: whatever a previous attempt already wrote. Guard against a stale file
        // that is somehow larger than the asset — that can only be leftover junk.
        long resumeFrom = 0;
        var existing = new FileInfo(destination);
        if (existing.Exists && existing.Length > 0
            && (_expectedSize == 0 || existing.Length < _expectedSize))
            resumeFrom = existing.Length;

        using var req = new HttpRequestMessage(HttpMethod.Get, _downloadUrl);
        if (resumeFrom > 0) req.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        // The server may ignore the Range header (or the file changed underneath us) and answer
        // 200 with the whole body. Starting from zero is then the only correct thing to do.
        bool resuming = resp.StatusCode == HttpStatusCode.PartialContent;
        if (resumeFrom > 0 && !resuming)
        {
            if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // Already have every byte the server has — nothing left to fetch.
                if (_expectedSize > 0 && existing.Length >= _expectedSize) { ReportProgress(existing.Length, _expectedSize, null); return; }
                File.Delete(destination);   // otherwise the file is junk; start clean
            }
            resumeFrom = 0;
        }
        resp.EnsureSuccessStatusCode();

        long total = resumeFrom + (resp.Content.Headers.ContentLength ?? -1);
        if (resp.Content.Headers.ContentLength == null) total = _expectedSize;

        await using var source = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(destination,
            resuming ? FileMode.Append : FileMode.Create, FileAccess.Write,
            FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long done = resumeFrom;
        var clock = Stopwatch.StartNew();
        long clockBase = resumeFrom;
        var lastPaint = TimeSpan.Zero;
        int read;

        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;

            if (clock.Elapsed - lastPaint < ProgressRefresh) continue;
            lastPaint = clock.Elapsed;
            double bytesPerSecond = clock.Elapsed.TotalSeconds > 0.5
                ? (done - clockBase) / clock.Elapsed.TotalSeconds
                : 0;
            ReportProgress(done, total, bytesPerSecond);
        }

        ReportProgress(done, total, null);
        if (done == 0) throw new IOException(L.T("Updater.Empty"));
    }

    private void ReportProgress(long done, long total, double? bytesPerSecond)
    {
        if (total > 0)
        {
            int pct = (int)Math.Min(100, done * 100 / total);
            Progress.Value = pct;
            PercentText.Text = $"{pct}%";
            SetDetail(L.T("Updater.Progress", Mb(done), Mb(total)) + Rate(bytesPerSecond) + Eta(total - done, bytesPerSecond));
        }
        else
        {
            PercentText.Text = Mb(done);
            SetDetail(Rate(bytesPerSecond).TrimStart(',', ' '));
        }
    }

    private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):0.0} MB";

    private static string Rate(double? bytesPerSecond)
        => bytesPerSecond is > 0 ? $", {bytesPerSecond.Value / (1024.0 * 1024.0):0.0} MB/s" : "";

    private static string Eta(long remaining, double? bytesPerSecond)
    {
        if (bytesPerSecond is not > 0 || remaining <= 0) return "";
        var left = TimeSpan.FromSeconds(remaining / bytesPerSecond.Value);
        string time = left.TotalMinutes >= 1 ? $"{(int)left.TotalMinutes}m {left.Seconds}s" : $"{Math.Max(1, left.Seconds)}s";
        return ", " + L.T("Updater.Left", time);
    }

    // ---- verification ----

    /// <summary>
    /// Refuses to install anything that is not plainly the new application. Without this the file
    /// GitHub happened to return was copied straight over the exe — and an error page, a captive
    /// portal's login HTML or a download truncated by a dropped connection all "succeed" as far as
    /// the transfer is concerned, leaving an unstartable app behind and no way back.
    /// </summary>
    private async Task VerifyAsync(string path, CancellationToken ct)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0) throw new IOException(L.T("Updater.Empty"));

        // A Windows executable begins with "MZ". HTML, JSON and plain text never do.
        await using (var head = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var magic = new byte[2];
            if (await head.ReadAsync(magic.AsMemory(0, 2), ct) != 2 || magic[0] != 0x4D || magic[1] != 0x5A)
                throw Rejected(path, L.T("Updater.NotAnExe"));
        }

        if (!_verify) return;

        if (_expectedSize > 0 && file.Length != _expectedSize)
            throw Rejected(path, L.T("Updater.Incomplete", Mb(file.Length), Mb(_expectedSize)));

        if (_expectedSha256 == null) return;

        string actual = await Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }, ct);

        if (actual != _expectedSha256)
            throw Rejected(path, L.T("Updater.ChecksumMismatch"));
    }

    /// <summary>
    /// Deletes a download that failed verification, so Retry fetches a fresh copy instead of
    /// resuming (and failing again on) the same bad bytes.
    /// </summary>
    private static IOException Rejected(string path, string message)
    {
        try { File.Delete(path); } catch { }
        return new IOException(message);
    }

    // ---- install ----

    /// <summary>
    /// Moves the running build aside, then puts the new one in its place. The old exe used to be
    /// overwritten outright, which left nothing to fall back on: a swap that failed halfway, or a
    /// new build that turned out broken, meant reinstalling by hand. Now it is renamed first, so
    /// a failure here is undone automatically and the user can roll back later from Settings.
    /// </summary>
    private async Task ReplaceMainExeAsync(string downloadPath)
    {
        string backup = _mainExePath + UpdateService.BackupSuffix;

        // Only one backup is kept — the build being replaced right now.
        try { if (File.Exists(backup)) File.Delete(backup); } catch { }

        Exception? last = null;
        for (int attempt = 1; attempt <= MaxReplaceAttempts; attempt++)
        {
            try
            {
                // Move the running build aside exactly once, and leave _backupPath set until the
                // copy has actually succeeded. Restoring between attempts would mean a retry could
                // move a half-written file over the backup and destroy the only good copy of the
                // previous version — the very thing this backup exists to prevent.
                if (_backupPath == null && File.Exists(_mainExePath))
                {
                    File.Move(_mainExePath, backup, overwrite: true);
                    _backupPath = backup;
                }

                // File.Copy streams into the destination, so a failure partway through leaves a
                // truncated executable. Clear any such leftover from an earlier attempt.
                if (File.Exists(_mainExePath)) File.Delete(_mainExePath);

                File.Copy(downloadPath, _mainExePath);

                // The swap held. Keep or drop the backup as the user asked.
                if (!_keepBackup) { try { File.Delete(backup); } catch { } }
                _backupPath = null;   // no longer a pending rollback

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Old exe still locked (straggling process, antivirus scan) — wait and retry.
                last = ex;
                await Task.Delay(ReplaceRetryDelay);
            }
        }

        // Out of attempts: put the previous build back so the user is left with a working app.
        RestoreBackupIfNeeded();
        throw new IOException(L.T("Updater.ReplaceFailed", MaxReplaceAttempts), last);
    }

    /// <summary>
    /// Puts the old exe back when the swap did not complete. Safe to call repeatedly.
    ///
    /// A file sitting at the destination at this point is our own half-written copy — File.Copy
    /// creates the target and streams into it, so an error partway through (a full disk, an
    /// antivirus scanner grabbing the handle) leaves a truncated executable behind. It has to go
    /// before the backup can be moved back: leaving it would strand the user on a binary that
    /// cannot start, and the next retry would then move that truncated file over the backup and
    /// destroy the only good copy of the previous version.
    /// </summary>
    private void RestoreBackupIfNeeded()
    {
        if (_backupPath == null) return;
        try
        {
            if (!File.Exists(_backupPath)) { _backupPath = null; return; }
            if (File.Exists(_mainExePath)) File.Delete(_mainExePath);
            File.Move(_backupPath, _mainExePath);
            _backupPath = null;
        }
        catch (Exception ex)
        {
            // Leave _backupPath set: the previous build is still on disk under its backup name,
            // and saying so beats pretending the restore happened.
            Debug.WriteLine($"[Updater] Restore failed: {ex.Message}");
        }
    }

    // ---- buttons ----
    private void Retry_Click(object sender, RoutedEventArgs e) => _ = RunAsync();

    private void StartAnyway_Click(object sender, RoutedEventArgs e) => StartOldAppAndExit();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // During download: cancel the token and let RunAsync unwind into StartOldAppAndExit.
        if (_downloadCts != null) { _downloadCts.Cancel(); return; }
        StartOldAppAndExit(); // still waiting for the app to close — just bail out
    }

    private void StartOldAppAndExit()
    {
        RestoreBackupIfNeeded();

        // If even the restore failed, the previous build is still on disk under its backup name.
        // Starting that beats leaving the user with nothing at all.
        string exe = File.Exists(_mainExePath)
            ? _mainExePath
            : _backupPath is { } b && File.Exists(b) ? b : _mainExePath;

        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false }); }
        catch { }
        Application.Current.Shutdown();
    }

    // ---- helpers ----
    private void SetStatus(string text) => StatusText.Text = text;

    private void SetDetail(string text) => DetailText.Text = text;

    private static string Shorten(string s) => s.Length <= 120 ? s : s[..117] + "…";

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
    }
}
