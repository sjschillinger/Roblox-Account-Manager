using System.Collections.Concurrent;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Polls the process registry for crashed/closed Roblox clients. On exit it notifies
/// via Discord webhook and, when the account has AutoRejoin on, relaunches it into the
/// same destination it was launched into — the same private server, the same followed player.
///
/// Recovery never gives up while rejoin is on, but it is paced: a failed relaunch is retried a
/// couple of times, a public server that keeps failing is swapped for any server of the same place,
/// and after a few rejoins in a row each further one waits longer (<see cref="RejoinBackoff"/>), so
/// a game that crashes instantly is retried a few times an hour rather than in a tight loop.
///
/// Optional extras: a client whose own log shows it out of its game (disconnected, but the process
/// is still open) is restarted through the same rejoin path, and a client can be restarted after a
/// set time or once its memory has grown to a multiple of its settled size.
/// </summary>
public static class WatchdogService
{
    private static System.Threading.Timer? _timer;
    private static readonly object _gate = new();
    private static Func<long, Account?>? _accountLookup;
    private static bool _hooked;

    // Crash-loop protection by backing off, never by giving up (see RejoinBackoff): per account, how
    // many rejoins happened in a row, and the rejoin currently waiting out its delay.
    private static readonly object _rejoinGate = new();
    private static readonly Dictionary<long, int> _streak = new();
    private static readonly Dictionary<long, (DateTime DueUtc, CancellationTokenSource Cts)> _pending = new();

