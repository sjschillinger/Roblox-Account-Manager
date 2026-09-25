using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Polls the process registry for crashed/closed Roblox clients. On exit it notifies
/// via Discord webhook and, when the account has AutoRejoin on, relaunches it into the
/// same destination it was launched into — the same private server, the same followed player.
///
/// Recovery is bounded at every level: a failed relaunch is retried a couple of times, a public
/// server that keeps failing is swapped for any server of the same place, and more than
/// <see cref="MaxRejoins"/> rejoins inside <see cref="RejoinWindow"/> stops auto-rejoin for that
/// account and says so.
/// </summary>
public static class WatchdogService
{
    private static System.Threading.Timer? _timer;
    private static readonly object _gate = new();
    private static Func<long, Account?>? _accountLookup;
    private static bool _hooked;

    // Crash-loop brake: at most MaxRejoins auto-rejoins per account inside RejoinWindow.
    // Without this an instantly-crashing game relaunches forever (launch → crash → launch …).
    private const int MaxRejoins = 3;
    private static readonly TimeSpan RejoinWindow = TimeSpan.FromMinutes(10);
    private static readonly Dictionary<long, Queue<DateTime>> _rejoins = new();

    // Relaunch attempts per rejoin when the launch itself fails (network, Roblox API hiccup).
    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60) };

    private static readonly Dictionary<long, int> _sessionRejoins = new();

    /// <summary>Auto-rejoins for an account since the manager started, for the dashboard.</summary>
    public static int RejoinsFor(long userId)
    {
        lock (_rejoins) return _sessionRejoins.GetValueOrDefault(userId);
    }

    /// <summary>
    /// Claims one rejoin slot for the account. Returns how many rejoins (including this one) fall
    /// inside the window, or 0 when the crash-loop cap is hit.
    /// </summary>
    private static int TryClaimRejoin(long userId)
    {
        lock (_rejoins)
        {
            if (!_rejoins.TryGetValue(userId, out var q)) _rejoins[userId] = q = new();
            var cutoff = DateTime.UtcNow - RejoinWindow;
            while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
            if (q.Count >= MaxRejoins) return 0;
            q.Enqueue(DateTime.UtcNow);
            _sessionRejoins[userId] = _sessionRejoins.GetValueOrDefault(userId) + 1;
            return q.Count;
        }
    }

    /// <summary>Wires the account lookup used for auto-rejoin. Call once at startup.</summary>
    public static void Init(Func<long, Account?> accountLookup)
    {
        _accountLookup = accountLookup;
        if (!_hooked) { ProcessRegistry.Exited += OnClientExited; _hooked = true; }
    }

    public static void Apply()
    {
        var s = SettingsService.Current;
        if (s.WatchdogEnabled) Start(Math.Max(5, s.WatchdogCheckSeconds));
        else Stop();
    }

    private static void Start(int seconds)
    {
        lock (_gate)
        {
            var period = TimeSpan.FromSeconds(seconds);
            if (_timer == null)
                _timer = new System.Threading.Timer(
                    _ => { try { ProcessRegistry.Prune(); } catch { } },   // a throwing Timer callback kills the process
                    null, period, period);
            else
                _timer.Change(period, period);
        }
    }

    public static void Stop()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; }
    }

    private static void OnClientExited(ProcessRegistry.Tracked t)
    {
        // Fires for every tracked-client exit, independent of the watchdog toggle
        // (the Exited hook is wired once in Init). Plugins learn the client is gone.
        try { PluginService.RaiseClosed(t.UserId, t.Alias); } catch { }

        // Adopted clients (started from the website / home screen) have no account behind them:
        // there is no cookie to rejoin with and no alias worth alerting about, so stay quiet
        // rather than posting "External client crashed (place 0)" to Discord.
        if (t.IsExternal || t.UserId == 0) return;

        // Closed on purpose through the manager (close button, "close all", relaunch, scheduler):
        // not a crash, nothing to report and nothing to rejoin.
        if (t.ClosingIntentionally) return;

        var s = SettingsService.Current;
        if (!s.WatchdogEnabled) return;

        var acc = _accountLookup?.Invoke(t.UserId);

        if (s.NotifyOnCrash && WebhookService.Configured)
            WebhookService.Disconnected(t.Alias, acc?.ThumbnailUrl, t.PlaceId);

        if (s.ToastOnCrash)
            ToastService.Warning(L.T("Toast.ClientClosed.Title"), L.T("Toast.ClientClosed.Body", t.Alias));

        if (acc == null || !acc.AutoRejoin) return;

        // Opened on the Roblox home screen ("open app"), not in a game: there is nothing to rejoin.
        if (t.Target.Kind == JoinKind.Place && t.Target.PlaceId <= 0) return;

        int claim = TryClaimRejoin(acc.UserId);
        if (claim == 0)
        {
            // Crash loop: give up instead of relaunching forever.
            DiagnosticsService.Warn("watchdog", $"Auto-rejoin paused for {t.Alias}: {MaxRejoins} rejoins within {(int)RejoinWindow.TotalMinutes} min");
            int mins = (int)RejoinWindow.TotalMinutes;
            if (s.ToastOnCrash)
                ToastService.Warning(L.T("Toast.RejoinPaused.Title"),
                    L.T("Toast.RejoinPaused.Body", t.Alias, MaxRejoins, mins));
            if (WebhookService.Configured)
                WebhookService.ReconnectFailed(t.Alias, acc.ThumbnailUrl, t.PlaceId,
                    $"crash loop: {MaxRejoins} rejoins in {mins} min, giving up");
            return;
        }

        // We treat this exit as a crash and are about to auto-rejoin: tell plugins first.
        try { PluginService.RaiseCrashed(acc, t.PlaceId, t.JobId); } catch { }
        _ = RejoinAsync(acc, t, t.Target.ForRejoin(claim));
    }

    private static async Task RejoinAsync(Account acc, ProcessRegistry.Tracked t, JoinTarget target)
    {
        try
        {
            await Task.Delay(3000); // let the crashed process fully die first
            DiagnosticsService.Log("watchdog", $"Rejoining {t.Alias} into {target}");

            LauncherService.LaunchResult result;
            for (int attempt = 0; ; attempt++)
            {
                if (LockService.IsLocked) return;
                // Relaunched by hand (or by a schedule) while we waited to retry: nothing left to recover.
                if (attempt > 0 && ProcessRegistry.CountFor(acc.UserId) > 0) return;
                result = await LauncherService.LaunchAsync(acc, target, t.Profile);
                if (result.Success || !result.Retryable || attempt >= RetryDelays.Length) break;
                DiagnosticsService.Warn("watchdog", $"Rejoin of {t.Alias} failed, retrying: {result.Message}");
                await Task.Delay(RetryDelays[attempt]);
            }

            bool ok = result.Success;
            if (WebhookService.Configured)
            {
                if (ok) WebhookService.Reconnected(acc, target.PlaceId, target.JobId);
                else WebhookService.ReconnectFailed(t.Alias, acc.ThumbnailUrl, target.PlaceId, result.Message);
            }
            if (!ok && SettingsService.Current.ToastOnCrash)
                ToastService.Warning(L.T("Toast.ClientClosed.Title"), $"{t.Alias}: {result.Message}");
        }
        catch (Exception ex) { DiagnosticsService.Warn("watchdog", $"Rejoin of {t.Alias} failed", ex); }
    }
}
