using System.Collections.Concurrent;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Keeps every account with auto-rejoin on running in the game it was launched into. One
/// <see cref="RecoverySlot"/> per account holds its state; a pass every few seconds looks at the
/// account's client and does at most one thing for it: wait, close and relaunch it, or relaunch it.
///
/// Clients are recognised by the tracker id and launch time on their command line
/// (<see cref="ClientIdentity"/>), so a slow launch, several launches at once or a manager restart
/// never lose an account or mix two up. Every launch of an account — the user's, a preset's, a
/// schedule's, a restart's — reports to <see cref="Launched"/>, so they all share one state instead
/// of racing each other.
///
/// Recovery never gives up while rejoin is on, but it is paced: after a few relaunches in a row each
/// further one waits longer (<see cref="RejoinBackoff"/>), and a public server that keeps failing is
/// swapped for any server of the same place. Every action is logged with its cause.
///
/// Optional extras: a client can be restarted after a set time or once its memory has grown to a
/// multiple of its settled size.
/// </summary>
public static class WatchdogService
{
    private static System.Threading.Timer? _timer;
    private static readonly object _gate = new();
    private static Func<long, Account?>? _accountLookup;
    private static bool _hooked;
    private static int _passing;

    // One slot per account the manager keeps running, and how many relaunches each had this session.
    private static readonly object _rejoinGate = new();
    private static readonly Dictionary<long, RecoverySlot> _slots = new();
    private static readonly Dictionary<long, int> _sessionRejoins = new();

    /// <summary>Relaunches waiting out a backoff delay of a minute or more (the overview lists them with a Stop button).</summary>
    public static IReadOnlyList<(long UserId, DateTime DueUtc)> PendingRejoins()
    {
        lock (_rejoinGate)
            return _slots.Values.Where(s => s.RelaunchAtUtc is { } due && due - DateTime.UtcNow > TimeSpan.FromSeconds(30))
                                .Select(s => (s.UserId, s.RelaunchAtUtc!.Value)).ToList();
    }

    /// <summary>Stops keeping the account running (the user pressed Stop on its waiting relaunch).</summary>
    public static void CancelRejoin(long userId)
    {
        lock (_rejoinGate) _slots.Remove(userId);
        DiagnosticsService.Log("watchdog", $"Stopped keeping user {userId} running (Stop pressed)");
    }

    /// <summary>Auto-rejoins for an account since the manager started, for the dashboard.</summary>
    public static int RejoinsFor(long userId)
    {
        lock (_rejoinGate) return _sessionRejoins.GetValueOrDefault(userId);
    }

    /// <summary>
    /// Every launch of an account reports here once its client has been started, whoever launched it.
    /// The slot then watches for that launch's client (and cancels any relaunch it was waiting to make).
    /// </summary>
    public static void Launched(Account acc, JoinTarget target, string? profile, long launchTime)
    {
        if (!acc.AutoRejoin || !Keepable(target)) return;
        lock (_rejoinGate)
        {
            if (!_slots.TryGetValue(acc.UserId, out var slot)) _slots[acc.UserId] = slot = new RecoverySlot(acc.UserId, target, profile);
            slot.Launched(target, profile, launchTime, DateTime.UtcNow);
        }
    }

    /// <summary>The home screen ("open app") has no game to go back to.</summary>
    private static bool Keepable(JoinTarget t) => !(t.Kind == JoinKind.Place && t.PlaceId <= 0);

    /// <summary>Wires the account lookup used for auto-rejoin. Call once at startup.</summary>
    public static void Init(Func<long, Account?> accountLookup)
    {
        _accountLookup = accountLookup;
        if (!_hooked)
        {
            ProcessRegistry.Exited += OnClientExited;
            RamMonitorService.Sampled += OnRamSampled;
            _hooked = true;
        }
    }

    private static string? _loggedSettings;