    // Relaunch attempts per rejoin when the launch itself fails (network, Roblox API hiccup).
    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60) };

    private static readonly Dictionary<long, int> _sessionRejoins = new();

    /// <summary>Rejoins waiting out a backoff delay of a minute or more (the overview lists them with a Stop button).</summary>
    public static IReadOnlyList<(long UserId, DateTime DueUtc)> PendingRejoins()
    {
        lock (_rejoinGate)
            return _pending.Where(kv => kv.Value.DueUtc - DateTime.UtcNow > TimeSpan.FromSeconds(30))
                           .Select(kv => (kv.Key, kv.Value.DueUtc)).ToList();
    }

    /// <summary>Cancels a waiting rejoin and resets the account's streak (the user stepped in).</summary>
    public static void CancelRejoin(long userId)
    {
        lock (_rejoinGate)
        {
            if (_pending.Remove(userId, out var p)) { try { p.Cts.Cancel(); } catch (ObjectDisposedException) { } }
            _streak.Remove(userId);
        }
        DiagnosticsService.Log("watchdog", $"Waiting rejoin cancelled for user {userId}");
    }

    /// <summary>Auto-rejoins for an account since the manager started, for the dashboard.</summary>
    public static int RejoinsFor(long userId)
    {
        lock (_rejoinGate) return _sessionRejoins.GetValueOrDefault(userId);
    }

    /// <summary>
    /// Books one rejoin for the account: returns its place in the streak (1 = first in a row) and how
    /// long to wait before it, plus a token that <see cref="CancelRejoin"/> trips. Null when a rejoin
    /// for this account is already waiting.
    /// </summary>
    private static (int Streak, TimeSpan Delay, CancellationTokenSource Cts)? BookRejoin(long userId, TimeSpan previousUptime)
    {
        lock (_rejoinGate)
        {
            if (_pending.ContainsKey(userId)) return null;
            int streak = RejoinBackoff.StreakAfterExit(_streak.GetValueOrDefault(userId), previousUptime);
            var delay = RejoinBackoff.DelayFor(streak);
            _streak[userId] = streak + 1;
            _sessionRejoins[userId] = _sessionRejoins.GetValueOrDefault(userId) + 1;
            var cts = new CancellationTokenSource();
            _pending[userId] = (DateTime.UtcNow + delay, cts);
            return (streak + 1, delay, cts);
        }
    }

    /// <summary>Wires the account lookup used for auto-rejoin. Call once at startup.</summary>
    public static void Init(Func<long, Account?> accountLookup)
    {
        _accountLookup = accountLookup;
        if (!_hooked)
        {
            ProcessRegistry.Exited += OnClientExited;
            ClientLogWatcher.Updated += CheckDisconnects;
            RamMonitorService.Sampled += OnRamSampled;
            _hooked = true;
        }
    }

    public static void Apply()
    {
        var s = SettingsService.Current;
        if (s.WatchdogEnabled) Start(Math.Max(5, s.WatchdogCheckSeconds));
        else Stop();

        ClientLogWatcher.Apply();

        lock (_gate)
        {
            if (s.RestartClientsEnabled || s.RestartOnRamGrowth)
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
                    _ => { try { ProcessRegistry.Prune(); CheckStuck(); } catch { } },   // a throwing Timer callback kills the process
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

        var acc = _accountLookup?.Invoke(t.UserId);

        // Gone within a minute of starting while a newer client nobody has claimed is running: that
        // one is the real client (the first process handed over to it). Rejoining here launched a
        // second client next to it, seconds after the first rejoin, and one of them was then kicked
        // with "launched from a different device".
        if (acc != null && t.Uptime < TimeSpan.FromMinutes(1))
        {
            DateTime? after = t.StartTimeLocal == default ? null : t.StartTimeLocal;
            int successor = ProcessRegistry.RegisterNewest(acc, t.Target, t.Profile, after);
            if (successor != 0)
            {
                DiagnosticsService.Log("watchdog", $"{t.Alias}'s client pid {t.Pid} exited after {(int)t.Uptime.TotalSeconds}s; following its successor pid {successor} instead of rejoining");
                return;
            }
        }

        var s = SettingsService.Current;
        if (!s.WatchdogEnabled) return;
        DiagnosticsService.Log("watchdog", $"{t.Alias}'s client pid {t.Pid} exited after {(int)t.Uptime.TotalMinutes} min {t.Uptime.Seconds} s");

        // Gone within seconds of starting: usually something else holds Roblox up — a client stuck
        // without a window. Clear those before the rejoin, or every relaunch dies the same way.
        if (t.Uptime < TimeSpan.FromMinutes(1)) _ = Task.Run(() => { try { CloseStrays(StrayAge / 2, report: true); } catch { } });

        if (s.NotifyOnCrash && WebhookService.Configured)
            WebhookService.Disconnected(t.Alias, acc?.ThumbnailUrl, t.PlaceId);

        if (s.ToastOnCrash)
            ToastService.Warning(L.T("Toast.ClientClosed.Title"), L.T("Toast.ClientClosed.Body", t.Alias));

        if (acc == null || !acc.AutoRejoin) return;

        // Opened on the Roblox home screen ("open app"), not in a game: there is nothing to rejoin.
        if (t.Target.Kind == JoinKind.Place && t.Target.PlaceId <= 0) return;

        var booking = BookRejoin(acc.UserId, t.Uptime);
        if (booking is not { } b) return;   // a rejoin for this account is already on its way
        AnnounceBackoff(t.Alias, acc, t.PlaceId, b.Streak, b.Delay);

        // We treat this exit as a crash and are about to auto-rejoin: tell plugins first.
        try { PluginService.RaiseCrashed(acc, t.PlaceId, t.JobId); } catch { }
        _ = RejoinAsync(acc, t, t.Target.ForRejoin(b.Streak), b.Delay, b.Cts);
    }

    /// <summary>A running client for the account that isn't one we are closing ourselves (pruned first, so a dead one never counts).</summary>
    private static bool HasLiveClient(long userId)
        => ProcessRegistry.ForUser(userId).Any(t => !t.IsExternal && !t.ClosingIntentionally);

    /// <summary>Tells the user when a rejoin is being slowed down (the streak went past the free rejoins).</summary>
    private static void AnnounceBackoff(string alias, Account acc, long placeId, int streak, TimeSpan delay)
    {
        if (delay < TimeSpan.FromMinutes(1)) return;
        int mins = (int)Math.Round(delay.TotalMinutes);
        DiagnosticsService.Warn("watchdog", $"{alias} needed {streak - 1} rejoins in a row; waiting {mins} min before the next");
        if (SettingsService.Current.ToastOnCrash)
            ToastService.Warning(L.T("Toast.RejoinPaused.Title"), L.T("Toast.RejoinPaused.Body", alias, streak - 1, mins));
        if (WebhookService.Configured)
            WebhookService.ReconnectFailed(alias, acc.ThumbnailUrl, placeId, $"{streak - 1} rejoins in a row, next try in {mins} min");
    }

    private static async Task RejoinAsync(Account acc, ProcessRegistry.Tracked t, JoinTarget target, TimeSpan delay, CancellationTokenSource cts)
    {
        try
        {
            try { await Task.Delay(delay, cts.Token); }   // at least a few seconds: let the old process fully die
            catch (OperationCanceledException) { return; }
            // Launched by hand (or by a schedule) while we waited: nothing left to recover.
            if (HasLiveClient(acc.UserId)) return;
            DiagnosticsService.Log("watchdog", $"Rejoining {t.Alias} into {target}");

            LauncherService.LaunchResult result;
            for (int attempt = 0; ; attempt++)
            {
                if (LockService.IsLocked) return;
                // Relaunched by hand (or by a schedule) while we waited to retry: nothing left to recover.
                if (attempt > 0 && HasLiveClient(acc.UserId)) return;
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
        finally
        {
            lock (_rejoinGate)
                if (_pending.TryGetValue(acc.UserId, out var p) && p.Cts == cts) _pending.Remove(acc.UserId);
            cts.Dispose();
        }
    }

    // ---------------------------------------------------------------- disconnects

    /// <summary>
    /// Runs after every pass over the client logs. A client whose own log shows it out of a game —
    /// disconnected, kicked, back on the home screen, or a join that never finished — for
    /// <see cref="AppSettings.DisconnectMinutes"/> is closed and, for accounts with rejoin on,
    /// relaunched like a crash (same retries, same backoff). Clients with no matching log are left alone.
    /// </summary>
    private static void CheckDisconnects()
    {
        try
        {
            var s = SettingsService.Current;
            if (!s.WatchdogEnabled || !s.RejoinOnDisconnect) return;

            var now = DateTime.UtcNow;
            var wait = TimeSpan.FromMinutes(s.DisconnectMinutes);
            foreach (var t in ProcessRegistry.All.Where(t => !t.IsExternal && t.UserId > 0 && !t.ClosingIntentionally))
            {
                if (t.Target.Kind == JoinKind.Place && t.Target.PlaceId <= 0) continue;   // opened on the home screen
                var log = ClientLogWatcher.StateOf(t.Pid);
                if (log == null || !log.NeedsRecovery(now, wait)) continue;
                var acc = _accountLookup?.Invoke(t.UserId);
                if (acc == null || !acc.AutoRejoin) continue;
                string why = log.LastDisconnectLine is { } line ? line.Trim() : $"{log.Phase} since {log.PhaseSinceUtc:HH:mm:ss} UTC";
                RecoverDisconnected(t, acc, why, log.Reason == ClientLogState.ReasonJoinedElsewhere);
            }
        }
        catch (Exception ex) { DiagnosticsService.Warn("watchdog", "Disconnect check failed", ex); }
    }

    private static void RecoverDisconnected(ProcessRegistry.Tracked t, Account acc, string why, bool elsewhere)
    {
        if (why.Length > 200) why = why[..200];

        // Another client of this account is running (it is why this one was kicked), or the account
        // joined from somewhere else: close the stale window, but launching again would only kick
        // the other session in turn.
        bool otherClient = ProcessRegistry.ForUser(acc.UserId).Any(o => o.Pid != t.Pid && !o.IsExternal && !o.ClosingIntentionally);
        if (otherClient || elsewhere)
        {
            DiagnosticsService.Warn("watchdog", $"{t.Alias} was disconnected ({why}); {(otherClient ? "another client of this account is running" : "the account joined from elsewhere")}, so only closing it");
            ProcessRegistry.MarkClosing(t.Pid);
            _ = Task.Run(() => { try { InstanceControlService.Close(t.Pid); } catch { } });
            return;
        }

        var booking = BookRejoin(acc.UserId, t.Uptime);
        if (booking is not { } b) return;
        AnnounceBackoff(t.Alias, acc, t.PlaceId, b.Streak, b.Delay);

        DiagnosticsService.Warn("watchdog", $"{t.Alias} is out of its game ({why}); restarting its client");
        if (SettingsService.Current.ToastOnCrash)
            ToastService.Warning(L.T("Toast.ClientClosed.Title"), L.T("Watchdog.Disconnected", t.Alias));

        ProcessRegistry.MarkClosing(t.Pid);   // our own close: the exit must not trigger a second rejoin
        _ = Task.Run(async () =>
        {
            bool closed = false;
            try { closed = InstanceControlService.Close(t.Pid); }
            catch (Exception ex) { DiagnosticsService.Warn("watchdog", "Closing a disconnected client failed", ex); }
            if (!closed && ProcessAlive(t.Pid))
            {
                // Launching now would put two clients on one account; the old one would then be
                // kicked with "joined from another device". Leave it for the next check.
                DiagnosticsService.Warn("watchdog", $"Could not close {t.Alias}'s disconnected client; not launching a second one");
                t.ClosingIntentionally = false;   // still ours to watch
                CancelRejoin(acc.UserId);
                return;
            }
            await RejoinAsync(acc, t, t.Target.ForRejoin(b.Streak), b.Delay, b.Cts);
        });
    }

    private static bool ProcessAlive(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    // ---------------------------------------------------------------- stuck clients

    /// <summary>How long a Roblox process may run without a window before it counts as stuck.</summary>
    private static readonly TimeSpan StrayAge = TimeSpan.FromMinutes(3);
    private static DateTime _lastStuckCheckUtc = DateTime.MinValue;

    /// <summary>
    /// Once a minute: a Roblox client that has had no window for <see cref="StrayAge"/> is stuck. One
    /// of ours is restarted like a disconnected client; one nobody launched (they showed up on the
    /// dashboard as "started outside the manager" with nothing on screen) is closed. A stuck client
    /// can keep every new one from starting — they exit within seconds — until it is gone.
    /// </summary>
    private static void CheckStuck()
    {
        if (DateTime.UtcNow - _lastStuckCheckUtc < TimeSpan.FromMinutes(1)) return;
        _lastStuckCheckUtc = DateTime.UtcNow;
        try
        {
            CloseStrays(StrayAge, report: false);

            if (!SettingsService.Current.RejoinOnDisconnect) return;
            foreach (var t in ProcessRegistry.All.Where(t => !t.IsExternal && t.UserId > 0 && !t.ClosingIntentionally && t.Uptime >= StrayAge))
            {
                if (ProcessRegistry.WindowHandle(t.Pid) != IntPtr.Zero) continue;
                if (t.Target.Kind == JoinKind.Place && t.Target.PlaceId <= 0) continue;
                var acc = _accountLookup?.Invoke(t.UserId);
                if (acc == null || !acc.AutoRejoin) continue;
                RecoverDisconnected(t, acc, $"no window after {(int)t.Uptime.TotalMinutes} min", elsewhere: false);
            }
        }
        catch (Exception ex) { DiagnosticsService.Warn("watchdog", "Stuck client check failed", ex); }
    }

    /// <summary>
    /// Closes Roblox clients no account of ours owns that have run for at least <paramref name="minAge"/>
    /// without a window. A client someone started by hand always has a window by then. With
    /// <paramref name="report"/>, logs what is running, for telling what keeps new clients from starting.
    /// </summary>
    private static void CloseStrays(TimeSpan minAge, bool report)
    {
        var ours = ProcessRegistry.All.Where(t => !t.IsExternal && t.UserId > 0).Select(t => t.Pid).ToHashSet();
        var procs = System.Diagnostics.Process.GetProcessesByName("RobloxPlayerBeta");
        try
        {
            if (report)
            {
                int windowed = procs.Count(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } });
                DiagnosticsService.Log("watchdog", $"{procs.Length} Roblox client process(es) running: {procs.Count(p => ours.Contains(p.Id))} the manager's, {windowed} with a window");
            }
            foreach (var p in procs)
            {
                try
                {
                    if (p.HasExited || ours.Contains(p.Id)) continue;
                    var age = DateTime.Now - p.StartTime;
                    if (age < minAge || p.MainWindowHandle != IntPtr.Zero) continue;
                    DiagnosticsService.Warn("watchdog", $"Closing a stuck Roblox process (pid {p.Id}, {(int)age.TotalMinutes} min old, no window, not one of the manager's clients)");
                    ProcessRegistry.MarkClosing(p.Id);
                    p.Kill();
                }
                catch { /* exited meanwhile, or not ours to touch */ }
            }
        }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    // ---------------------------------------------------------------- timed restart, memory growth, scheduled restart

    private static System.Threading.Timer? _restartTimer;
    private static DateTime _lastRestartUtc = DateTime.MinValue;
    private static int _restarting;

    // Per client: its settled memory size and whether it has grown past the limit (see RamGrowth).
    private static readonly ConcurrentDictionary<int, RamGrowth> _ram = new();
    private static readonly ConcurrentDictionary<int, long> _ramDue = new();

    private static void OnRamSampled(IReadOnlyList<RamMonitorService.Sample> samples)
    {
        try
        {
            var s = SettingsService.Current;
            if (!s.RestartOnRamGrowth) { _ram.Clear(); _ramDue.Clear(); return; }

            var live = ProcessRegistry.All.Where(t => !t.IsExternal && t.UserId > 0).ToDictionary(t => t.Pid);
            foreach (int pid in _ram.Keys.Where(p => !live.ContainsKey(p)).ToList()) { _ram.TryRemove(pid, out _); _ramDue.TryRemove(pid, out _); }

            foreach (var sample in samples)
            {
                if (!live.TryGetValue(sample.Pid, out var t)) continue;
                var growth = _ram.GetOrAdd(sample.Pid, _ => new RamGrowth());
                if (growth.Observe(t.Uptime, sample.PrivateMb, s.RamGrowthFactor) && _ramDue.TryAdd(sample.Pid, sample.PrivateMb))
                    DiagnosticsService.Warn("watchdog", $"{t.Alias} grew from {growth.BaselineMb} MB to {sample.PrivateMb} MB; restarting it");
            }
        }
        catch (Exception ex) { DiagnosticsService.Warn("watchdog", "Memory growth check failed", ex); }
    }

    /// <summary>
    /// Restarts one client that is due: one whose memory has grown past the limit first, otherwise
    /// the longest-running one once it has been up for <see cref="AppSettings.RestartClientsMinutes"/>.
    /// One client per minute at most, so a batch launched together isn't restarted all at once, and
    /// never while a restart is still in progress or the manager is locked (it couldn't relaunch).
    /// </summary>
    private static void RestartTick()
    {
        try
        {
            var s = SettingsService.Current;
            if ((!s.RestartClientsEnabled && !s.RestartOnRamGrowth) || LockService.IsLocked) return;
            if (DateTime.UtcNow - _lastRestartUtc < TimeSpan.FromMinutes(1)) return;
            if (Volatile.Read(ref _restarting) != 0) return;

            var limit = TimeSpan.FromMinutes(s.RestartClientsMinutes);
            var candidates = ProcessRegistry.All
                .Where(t => !t.IsExternal && t.UserId > 0 && !t.ClosingIntentionally
                            && !(t.Target.Kind == JoinKind.Place && t.Target.PlaceId <= 0))
                .ToList();
            var due = candidates.FirstOrDefault(t => s.RestartOnRamGrowth && _ramDue.ContainsKey(t.Pid))
                   ?? (s.RestartClientsEnabled ? candidates.Where(t => t.Uptime >= limit).OrderByDescending(t => t.Uptime).FirstOrDefault() : null);
            if (due == null) return;

            var acc = _accountLookup?.Invoke(due.UserId);
            if (acc == null || string.IsNullOrEmpty(acc.Cookie)) return;

            _lastRestartUtc = DateTime.UtcNow;
            Interlocked.Exchange(ref _restarting, 1);
            _ = Task.Run(async () =>
            {
                try { await RestartAsync(acc, due); }
                finally { Interlocked.Exchange(ref _restarting, 0); }
            });
        }
        catch (Exception ex) { DiagnosticsService.Warn("watchdog", "Timed restart check failed", ex); }
    }

    /// <summary>
    /// Closes every given client, then relaunches them one at a time into the same game, each after
    /// the previous one's client has appeared (the scheduled "Restart" task). Returns how many came back.
    /// </summary>
    public static async Task<int> RestartAllAsync(IReadOnlyList<ProcessRegistry.Tracked> clients)
    {
        var jobs = new List<(Account Acc, ProcessRegistry.Tracked T)>();
        foreach (var t in clients.Where(t => !t.IsExternal && t.UserId > 0 && !(t.Target.Kind == JoinKind.Place && t.Target.PlaceId <= 0)))
            if (_accountLookup?.Invoke(t.UserId) is { } acc && !string.IsNullOrEmpty(acc.Cookie)) jobs.Add((acc, t));
        if (jobs.Count == 0) return 0;

        DiagnosticsService.Log("watchdog", $"Scheduled restart of {jobs.Count} client(s)");
        var closed = new List<(Account Acc, ProcessRegistry.Tracked T)>();
        foreach (var j in jobs)
            if (await CloseForRestartAsync(j.T)) closed.Add(j);
        await Task.Delay(3000);

        int launched = 0;
        foreach (var (acc, t) in closed)
        {
            if (LockService.IsLocked) break;
            if (HasLiveClient(acc.UserId)) continue;   // relaunched by hand meanwhile
            var result = await LauncherService.LaunchAsync(acc, RestartTarget(t), t.Profile);
            if (!result.Success)
            {
                DiagnosticsService.Warn("watchdog", $"Scheduled restart of {t.Alias} could not relaunch: {result.Message}");
                continue;
            }
            launched++;
            await result.Client;                 // one at a time: wait for this client before the next
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
        return launched;
    }

    // A long-lived public server may be gone by now: go back to the place, not that server.
    private static JoinTarget RestartTarget(ProcessRegistry.Tracked t)
        => t.Target.Kind == JoinKind.Server ? t.Target.WithoutServer() : t.Target;

    /// <summary>Closes a client on purpose. False (and still watched) when it would not close.</summary>
    private static async Task<bool> CloseForRestartAsync(ProcessRegistry.Tracked t)
    {
        ProcessRegistry.MarkClosing(t.Pid);   // deliberate: no crash handling
        bool closed = await Task.Run(() => InstanceControlService.Close(t.Pid));
        if (closed || !ProcessAlive(t.Pid)) return true;
        t.ClosingIntentionally = false;
        DiagnosticsService.Warn("watchdog", $"Could not close {t.Alias}'s client for a restart; not launching a second one");
        return false;
    }

    private static async Task RestartAsync(Account acc, ProcessRegistry.Tracked t)
    {
        try
        {
            var target = RestartTarget(t);
            string why = _ramDue.TryRemove(t.Pid, out long mb) ? $"at {mb} MB" : $"after {(int)t.Uptime.TotalMinutes} min";
            DiagnosticsService.Log("watchdog", $"Restarting {t.Alias} {why} into {target}");

            if (!await CloseForRestartAsync(t)) return;
            await Task.Delay(3000);

            var result = await LauncherService.LaunchAsync(acc, target, t.Profile);
            if (!result.Success)
            {
                DiagnosticsService.Warn("watchdog", $"Restart of {t.Alias} could not relaunch: {result.Message}");
                if (SettingsService.Current.ToastOnCrash)
                    ToastService.Warning(L.T("Toast.ClientClosed.Title"), $"{t.Alias}: {result.Message}");
            }
        }
        catch (Exception ex) { DiagnosticsService.Warn("watchdog", $"Restart of {t.Alias} failed", ex); }
    }
}
