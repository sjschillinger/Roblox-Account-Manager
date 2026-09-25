using System.Collections.Concurrent;
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
///
/// Two optional extras run through the same rejoin path: a client whose account stops showing as
/// in game (disconnected, but the process is still open) is restarted, and every client can be
/// restarted after a set time.
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

    /// <summary>
    /// Accounts whose auto-rejoin is paused right now because they hit the crash-loop cap. The pause
    /// lifts by itself once the oldest rejoin leaves the window, or at once through <see cref="Resume"/>.
    /// </summary>
    public static IReadOnlyList<long> PausedAccounts()
    {
        lock (_rejoins)
        {
            var cutoff = DateTime.UtcNow - RejoinWindow;
            return _rejoins.Where(kv => kv.Value.Count(t => t >= cutoff) >= MaxRejoins).Select(kv => kv.Key).ToList();
        }
    }

    /// <summary>Lifts the crash-loop pause for an account (the user checked the game and wants rejoins back).</summary>
    public static void Resume(long userId)
    {
        lock (_rejoins) _rejoins.Remove(userId);
        DiagnosticsService.Log("watchdog", $"Auto-rejoin resumed for user {userId}");
    }

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
        if (!_hooked)
        {
            ProcessRegistry.Exited += OnClientExited;
            PresenceService.PresenceUpdated += CheckDisconnects;
            _hooked = true;
        }
    }

    public static void Apply()
    {
        var s = SettingsService.Current;
        if (s.WatchdogEnabled) Start(Math.Max(5, s.WatchdogCheckSeconds));
        else Stop();

        lock (_gate)
        {
            if (s.RestartClientsEnabled)
                _restartTimer ??= new System.Threading.Timer(_ => RestartTick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            else { _restartTimer?.Dispose(); _restartTimer = null; }
        }
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

    // ---------------------------------------------------------------- disconnects

    /// <summary>Per client: when its account was last seen in game, and when the manager first saw the client.</summary>
    private sealed class PresenceTrack
    {
        public DateTime FirstSeenUtc = DateTime.UtcNow;
        public DateTime? LastInGameUtc;
        public bool Handled;
    }

    private static readonly ConcurrentDictionary<int, PresenceTrack> _presence = new();

    /// <summary>
    /// Runs after every presence poll. A client whose account was in game and has not been for
    /// <see cref="AppSettings.DisconnectMinutes"/> — or that never got into a game within a few
    /// minutes — is closed and rejoined like a crash (same retries, same crash-loop cap). Accounts
    /// with more than one client are skipped: presence can't tell which of them dropped.
    /// </summary>
    private static void CheckDisconnects()
    {
        try
        {
            var s = SettingsService.Current;
            if (!s.WatchdogEnabled || !s.RejoinOnDisconnect || !s.ShowPresence) { _presence.Clear(); return; }

            var clients = ProcessRegistry.All.Where(t => !t.IsExternal && t.UserId > 0).ToList();
            foreach (int pid in _presence.Keys.Except(clients.Select(t => t.Pid)).ToList()) _presence.TryRemove(pid, out _);

            var now = DateTime.UtcNow;
            var limit = TimeSpan.FromMinutes(s.DisconnectMinutes);
            foreach (var group in clients.GroupBy(t => t.UserId).Where(g => g.Count() == 1))
            {
                var t = group.First();
                if (t.ClosingIntentionally || (t.Target.Kind == JoinKind.Place && t.Target.PlaceId <= 0)) continue;   // home screen
                var acc = _accountLookup?.Invoke(t.UserId);
                if (acc == null || !acc.AutoRejoin) continue;

                var st = _presence.GetOrAdd(t.Pid, _ => new PresenceTrack());
                if (acc.Presence == PresenceStatus.InGame) { st.LastInGameUtc = now; st.Handled = false; continue; }
                if (st.Handled) continue;

                // Never in game yet: give loading (and a slow presence update) at least five minutes.
                var since = st.LastInGameUtc ?? st.FirstSeenUtc;
                var wait = st.LastInGameUtc == null ? TimeSpan.FromMinutes(Math.Max(5, s.DisconnectMinutes)) : limit;
                if (now - since < wait) continue;

                st.Handled = true;
                RecoverDisconnected(t, acc);
            }
        }
        catch (Exception ex) { DiagnosticsService.Warn("watchdog", "Disconnect check failed", ex); }
    }

    private static void RecoverDisconnected(ProcessRegistry.Tracked t, Account acc)
    {
        int claim = TryClaimRejoin(acc.UserId);
        if (claim == 0)
        {
            // Crash-loop cap reached: leave the client as it is; the overview lists the pause.
            DiagnosticsService.Warn("watchdog", $"{t.Alias} is not in game, but auto-rejoin is paused after repeated rejoins");
            return;
        }

        DiagnosticsService.Warn("watchdog", $"{t.Alias} has not been in game for a while (disconnected?); restarting its client");
        if (SettingsService.Current.ToastOnCrash)
            ToastService.Warning(L.T("Toast.ClientClosed.Title"), L.T("Watchdog.Disconnected", t.Alias));

        ProcessRegistry.MarkClosing(t.Pid);   // our own close: the exit must not trigger a second rejoin
        _ = Task.Run(async () =>
        {
            try { InstanceControlService.Close(t.Pid); } catch (Exception ex) { DiagnosticsService.Warn("watchdog", "Closing a disconnected client failed", ex); }
            await RejoinAsync(acc, t, t.Target.ForRejoin(claim));
        });
    }

    // ---------------------------------------------------------------- timed restart

    private static System.Threading.Timer? _restartTimer;
    private static DateTime _lastRestartUtc = DateTime.MinValue;
    private static int _restarting;

    /// <summary>
    /// Restarts the longest-running client once it has been up for <see cref="AppSettings.RestartClientsMinutes"/>.
    /// One client per minute at most, so a batch launched together isn't restarted all at once, and
    /// never while a restart is still in progress or the manager is locked (it couldn't relaunch).
    /// </summary>
    private static void RestartTick()
    {
        try
        {
            var s = SettingsService.Current;
            if (!s.RestartClientsEnabled || LockService.IsLocked) return;
            if (DateTime.UtcNow - _lastRestartUtc < TimeSpan.FromMinutes(1)) return;
            if (Volatile.Read(ref _restarting) != 0) return;

            var limit = TimeSpan.FromMinutes(s.RestartClientsMinutes);
            var due = ProcessRegistry.All
                .Where(t => !t.IsExternal && t.UserId > 0 && !t.ClosingIntentionally && t.Uptime >= limit
                            && !(t.Target.Kind == JoinKind.Place && t.Target.PlaceId <= 0))
                .OrderByDescending(t => t.Uptime)
                .FirstOrDefault();
            if (due == null) return;

            var acc = _accountLookup?.Invoke(due.UserId);
            if (acc == null || string.IsNullOrEmpty(acc.Cookie)) return;

            _lastRestartUtc = DateTime.UtcNow;
            Interlocked.Exchange(ref _restarting, 1);
            _ = RestartAsync(acc, due);
        }
        catch (Exception ex) { DiagnosticsService.Warn("watchdog", "Timed restart check failed", ex); }
    }

    private static async Task RestartAsync(Account acc, ProcessRegistry.Tracked t)
    {
        try
        {
            // A long-lived public server may be gone by now: go back to the place, not that server.
            var target = t.Target.Kind == JoinKind.Server ? t.Target.WithoutServer() : t.Target;
            DiagnosticsService.Log("watchdog", $"Timed restart of {t.Alias} after {(int)t.Uptime.TotalMinutes} min into {target}");

            ProcessRegistry.MarkClosing(t.Pid);   // deliberate: no crash handling
            await Task.Run(() => InstanceControlService.Close(t.Pid));
            await Task.Delay(3000);

            var result = await LauncherService.LaunchAsync(acc, target, t.Profile);
            if (!result.Success)
            {
                DiagnosticsService.Warn("watchdog", $"Timed restart of {t.Alias} could not relaunch: {result.Message}");
                if (SettingsService.Current.ToastOnCrash)
                    ToastService.Warning(L.T("Toast.ClientClosed.Title"), $"{t.Alias}: {result.Message}");
            }
        }
        catch (Exception ex) { DiagnosticsService.Warn("watchdog", $"Timed restart of {t.Alias} failed", ex); }
        finally { Interlocked.Exchange(ref _restarting, 0); }
    }
}