    public static void Apply()
    {
        var s = SettingsService.Current;
        string summary = EventLog.RecoverySettings();
        if (_loggedSettings != null && summary != _loggedSettings) EventLog.Write("SETTINGS", "recovery settings changed", ("now", summary));
        _loggedSettings = summary;
        if (s.WatchdogEnabled) Start(5);
        else Stop();

        ClientLogWatcher.Apply();

        // Always armed: an account can have its own restart settings with the global ones off. A tick
        // with nothing due costs next to nothing.
        lock (_gate)
            _restartTimer ??= new System.Threading.Timer(_ => RestartTick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private static void Start(int seconds)
    {
        lock (_gate)
        {
            var period = TimeSpan.FromSeconds(seconds);
            if (_timer == null)
                _timer = new System.Threading.Timer(_ => Pass(), null, period, period);
            else
                _timer.Change(period, period);
        }
    }

    public static void Stop()
    {
        lock (_gate) { _timer?.Dispose(); _timer = null; }
    }

    // ---------------------------------------------------------------- exits

    private static void OnClientExited(ProcessRegistry.Tracked t)
    {
        // Fires for every tracked-client exit, independent of the watchdog toggle
        // (the Exited hook is wired once in Init). Plugins learn the client is gone.
        try { PluginService.RaiseClosed(t.UserId, t.Alias); } catch { }

        // Adopted clients (started from the website / home screen) have no account behind them:
        // there is no cookie to rejoin with and no alias worth alerting about, so stay quiet
        // rather than posting "External client crashed (place 0)" to Discord.
        if (t.IsExternal || t.UserId == 0) return;

        RecoverySlot? slot;
        bool watched;
        lock (_rejoinGate)
        {
            _slots.TryGetValue(t.UserId, out slot);
            // The watched client, or the client of the launch it is still waiting for (not latched yet).
            watched = slot != null && (slot.Pid == t.Pid
                                       || (slot.Pid == 0 && slot.PendingLaunch != 0 && t.LaunchTime == slot.PendingLaunch));
        }

        // Closed on purpose through the manager (close button, "close all", the scheduler): the user
        // no longer wants it running. The watchdog's own closes never get here as "watched" — it lets
        // go of a client before closing it — and a relaunch (restart, launching again) reports a new launch.
        if (t.ClosingIntentionally)
        {
            // The watchdog's own closes (a reset of Roblox, a stray) never stop an account being kept running.
            if (watched && t.CloseCause?.StartsWith("watchdog:", StringComparison.Ordinal) != true)
            {
                lock (_rejoinGate) if (_slots.TryGetValue(t.UserId, out var s) && s == slot) _slots.Remove(t.UserId);
                DiagnosticsService.Log("watchdog", $"{t.Alias}'s client pid {t.Pid} was closed through the manager; no longer keeping it running");
                EventLog.Write("UNWATCHED", EventLog.Who(t.Alias, t.UserId), ("pid", t.Pid), ("why", $"closed on purpose ({t.CloseCause})"));
            }
            return;
        }

        var acc = _accountLookup?.Invoke(t.UserId);
        var s0 = SettingsService.Current;
        if (!s0.WatchdogEnabled) return;
        DiagnosticsService.Log("watchdog", $"{t.Alias}'s client pid {t.Pid} exited after {(int)t.Uptime.TotalMinutes} min {t.Uptime.Seconds} s");

        if (t.Uptime < TimeSpan.FromMinutes(1))
        {
            // The process handed over to another one of the same launch (it carries the same tracker id):
            // follow that one. Then report what is running: something keeping new clients from starting
            // (a client stuck without a window, one holding Roblox's single-instance lock) shows up here.
            foreach (var adopted in ProcessRegistry.AdoptOwn()) LogRecognised(adopted);
            var successor = ProcessRegistry.ForUser(t.UserId).FirstOrDefault(o => o.Pid != t.Pid && !o.IsExternal && !o.ClosingIntentionally);
            if (successor != null)
            {
                if (watched) lock (_rejoinGate) slot!.Found(successor.Pid);
                DiagnosticsService.Log("watchdog", $"{t.Alias}'s client pid {t.Pid} handed over to pid {successor.Pid}; following that one");
                return;
            }

            // Nothing of the launch is left: it died, or handed its ticket to a stray. Say what is running
            // and what Roblox logged, clear strays, and count it against Roblox's health.
            string? ownLog = ClientLogWatcher.LogPathOf(t.Pid);
            bool reachedGame = ClientLogWatcher.StateOf(t.Pid)?.EverInGame == true;
            _ = Task.Run(() =>
            {
                try
                {
                    ReportInventory("a client exited within a minute of starting");
                    CloseStrays(StrayCheck.AfterFastExit);
                    if (ClientLogWatcher.CrashClues(t.StartTimeLocal, ownLog) is { } clues)
                    {
                        DiagnosticsService.Warn("watchdog", $"{t.Alias}'s client pid {t.Pid} — what Roblox logged: {clues}");
                        EventLog.Write("CRASH-CLUES", EventLog.Who(t.Alias, t.UserId), ("pid", t.Pid), ("clues", clues));
                    }
                }
                catch { }
            });
            if (!reachedGame) LaunchFailed($"{t.Alias}'s client exited after {(int)t.Uptime.TotalSeconds} s");
        }

        if (s0.NotifyOnCrash && WebhookService.Configured)
            WebhookService.Disconnected(t.Alias, acc?.ThumbnailUrl, t.PlaceId);
        if (s0.ToastOnCrash)
            ToastService.Warning(L.T("Toast.ClientClosed.Title"), L.T("Toast.ClientClosed.Body", t.Alias));

        if (acc == null || !acc.AutoRejoin || !Keepable(t.Target)) return;

        TimeSpan delay;
        int streak;
        lock (_rejoinGate)
        {
            if (slot == null) _slots[t.UserId] = slot = new RecoverySlot(t.UserId, t.Target, t.Profile);
            // An older client of the account ended while a newer one is the one being watched, or this
            // exit was already noticed by a pass (which booked the relaunch).
            else if (!watched && (slot.Pid != 0 || slot.Launching || slot.RelaunchAtUtc != null)) return;
            delay = slot.Lost(DateTime.UtcNow);
            streak = slot.Streak;
            _sessionRejoins[t.UserId] = _sessionRejoins.GetValueOrDefault(t.UserId) + 1;
        }
        Booked(t.Alias, acc, t.PlaceId, "its client exited", streak, delay);

        // We treat this exit as a crash and are about to auto-rejoin: tell plugins first.
        try { PluginService.RaiseCrashed(acc, t.PlaceId, t.JobId); } catch { }
    }

    /// <summary>Logs a booked relaunch, and tells the user when it is being slowed down (the streak went past the free ones).</summary>
    private static void Booked(string alias, Account acc, long placeId, string why, int streak, TimeSpan delay)
    {
        string when = delay < TimeSpan.FromMinutes(1) ? $"{(int)delay.TotalSeconds} s" : $"{(int)Math.Round(delay.TotalMinutes)} min";
        DiagnosticsService.Log("watchdog", $"{alias}: {why}; relaunching in {when} (relaunch {streak} in a row)");
        lock (_rejoinGate) if (_slots.TryGetValue(acc.UserId, out var slot)) slot.Why = why;
        EventLog.Write("RECOVERY", EventLog.Who(alias, acc.UserId), ("why", why), ("relaunchIn", EventLine.Duration(delay)), ("inARow", streak));
        if (delay < TimeSpan.FromMinutes(1)) return;

        int mins = (int)Math.Round(delay.TotalMinutes);
        DiagnosticsService.Warn("watchdog", $"{alias} needed {streak - 1} relaunches in a row; waiting {mins} min before the next");
        if (SettingsService.Current.ToastOnCrash)
            ToastService.Warning(L.T("Toast.RejoinPaused.Title"), L.T("Toast.RejoinPaused.Body", alias, streak - 1, mins));
        if (WebhookService.Configured)
            WebhookService.ReconnectFailed(alias, acc.ThumbnailUrl, placeId, $"{streak - 1} rejoins in a row, next try in {mins} min");
    }

    private static void LogRecognised(ProcessRegistry.Tracked t)
        => DiagnosticsService.Log("watchdog", $"Recognised {t.Alias}'s client pid {t.Pid} by its tracker id (running {(int)t.Uptime.TotalMinutes} min, {t.Target})");

    // ---------------------------------------------------------------- the pass

    /// <summary>
    /// Every few seconds: drop exited clients, recognise the accounts' own clients, clear stuck strays,
    /// then let every account's slot decide what its client needs.
    /// </summary>
    private static void Pass()
    {
        if (Interlocked.CompareExchange(ref _passing, 1, 0) != 0) return;
        try
        {
            ProcessRegistry.Prune();
            foreach (var t in ProcessRegistry.AdoptOwn()) LogRecognised(t);
            var tree = ProcessTree.Snapshot();
            WatchUpdate(tree);
            CloseStrays(StrayCheck.Pass, tree);
            Supervise();
        }
        catch (Exception ex) { DiagnosticsService.Warn("watchdog", "Watchdog pass failed", ex); }   // a throwing Timer callback kills the process
        finally { Interlocked.Exchange(ref _passing, 0); }
    }

    private static void Supervise()
    {
        if (Volatile.Read(ref _repairing) != 0) return;   // a repair is closing clients on purpose: nothing to judge meanwhile
        var s = SettingsService.Current;
        var now = DateTime.UtcNow;
        var clients = ProcessRegistry.All.Where(t => !t.IsExternal && t.UserId > 0).ToList();

        List<RecoverySlot> slots;
        lock (_rejoinGate)
        {
            // A client running without a slot (it outlived a manager restart): keep it running from here on.
            foreach (var g in clients.Where(t => !t.ClosingIntentionally && Keepable(t.Target)).GroupBy(t => t.UserId))
                if (!_slots.ContainsKey(g.Key) && _accountLookup?.Invoke(g.Key) is { AutoRejoin: true })
                {
                    var newest = g.OrderByDescending(t => t.StartTimeLocal).First();
                    var slot = new RecoverySlot(g.Key, newest.Target, newest.Profile);
                    slot.Found(newest.Pid);
                    _slots[g.Key] = slot;
                }
            // Accounts removed, or with rejoin turned off since: no longer kept running.
            foreach (var id in _slots.Keys.Where(id => _accountLookup?.Invoke(id) is not { AutoRejoin: true }).ToList())
                _slots.Remove(id);
            slots = _slots.Values.ToList();
        }

        bool launching;
        lock (_rejoinGate) launching = slots.Any(x => x.Launching);
        // Nothing is launched while Roblox updates itself or is being reset: the launch would be lost.
        bool held = LockService.IsLocked || UpdateHoldsLaunches || Volatile.Read(ref _repairing) != 0;
        var failures = new List<string>();
        bool reachedGame = false;

        foreach (var slot in slots)
        {
            if (_accountLookup?.Invoke(slot.UserId) is not { } acc) continue;

            var mine = clients.Where(t => t.UserId == slot.UserId).ToList();
            var live = mine.Where(t => !t.ClosingIntentionally).OrderByDescending(t => t.StartTimeLocal).FirstOrDefault();
            if (s.RejoinOnDisconnect) CloseKickedDuplicates(mine, live);

            // A client being closed counts as alive for a couple of minutes; one that outlives that would
            // not close, and must not keep the account from ever being relaunched.
            bool anyProcess = mine.Any(t => !t.ClosingIntentionally || now - t.ClosingSinceUtc < ClosingLimit);
            var obs = live == null
                ? new ClientObservation(0, TimeSpan.Zero, false, null, anyProcess)
                : new ClientObservation(live.Pid, live.Uptime, ProcessRegistry.WindowHandle(live.Pid) != IntPtr.Zero,
                                        ClientLogWatcher.StateOf(live.Pid), true, ClientLogWatcher.Working);
            // A fresh client in its game: Roblox starts clients fine.
            if (live != null && obs.Log is { Phase: ClientPhase.InGame } && live.Uptime < TimeSpan.FromMinutes(10)) reachedGame = true;

            RecoveryDecision d;
            int streak;
            TimeSpan delay = TimeSpan.Zero;
            lock (_rejoinGate)
            {
                bool waitingForClient = slot.PendingLaunch != 0 && obs.Pid == 0;
                d = slot.Tick(obs, now, s.RejoinOnDisconnect, AccountSettings.DisconnectWait(acc), canLaunch: !launching && !held);
                if (d.Action == RecoveryAction.Booked && waitingForClient) failures.Add($"{acc.DisplayNameOrUser}: {d.Why}");
                if (d.Action == RecoveryAction.Launch) { slot.BeginLaunch(now); launching = true; }
                if (d.Action == RecoveryAction.Booked) _sessionRejoins[slot.UserId] = _sessionRejoins.GetValueOrDefault(slot.UserId) + 1;
                if (d.Action == RecoveryAction.Booked && slot.RelaunchAtUtc is { } due) delay = due - now;
                streak = slot.Streak;
            }

            switch (d.Action)
            {
                case RecoveryAction.Booked:
                    Booked(acc.DisplayNameOrUser, acc, slot.Target.PlaceId, d.Why, streak, delay);
                    break;
                case RecoveryAction.Launch:
                    _ = RelaunchAsync(acc, slot, d.Target ?? slot.Target);
                    break;
                case RecoveryAction.Replace when live != null:
                    Replace(acc, slot, live, d.Why);
                    break;
                case RecoveryAction.CloseAndStop when live != null:
                    lock (_rejoinGate) if (_slots.TryGetValue(slot.UserId, out var cur) && cur == slot) _slots.Remove(slot.UserId);
                    DiagnosticsService.Warn("watchdog", $"{acc.DisplayNameOrUser}: {d.Why}; closing its client and no longer keeping it running");
                    EventLog.Write("UNWATCHED", EventLog.Who(acc), ("pid", live.Pid), ("why", d.Why));
                    string stopCause = $"watchdog: {d.Why}";
                    _ = Task.Run(() => { try { InstanceControlService.Close(live.Pid, stopCause); } catch { } });
                    break;
            }
        }

        if (reachedGame)
        {
            bool recovered;
            lock (_rejoinGate) { recovered = _health.FailuresInARow > 0 || _health.ResetsInARow > 0; _health.Succeeded(); }
            if (recovered)
                DiagnosticsService.Log("watchdog", "A launch reached its game again: Roblox starts clients"
                    + (RobloxSingletonService.GentleWithNewClients ? " (new clients are being left alone while they start)" : ""));
        }
        foreach (var why in failures) LaunchFailed(why);
    }

    private static readonly TimeSpan ClosingLimit = TimeSpan.FromMinutes(2);

    /// <summary>
    /// An older client of an account that was kicked because a newer one joined ("joined from another
    /// device") is closed, never relaunched: the newer client is the account's.
    /// </summary>
    private static void CloseKickedDuplicates(List<ProcessRegistry.Tracked> mine, ProcessRegistry.Tracked? live)
    {
        foreach (var old in mine.Where(t => t != live && !t.ClosingIntentionally))
        {
            if (ClientLogWatcher.StateOf(old.Pid) is not { Phase: ClientPhase.Out }) continue;
            DiagnosticsService.Warn("watchdog", $"{old.Alias}'s older client pid {old.Pid} is out of its game while pid {live?.Pid} runs; closing it");
            string cause = $"watchdog: older client kicked while pid {live?.Pid} runs";
            ProcessRegistry.MarkClosing(old.Pid, cause);
            _ = Task.Run(() => { try { InstanceControlService.Close(old.Pid, cause); } catch { } });
        }
    }

    /// <summary>Closes a client that is out of its game or stuck, then lets its slot relaunch it once it is gone.</summary>
    private static void Replace(Account acc, RecoverySlot slot, ProcessRegistry.Tracked live, string why)
    {
        TimeSpan delay;
        int streak;
        // Let go of the client before closing it: its exit is then not mistaken for the user closing it.
        string cause = $"watchdog: {why}";
        ProcessRegistry.MarkClosing(live.Pid, cause);
        lock (_rejoinGate)
        {
            delay = slot.Lost(DateTime.UtcNow);
            streak = slot.Streak;
            _sessionRejoins[slot.UserId] = _sessionRejoins.GetValueOrDefault(slot.UserId) + 1;
        }
        DiagnosticsService.Warn("watchdog", $"{acc.DisplayNameOrUser}'s client pid {live.Pid}: {why}; closing it");
        Booked(acc.DisplayNameOrUser, acc, live.PlaceId, why, streak, delay);
        if (SettingsService.Current.ToastOnCrash)
            ToastService.Warning(L.T("Toast.ClientClosed.Title"), L.T("Watchdog.Disconnected", acc.DisplayNameOrUser));

        _ = Task.Run(() =>
        {
            bool closed = false;
            try { closed = InstanceControlService.Close(live.Pid, cause); }
            catch (Exception ex) { DiagnosticsService.Warn("watchdog", "Closing a client failed", ex); }
            if (closed || !ProcessAlive(live.Pid)) return;

            // A second client would get this one kicked with "joined from another device": keep
            // watching this one and try again on a later pass.
            DiagnosticsService.Warn("watchdog", $"Could not close {acc.DisplayNameOrUser}'s client pid {live.Pid}; not launching a second one");
            live.ClosingIntentionally = false;
            lock (_rejoinGate)
            {
                slot.Found(live.Pid);
                slot.HoldReplace(DateTime.UtcNow + delay);
            }
        });
    }

    private static async Task RelaunchAsync(Account acc, RecoverySlot slot, JoinTarget target)
    {
        try
        {
            DiagnosticsService.Log("watchdog", $"Relaunching {acc.DisplayNameOrUser} into {target}");

            // Checked again once the launch's turn comes: meanwhile someone may have launched the account,
            // its late client may have turned up, or Stop may have been pressed.
            bool Proceed()
            {
                bool alive = ProcessRegistry.ForUser(acc.UserId)   // outside the lock: it prunes
                    .Any(t => !t.IsExternal && (!t.ClosingIntentionally || DateTime.UtcNow - t.ClosingSinceUtc < ClosingLimit));
                lock (_rejoinGate)
                    return !alive && slot.Issuing && _slots.TryGetValue(acc.UserId, out var cur) && cur == slot;
            }

            string why;
            lock (_rejoinGate) why = slot.Why;
            var result = await LauncherService.LaunchAsync(acc, target, $"watchdog: relaunch after {(why.Length > 0 ? why : "losing its client")}", slot.Profile, Proceed);   // reports back through Launched
            if (result.Success)
            {
                if (WebhookService.Configured) WebhookService.Reconnected(acc, target.PlaceId, target.JobId);
                return;
            }
            if (result.Abandoned)
            {
                DiagnosticsService.Log("watchdog", $"Relaunch of {acc.DisplayNameOrUser} dropped: the account was launched, found running or stopped meanwhile");
                lock (_rejoinGate) if (slot.Issuing && slot.Pid == 0 && slot.PendingLaunch == 0) slot.LaunchFailed(DateTime.UtcNow);
                return;
            }

            if (result.CookieProblem)
            {
                lock (_rejoinGate) if (_slots.TryGetValue(acc.UserId, out var cur) && cur == slot) _slots.Remove(acc.UserId);
                DiagnosticsService.Warn("watchdog", $"Relaunch of {acc.DisplayNameOrUser} is not possible ({result.Message}); no longer keeping it running");
                EventLog.Write("UNWATCHED", EventLog.Who(acc), ("why", $"cannot launch: {result.Message}"));
            }
            else
            {
                TimeSpan delay;
                int streak;
                lock (_rejoinGate) { delay = slot.LaunchFailed(DateTime.UtcNow); streak = slot.Streak; }
                Booked(acc.DisplayNameOrUser, acc, target.PlaceId, $"the launch failed ({result.Message})", streak, delay);
            }
            if (WebhookService.Configured) WebhookService.ReconnectFailed(acc.DisplayNameOrUser, acc.ThumbnailUrl, target.PlaceId, result.Message);
            if (SettingsService.Current.ToastOnCrash) ToastService.Warning(L.T("Toast.ClientClosed.Title"), $"{acc.DisplayNameOrUser}: {result.Message}");
        }
        catch (Exception ex)
        {
            lock (_rejoinGate) slot.LaunchFailed(DateTime.UtcNow);
            DiagnosticsService.Warn("watchdog", $"Relaunch of {acc.DisplayNameOrUser} failed", ex);
        }
    }

    /// <summary>A running client for the account that isn't one being closed (pruned first, so a dead one never counts).</summary>
    private static bool HasLiveClient(long userId)
        => ProcessRegistry.ForUser(userId).Any(t => !t.IsExternal && !t.ClosingIntentionally);

    private static bool ProcessAlive(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    // ---------------------------------------------------------------- strays

    /// <summary>Closes the windowless strays that would take the next launch's ticket. Called right before every launch.</summary>
    public static void ClearBeforeLaunch()
    {
        try { CloseStrays(StrayCheck.BeforeLaunch); }
        catch (Exception ex) { DiagnosticsService.Warn("watchdog", "Clearing stray Roblox processes before a launch failed", ex); }
    }

    /// <summary>
    /// Closes Roblox processes no account of ours owns that <see cref="StrayPolicy"/> says must go: above
    /// all the windowless one a client starts as it leaves its game, which would otherwise take the next
    /// launch and show as "started outside the manager".
    /// </summary>
    private static void CloseStrays(StrayCheck check, IReadOnlyList<ProcessTree.Entry>? tree = null)
    {
        tree ??= ProcessTree.Snapshot();
        var parentOf = new Dictionary<int, int>();
        foreach (var e in tree) parentOf[e.Pid] = e.ParentPid;
        var ours = ProcessRegistry.All.Where(t => !t.IsExternal && t.UserId > 0).Select(t => t.Pid).ToHashSet();

        var procs = System.Diagnostics.Process.GetProcessesByName("RobloxPlayerBeta");
        try
        {
            foreach (var p in procs)
            {
                try
                {
                    if (p.HasExited || ours.Contains(p.Id)) continue;
                    DateTime start = p.StartTime;
                    // An account's own client not registered yet (its launch is still claiming it) is never a stray.
                    if (ProcessCommandLine.IdentityOf(p.Id, start) is { } id && LauncherService.AccountByTracker(id.Tracker) != null) continue;

                    var age = DateTime.Now - start;
                    bool spawned = parentOf.TryGetValue(p.Id, out int parent) && ProcessRegistry.IsOrWasOwn(parent);
                    if (!StrayPolicy.ShouldClose(spawned, p.MainWindowHandle != IntPtr.Zero, age, check)) continue;

                    string why = spawned ? $"started by our client pid {parent}, no window" : $"{(int)age.TotalSeconds} s old, no window";
                    string when = check switch
                    {
                        StrayCheck.BeforeLaunch => "before a launch",
                        StrayCheck.AfterFastExit => "after a launch was lost",
                        _ => "on a pass",
                    };
                    DiagnosticsService.Warn("watchdog", $"Closing stray Roblox process pid {p.Id} ({why}, not one of the manager's clients; {when}); command line: {ProcessCommandLine.ForLog(p.Id) ?? "unreadable"}");
                    ProcessRegistry.MarkClosing(p.Id, $"watchdog: stray Roblox process ({why})");
                    p.Kill();
                }
                catch { /* exited meanwhile, or not ours to touch */ }
            }
        }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    /// <summary>Logs every Roblox client process and whose it is, for telling what keeps new clients from starting.</summary>
    private static void ReportInventory(string why)
    {
        var ours = ProcessRegistry.All.Where(t => !t.IsExternal && t.UserId > 0).ToDictionary(t => t.Pid);
        var procs = System.Diagnostics.Process.GetProcessesByName("RobloxPlayerBeta");
        try
        {
            var rows = procs.Select(p =>
            {
                try
                {
                    string who = ours.TryGetValue(p.Id, out var t) ? t.Alias
                               : ProcessCommandLine.IdentityOf(p.Id, p.StartTime) is { } id && LauncherService.AccountByTracker(id.Tracker) is { } a ? a.DisplayNameOrUser + " (not tracked)"
                               : "unknown";
                    return $"{p.Id}={who}{(p.MainWindowHandle != IntPtr.Zero ? "" : " no window")}";
                }
                catch { return $"{p.Id}=?"; }
            });
            string list = string.Join(", ", rows);
            DiagnosticsService.Log("watchdog", $"{procs.Length} Roblox client process(es) running: {list}; multi-instance: {RobloxSingletonService.StatusText}");
            EventLog.Write("INVENTORY", $"{procs.Length} Roblox process(es)", ("clients", list), ("multiInstance", RobloxSingletonService.StatusText), ("why", why));
        }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    // ---------------------------------------------------------------- Roblox updates

    private static DateTime? _updateSinceUtc;

    /// <summary>An installer running longer than this is stuck, not updating: launches go ahead (and a repair may close it).</summary>
    private static readonly TimeSpan UpdateLimit = TimeSpan.FromMinutes(10);

    /// <summary>True while Roblox's installer is updating Roblox (and has not run for longer than <see cref="UpdateLimit"/>).</summary>
    private static bool UpdateHoldsLaunches => _updateSinceUtc is { } since && DateTime.UtcNow - since < UpdateLimit;

    private static bool InstallerRunning(IReadOnlyList<ProcessTree.Entry> tree)
        => tree.Any(e => string.Equals(e.Name, "RobloxPlayerInstaller", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// While Roblox updates itself, no client is launched and the manager lets go of Roblox's
    /// single-instance lock: a launch then would be lost or crash, and the installer must find Roblox closed.
    /// </summary>
    private static void WatchUpdate(IReadOnlyList<ProcessTree.Entry> tree)
    {
        bool running = InstallerRunning(tree);
        if (running && _updateSinceUtc == null)
        {
            _updateSinceUtc = DateTime.UtcNow;
            DiagnosticsService.Log("watchdog", "Roblox is updating: holding launches until it is done");
            EventLog.Write("ROBLOX", "updating", ("action", "launches held, single-instance lock released"));
            if (SettingsService.Current.EnableMultiInstance) RobloxSingletonService.EnsureMutex(false);
        }
        else if (!running && _updateSinceUtc is { } since)
        {
            _updateSinceUtc = null;
            DiagnosticsService.Log("watchdog", $"Roblox finished updating after {(int)(DateTime.UtcNow - since).TotalSeconds} s");
            EventLog.Write("ROBLOX", "updated", ("took", EventLine.Duration(DateTime.UtcNow - since)));
            RobloxSingletonService.Apply();
        }
    }

    /// <summary>A launch waits (up to <see cref="UpdateLimit"/>) for an update of Roblox that is under way.</summary>
    public static async Task WaitForRobloxUpdateAsync()
    {
        var until = DateTime.UtcNow + UpdateLimit;
        bool logged = false;
        while (DateTime.UtcNow < until && InstallerRunning(ProcessTree.Snapshot()))
        {
            if (!logged) { DiagnosticsService.Log("launcher", "Roblox is updating; the launch waits until it is done"); logged = true; }
            await Task.Delay(2000);
        }
    }

    // ---------------------------------------------------------------- Roblox's health

    // Launches that fail in a row, whichever account: when Roblox as a whole stops starting clients.
    private static readonly LaunchHealth _health = new();
    private static int _repairing;

    /// <summary>A launch produced no client, or its client died before reaching its game.</summary>
    private static void LaunchFailed(string why)
    {
        if (!SettingsService.Current.WatchdogEnabled) return;
        RobloxRepair repair;
        int inARow;
        lock (_rejoinGate) { repair = _health.Failed(DateTime.UtcNow); inARow = _health.FailuresInARow; }
        if (repair == RobloxRepair.None) return;
        if (Interlocked.Exchange(ref _repairing, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try
            {
                if (repair == RobloxRepair.Cleanup) Cleanup(why);
                else await ResetRobloxAsync(why);
            }
            catch (Exception ex) { DiagnosticsService.Warn("watchdog", "Repairing Roblox failed", ex); }
            finally { Interlocked.Exchange(ref _repairing, 0); }
        });
    }

    /// <summary>Second launch in a row lost: close strays and helpers left behind, and take Roblox's single-instance lock afresh.</summary>
    private static void Cleanup(string why)
    {
        DiagnosticsService.Warn("watchdog", $"Two launches in a row failed (latest: {why}); closing stray Roblox processes and resetting the single-instance lock");
        EventLog.Write("REPAIR", "cleanup", ("why", why));
        ReportInventory("launches keep failing");
        CheckMemory();
        CloseStrays(StrayCheck.AfterFastExit);
        CloseLeftovers(everything: false);
        RobloxSingletonService.Stop();
        Thread.Sleep(1000);
        RobloxSingletonService.Apply();
    }

    /// <summary>
    /// Launches keep failing after a cleanup: do for Roblox what a reboot does. Every client the manager
    /// keeps running is closed with every other Roblox process, the single-instance lock is let go until
    /// all of them are gone, and the accounts are then launched again one at a time.
    /// </summary>
    private static async Task ResetRobloxAsync(string why)
    {
        int resets;
        lock (_rejoinGate) resets = _health.ResetsInARow;
        DiagnosticsService.Warn("watchdog", $"Launches keep failing (latest: {why}); resetting Roblox: closing every Roblox process and starting the accounts again one at a time (reset {resets} in a row)");
        EventLog.Write("REPAIR", "full reset", ("why", why), ("resetsInARow", resets));
        ReportInventory("before resetting Roblox");
        CheckMemory();
        if (SettingsService.Current.ToastOnCrash)
            ToastService.Warning(L.T("Toast.RobloxReset.Title"), L.T("Toast.RobloxReset.Body"));

        // A reset already happened and launches still die: stop touching clients while they start up, in
        // case unlocking multi-instance that early is what kills them. Kept on for the rest of the session.
        if (resets >= 2 && !RobloxSingletonService.GentleWithNewClients && SettingsService.Current.CloseSingletonEvent)
        {
            RobloxSingletonService.GentleWithNewClients = true;
            DiagnosticsService.Warn("watchdog", "Resetting Roblox did not help; from now on new clients are left alone for their first 20 s (multi-instance is unlocked just before the next launch instead)");
            EventLog.Write("REPAIR", "gentle with new clients", ("why", "a reset of Roblox did not stop launches failing"));
        }

        const string cause = "watchdog: resetting Roblox (launches kept failing)";
        var now = DateTime.UtcNow;
        var closing = new List<int>();
        lock (_rejoinGate)
        {
            foreach (var slot in _slots.Values) slot.Reset(now, TimeSpan.FromSeconds(20));
            // Only the accounts kept running: one switched off for recovery would not come back.
            foreach (var t in ProcessRegistry.All.Where(t => !t.IsExternal && t.UserId > 0 && _slots.ContainsKey(t.UserId)))
            {
                ProcessRegistry.MarkClosing(t.Pid, cause);
                closing.Add(t.Pid);
            }
        }

        RobloxSingletonService.Stop();
        await Task.WhenAll(closing.Select(pid => Task.Run(() => { try { InstanceControlService.Close(pid, cause); } catch { } })));
        CloseLeftovers(everything: true);

        // Wait for every Roblox process of ours to be gone, so the lock and the handover objects die with them.
        for (int i = 0; i < 20 && RobloxProcessesLeft(); i++) await Task.Delay(1000);
        if (RobloxProcessesLeft())
        {
            CloseLeftovers(everything: true);
            await Task.Delay(3000);
            ReportUnclosable();
        }
        await Task.Delay(2000);
        RobloxSingletonService.Apply();
        DiagnosticsService.Log("watchdog", "Roblox reset done; relaunching the accounts one at a time");
    }

    private static DateTime _lastMemoryWarningUtc = DateTime.MinValue;

    /// <summary>Logs how much memory Windows has left; when it is nearly used up, says so and what is using it.</summary>
    private static void CheckMemory()
    {
        if (MemoryStatus.Read() is not { } m) return;
        if (!m.Low) { DiagnosticsService.Log("watchdog", $"Memory: {m}"); return; }

        string top = MemoryStatus.TopUsers();
        DiagnosticsService.Warn("watchdog", $"Windows is nearly out of memory ({m}); new clients can't start until memory is freed. Using the most: {top}");
        EventLog.Write("MEMORY", "nearly out of memory", ("status", m.ToString()), ("top", top));
        if (DateTime.UtcNow - _lastMemoryWarningUtc < TimeSpan.FromMinutes(30)) return;
        _lastMemoryWarningUtc = DateTime.UtcNow;
        ToastService.Warning(L.T("Toast.LowMemory.Title"), L.T("Toast.LowMemory.Body", top));
    }

    /// <summary>
    /// After a reset: Roblox processes Windows would not close. These are what only a restart of the PC
    /// clears (a client started as administrator, or one stuck while ending), so they are named exactly.
    /// </summary>
    private static void ReportUnclosable()
    {
        var keep = KeptOutPids();
        var rows = new List<string>();
        bool denied = false;
        foreach (var e in ProcessTree.Snapshot().Where(e => IsRobloxHelper(e.Name) || (IsClient(e.Name) && !keep.Contains(e.Pid))))
        {
            string why;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(e.Pid);
                try { p.Kill(); why = p.WaitForExit(2000) ? "" : "did not end when killed (stuck while ending)"; }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5) { why = "Windows denied access (started as administrator?)"; denied = true; }
                catch (Exception ex) { why = ex.Message; }
                if (why.Length == 0) continue;
                why = $"{(int)(DateTime.Now - p.StartTime).TotalMinutes} min old, {why}";
            }
            catch { continue; }   // gone meanwhile
            rows.Add($"{e.Name} pid {e.Pid} ({why})");
        }
        if (rows.Count == 0) return;

        string list = string.Join("; ", rows);
        DiagnosticsService.Warn("watchdog", $"Roblox processes Windows would not close: {list}. New clients may not start until they are gone.");
        EventLog.Write("REPAIR", "could not close", ("processes", list));
        string hint = denied && !RobloxSingletonService.IsElevated ? L.T("Toast.RobloxStuck.Admin") : L.T("Toast.RobloxStuck.Restart");
        ToastService.Warning(L.T("Toast.RobloxStuck.Title"), $"{string.Join(", ", rows.Take(3))}. {hint}");
    }

    /// <summary>Roblox processes other than clients of accounts that aren't kept running.</summary>
    private static bool RobloxProcessesLeft()
    {
        var keep = KeptOutPids();
        return ProcessTree.Snapshot().Any(e => IsRobloxHelper(e.Name) || (IsClient(e.Name) && !keep.Contains(e.Pid)));
    }

    /// <summary>Clients of accounts switched off for recovery: a reset leaves them alone.</summary>
    private static HashSet<int> KeptOutPids()
    {
        lock (_rejoinGate)
            return ProcessRegistry.All.Where(t => !t.IsExternal && t.UserId > 0 && !_slots.ContainsKey(t.UserId)).Select(t => t.Pid).ToHashSet();
    }

    private static bool IsClient(string name) => string.Equals(name, "RobloxPlayerBeta", StringComparison.OrdinalIgnoreCase);

    private static bool IsRobloxHelper(string name)
        => string.Equals(name, "RobloxCrashHandler", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "RobloxPlayerLauncher", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Closes Roblox's helpers left behind: crash handlers whose client is gone, a launcher or installer
    /// stuck for minutes. With <paramref name="everything"/> (a reset), every Roblox process except the
    /// clients of accounts that aren't kept running.
    /// </summary>
    private static void CloseLeftovers(bool everything)
    {
        var tree = ProcessTree.Snapshot();
        var alive = tree.Select(e => e.Pid).ToHashSet();
        var keep = KeptOutPids();
        foreach (var e in tree)
        {
            bool installer = string.Equals(e.Name, "RobloxPlayerInstaller", StringComparison.OrdinalIgnoreCase);
            if (!IsRobloxHelper(e.Name) && !installer && !IsClient(e.Name)) continue;
            if (keep.Contains(e.Pid) || keep.Contains(e.ParentPid)) continue;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(e.Pid);
                var age = DateTime.Now - p.StartTime;
                string? why =
                    installer ? (age >= TimeSpan.FromMinutes(15) ? $"installer stuck for {(int)age.TotalMinutes} min" : null)
                    : everything ? "resetting Roblox"
                    : string.Equals(e.Name, "RobloxCrashHandler", StringComparison.OrdinalIgnoreCase) && !alive.Contains(e.ParentPid) ? "crash handler of a client that is gone"
                    : string.Equals(e.Name, "RobloxPlayerLauncher", StringComparison.OrdinalIgnoreCase) && age >= TimeSpan.FromMinutes(2) ? $"launcher stuck for {(int)age.TotalMinutes} min"
                    : null;
                if (why == null) continue;
                DiagnosticsService.Warn("watchdog", $"Closing {e.Name} pid {e.Pid} ({why})");
                if (IsClient(e.Name)) ProcessRegistry.MarkClosing(e.Pid, $"watchdog: {why}");
                p.Kill();
            }
            catch { /* gone, or not ours to touch */ }
        }
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
            if (!AccountSettings.AnyRamGrowth) { _ram.Clear(); _ramDue.Clear(); return; }

            var live = ProcessRegistry.All.Where(t => !t.IsExternal && t.UserId > 0).ToDictionary(t => t.Pid);
            foreach (int pid in _ram.Keys.Where(p => !live.ContainsKey(p)).ToList()) { _ram.TryRemove(pid, out _); _ramDue.TryRemove(pid, out _); }

            foreach (var sample in samples)
            {
                if (!live.TryGetValue(sample.Pid, out var t)) continue;
                if (AccountSettings.RamGrowthFactor(_accountLookup?.Invoke(t.UserId)) is not double factor) continue;
                var growth = _ram.GetOrAdd(sample.Pid, _ => new RamGrowth());
                if (growth.Observe(t.Uptime, sample.PrivateMb, factor) && _ramDue.TryAdd(sample.Pid, sample.PrivateMb))
                    DiagnosticsService.Warn("watchdog", $"{t.Alias} grew from {growth.BaselineMb} MB to {sample.PrivateMb} MB; restarting it");
            }
        }
        catch (Exception ex) { DiagnosticsService.Warn("watchdog", "Memory growth check failed", ex); }
    }

    /// <summary>
    /// Restarts one client that is due: one whose memory has grown past the limit first, otherwise
    /// the longest-running one once it has been up for its restart time (<see cref="AccountSettings.RestartMinutes"/>).
    /// One client per minute at most, so a batch launched together isn't restarted all at once, and
    /// never while a restart is still in progress or the manager is locked (it couldn't relaunch).
    /// </summary>
    private static void RestartTick()
    {
        try
        {
            if (LockService.IsLocked) return;
            if (DateTime.UtcNow - _lastRestartUtc < TimeSpan.FromMinutes(1)) return;
            if (Volatile.Read(ref _restarting) != 0) return;

            // Each client by its account's own restart settings, or the global ones.
            var candidates = ProcessRegistry.All
                .Where(t => !t.IsExternal && t.UserId > 0 && !t.ClosingIntentionally
                            && !(t.Target.Kind == JoinKind.Place && t.Target.PlaceId <= 0))
                .ToList();
            if (candidates.Count == 0) return;
            var due = candidates.FirstOrDefault(t => _ramDue.ContainsKey(t.Pid))
                   ?? candidates.Where(t => AccountSettings.RestartMinutes(_accountLookup?.Invoke(t.UserId)) is int m && t.Uptime >= TimeSpan.FromMinutes(m))
                                .OrderByDescending(t => t.Uptime).FirstOrDefault();
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
    public static async Task<int> RestartAllAsync(IReadOnlyList<ProcessRegistry.Tracked> clients, string cause)
    {
        var jobs = new List<(Account Acc, ProcessRegistry.Tracked T)>();
        foreach (var t in clients.Where(t => !t.IsExternal && t.UserId > 0 && !(t.Target.Kind == JoinKind.Place && t.Target.PlaceId <= 0)))
            if (_accountLookup?.Invoke(t.UserId) is { } acc && !string.IsNullOrEmpty(acc.Cookie)) jobs.Add((acc, t));
        if (jobs.Count == 0) return 0;

        DiagnosticsService.Log("watchdog", $"Scheduled restart of {jobs.Count} client(s)");
        var closed = new List<(Account Acc, ProcessRegistry.Tracked T)>();
        foreach (var j in jobs)
            if (await CloseForRestartAsync(j.T, cause)) closed.Add(j);
        await Task.Delay(3000);

        int launched = 0;
        foreach (var (acc, t) in closed)
        {
            if (LockService.IsLocked) { RestartFailed(acc, t, L.T("Lock.Blocked")); continue; }
            if (HasLiveClient(acc.UserId)) continue;   // relaunched by hand meanwhile
            var result = await LauncherService.LaunchAsync(acc, RestartTarget(t), cause, t.Profile);
            if (!result.Success)
            {
                DiagnosticsService.Warn("watchdog", $"Scheduled restart of {t.Alias} could not relaunch: {result.Message}");
                RestartFailed(acc, t, result.Message);
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

    /// <summary>
    /// Closes a client on purpose for a restart. The account's slot lets go of it first (so the exit
    /// doesn't stop the account being kept running) and waits for the restart to report back. False
    /// (and still watched) when it would not close.
    /// </summary>
    private static async Task<bool> CloseForRestartAsync(ProcessRegistry.Tracked t, string cause)
    {
        RecoverySlot? slot = null;
        if (_accountLookup?.Invoke(t.UserId) is { AutoRejoin: true } && Keepable(t.Target))
            lock (_rejoinGate)
            {
                if (!_slots.TryGetValue(t.UserId, out slot)) _slots[t.UserId] = slot = new RecoverySlot(t.UserId, t.Target, t.Profile);
                slot.Handover(DateTime.UtcNow);
            }

        ProcessRegistry.MarkClosing(t.Pid, cause);   // deliberate: no crash handling
        bool closed = await Task.Run(() => InstanceControlService.Close(t.Pid, cause));
        if (closed || !ProcessAlive(t.Pid)) return true;
        t.ClosingIntentionally = false;
        if (slot != null) lock (_rejoinGate) slot.Found(t.Pid);
        DiagnosticsService.Warn("watchdog", $"Could not close {t.Alias}'s client for a restart; not launching a second one");
        return false;
    }

    /// <summary>A restart closed the client but could not launch it again: the account's slot books a retry.</summary>
    private static void RestartFailed(Account acc, ProcessRegistry.Tracked t, string message)
    {
        TimeSpan delay;
        int streak;
        lock (_rejoinGate)
        {
            if (!_slots.TryGetValue(acc.UserId, out var slot) || !slot.Issuing) return;
            delay = slot.LaunchFailed(DateTime.UtcNow);
            streak = slot.Streak;
        }
        Booked(t.Alias, acc, t.PlaceId, $"the restart could not relaunch it ({message})", streak, delay);
    }

    private static async Task RestartAsync(Account acc, ProcessRegistry.Tracked t)
    {
        try
        {
            var target = RestartTarget(t);
            bool grew = _ramDue.TryRemove(t.Pid, out long mb);
            string why = grew ? $"at {mb} MB" : $"after {(int)t.Uptime.TotalMinutes} min";
            DiagnosticsService.Log("watchdog", $"Restarting {t.Alias} {why} into {target}");

            string cause = grew ? $"memory-growth restart at {mb} MB" : $"timed restart after {(int)t.Uptime.TotalMinutes} min";
            if (!await CloseForRestartAsync(t, cause)) return;
            await Task.Delay(3000);

            var result = await LauncherService.LaunchAsync(acc, target, cause, t.Profile);
            if (!result.Success)
            {
                DiagnosticsService.Warn("watchdog", $"Restart of {t.Alias} could not relaunch: {result.Message}");
                RestartFailed(acc, t, result.Message);
                if (SettingsService.Current.ToastOnCrash)
                    ToastService.Warning(L.T("Toast.ClientClosed.Title"), $"{t.Alias}: {result.Message}");
            }
        }
        catch (Exception ex)
        {
            DiagnosticsService.Warn("watchdog", $"Restart of {t.Alias} failed", ex);
            RestartFailed(acc, t, ex.GetType().Name);
        }
    }
}
