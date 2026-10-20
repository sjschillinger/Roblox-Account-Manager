using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Web;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

public static class LauncherService
{
    // Last client spawned per account, so a relaunch can close it. Concurrent: written from delayed
    // attribution tasks, read from the UI thread and the "close all" hotkey.
    private static readonly ConcurrentDictionary<long, int> _lastProcess = new();

    // One client starts at a time, from the moment its ticket is fetched until it has been
    // attributed (or given up on) and has had a few seconds to settle. Several clients starting in
    // the same seconds — every rejoin after a Roblox update closed them all — got each other's
    // logs and windows, and a client could hand its launch to another one.
    private static readonly SemaphoreSlim _launchGate = new(1, 1);
    private static readonly TimeSpan LaunchSettle = TimeSpan.FromSeconds(3);

    // The next launch need not wait longer than this for the previous client: it is recognised by
    // its own tracker id and launch time, so a slow client can't be mistaken for the next one.
    private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(40);

    /// <summary>Releases the launch gate once <paramref name="client"/> is found and has settled, or after <see cref="GateWait"/>.</summary>
    private static void ReleaseAfter(Task<int> client)
        => _ = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAny(client, Task.Delay(GateWait));
                await Task.Delay(LaunchSettle);
            }
            finally { _launchGate.Release(); }
        });

    private static AccountStore? _store;

    public static void Init(AccountStore store)
    {
        _store = store;
        EnsureUniqueTrackers(store);
    }

    /// <summary>
    /// Every account needs its own tracker id: it is how a running client is tied to its account
    /// (<see cref="ClientIdentity"/>). An imported or restored account can carry another's.
    /// </summary>
    private static void EnsureUniqueTrackers(AccountStore store)
    {
        var seen = new HashSet<string>();
        bool changed = false;
        foreach (var acc in store.Accounts)
        {
            if (string.IsNullOrEmpty(acc.BrowserTrackerId)) continue;
            if (seen.Add(acc.BrowserTrackerId)) continue;
            acc.BrowserTrackerId = "";
            seen.Add(EnsureTrackerId(acc));
            changed = true;
            DiagnosticsService.Warn("launcher", $"{acc.DisplayNameOrUser} shared a tracker id with another account; gave it its own");
        }
        if (changed) { try { store.Save(); } catch { } }
    }

    /// <summary>The account whose tracker id this is, or null.</summary>
    public static Account? AccountByTracker(string tracker)
        => string.IsNullOrEmpty(tracker) ? null : _store?.Accounts.FirstOrDefault(a => a.BrowserTrackerId == tracker);

    /// <summary>
    /// Persists a cookie Roblox rotated during the auth-ticket call. Without this an account whose
    /// cookie was rotated eventually stops launching. Never logs the value.
    /// </summary>
    private static void PersistRotatedCookie(Account acc, string? rotated)
    {
        if (string.IsNullOrEmpty(rotated) || rotated == acc.Cookie) return;
        if (!SettingsService.Current.RotationDetectionEnabled) return;
        acc.ReplaceCookie(rotated);
        try { _store?.Save(); } catch { }
        AuditLogService.Log(AuditLogService.Category.Rotation, $"Cookie rotated for {acc.DisplayNameOrUser} (userId {acc.UserId})");
    }

    public static void EnsureMultiInstance(bool enabled)
    {
        if (enabled) RobloxSingletonService.Apply();
        else RobloxSingletonService.Stop();
    }

    public static void ReleaseMultiInstance() => RobloxSingletonService.Stop();

    public static string EnsureTrackerId(Account acc)
    {
        while (string.IsNullOrEmpty(acc.BrowserTrackerId))
        {
            string id = Random.Shared.Next(100000, 175000).ToString() + Random.Shared.Next(100000, 900000).ToString();
            if (AccountByTracker(id) == null) acc.BrowserTrackerId = id;
        }
        return acc.BrowserTrackerId;
    }

    public class LaunchResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";

        /// <summary>
        /// False when trying again cannot help (locked, no cookie, rejected cookie, no protocol handler) —
        /// the watchdog stops retrying a rejoin on those.
        /// </summary>
        public bool Retryable { get; init; } = true;

        /// <summary>The account's cookie is missing or was rejected: nothing but the user can fix that.</summary>
        public bool CookieProblem { get; init; }

        /// <summary>The caller's <c>proceed</c> check said no; nothing was launched.</summary>
        public bool Abandoned { get; init; }

        /// <summary>The launch's timestamp (on the client's command line), once it was started.</summary>
        public long LaunchTime { get; init; }

        /// <summary>
        /// Completes with the pid of the client this launch produced once it has been found, or 0 when
        /// none appeared in time. The launch itself is fire-and-forget (Roblox's protocol handler starts
        /// the client), so this is the only signal that it actually came up.
        /// </summary>
        public Task<int> Client { get; init; } = Task.FromResult(0);

        public static LaunchResult Ok(Task<int> client) => new() { Success = true, Message = L.T("Launch.Launched"), Client = client };
        public static LaunchResult Fail(string m, bool retryable = true) => new() { Success = false, Message = m, Retryable = retryable };
    }

    /// <summary>
    /// Builds the placelauncherurl the client parses. The client reads the query parameters itself
    /// (it does not fetch this URL), which is why the long-standing assetgame form keeps working.
    /// </summary>
    public static string PlaceLauncherUrl(JoinTarget t, string tracker)
    {
        const string Base = "https://assetgame.roblox.com/game/PlaceLauncher.ashx";

        if (t.FollowUserId > 0)
            return $"{Base}?request=RequestFollowUser&userId={t.FollowUserId}";

        if (!string.IsNullOrEmpty(t.LinkCode) || !string.IsNullOrEmpty(t.AccessCode))
            return $"{Base}?request=RequestPrivateGame&browserTrackerId={tracker}&placeId={t.PlaceId}"
                 + $"&accessCode={Uri.EscapeDataString(t.AccessCode ?? "")}"
                 + $"&linkCode={Uri.EscapeDataString(t.LinkCode ?? "")}"
                 + "&isPlayTogetherGame=false";

        if (!string.IsNullOrEmpty(t.JobId))
            return $"{Base}?request=RequestGameJob&browserTrackerId={tracker}&placeId={t.PlaceId}"
                 + $"&gameId={Uri.EscapeDataString(t.JobId)}&isPlayTogetherGame=false";

        return $"{Base}?request=RequestGame&browserTrackerId={tracker}&placeId={t.PlaceId}&isPlayTogetherGame=false";
    }

    /// <param name="jobId">optional specific server</param>
    /// <param name="followUserId">optional user to follow into their game</param>
    public static Task<LaunchResult> LaunchAsync(Account acc, long placeId, string cause, string? jobId = null, long followUserId = 0,
        string? privateLinkCode = null, string? accessCode = null)
        => LaunchAsync(acc, new JoinTarget(placeId, jobId, followUserId, privateLinkCode, accessCode), cause);

    /// <param name="cause">Who launched and why, for the event log (e.g. "user: Launch button", "preset: Grinders").</param>
    /// <param name="profile">Performance profile for this client (<see cref="PerformanceProfiles"/>); null/empty = normal.</param>
    /// <param name="proceed">
    /// Asked once the launch's turn has come and again just before the client starts; false abandons
    /// the launch (the watchdog's relaunch, when someone else launched the account meanwhile).
    /// </param>
    public static async Task<LaunchResult> LaunchAsync(Account acc, JoinTarget target, string cause, string? profile = null, Func<bool>? proceed = null)
    {
        var r = await LaunchCoreAsync(acc, target, profile, proceed);
        if (r.Success)
            EventLog.Write("LAUNCH", EventLog.Who(acc), ("launch", r.LaunchTime), ("target", target.ToString()), ("profile", profile), ("cause", cause));
        else
            EventLog.Write(r.Abandoned ? "LAUNCH-SKIPPED" : "LAUNCH-FAILED", EventLog.Who(acc), ("target", target.ToString()), ("cause", cause),
                           ("why", r.Abandoned ? "the account was launched, found running or stopped meanwhile" : r.Message));
        return r;
    }

    private static async Task<LaunchResult> LaunchCoreAsync(Account acc, JoinTarget target, string? profile, Func<bool>? proceed)
    {
        if (LockService.IsLocked) return LaunchResult.Fail(L.T("Lock.Blocked"), retryable: false);
        if (string.IsNullOrEmpty(acc.Cookie)) return new LaunchResult { Message = L.T("Launch.NoCookie"), Retryable = false, CookieProblem = true };
        if (target.Kind != JoinKind.FollowUser && target.PlaceId <= 0) return LaunchResult.Fail(L.T("Launch.NeedPlace"), retryable: false);

        var settings = SettingsService.Current;
        await WatchdogService.WaitForRobloxUpdateAsync();
        EnsureMultiInstance(settings.EnableMultiInstance);

        string tracker = EnsureTrackerId(acc);

        await _launchGate.WaitAsync();
        bool handedOff = false;
        try
        {
            if (proceed != null && !proceed()) return new LaunchResult { Message = "abandoned", Retryable = false, Abandoned = true };

            // Frame-rate cap, FastFlags, the profile and this account's own overrides, before the process
            // starts. Inside the gate: two launches writing the flag files at once failed with "file in use".
            try
            {
                bool hadUndo = settings.ProfileUndo != null || settings.FpsCapBeforeProfile != null;
                FFlagsService.ApplyForLaunch(settings, acc, profile);
                if (hadUndo || !string.IsNullOrEmpty(profile)) SettingsService.Save();
            }
            catch (Exception ex) { DiagnosticsService.Warn("launcher", "Could not apply graphics settings before launch", ex); }
            if (settings.ResizeClientWindows) RobloxClientSettingsService.WriteWindowed();

            var (ticket, rotated, error) = await RobloxApi.GetAuthTicketDetailedAsync(acc.Cookie);
            if (string.IsNullOrEmpty(ticket))
            {
                // Only an outright rejection (401/403) may mark the account invalid — a 429 from
                // launching several accounts at once must not condemn them all.
                var (identity, rejected) = await RobloxApi.GetAuthenticatedUserDetailedAsync(acc.Cookie);
                if (identity == null && rejected)
                {
                    acc.MarkValidated(false);
                    try { _store?.Save(); } catch { }
                    return new LaunchResult { Message = L.T("Launch.CookieExpired"), Retryable = false, CookieProblem = true };
                }
                if (identity == null)
                    return LaunchResult.Fail(L.T("Launch.Unreachable", error));

                acc.MarkValidated(true);
                return LaunchResult.Fail(L.T("Launch.NoTicket", error));
            }
            acc.MarkValidated(true);
            PersistRotatedCookie(acc, rotated);

            if (settings.AutoCloseLastProcess) CloseLast(acc);

            long launchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Sanitise a pasted Job ID: stray whitespace, quotes and commas from copy-paste.
            string? jobId = target.JobId?.Trim().Trim('"', '\'', ',', ' ');
            if (string.IsNullOrWhiteSpace(jobId)) jobId = null;
            target = target with { JobId = jobId };

            string uri = "roblox-player:1"
                + "+launchmode:play"
                + $"+gameinfo:{ticket}"
                + $"+launchtime:{launchTime}"
                + $"+placelauncherurl:{HttpUtility.UrlEncode(PlaceLauncherUrl(target, tracker))}"
                + $"+browsertrackerid:{tracker}"
                + "+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";

            WatchdogService.ClearBeforeLaunch();
            RobloxSingletonService.SweepBeforeLaunch();
            if (proceed != null && !proceed()) return new LaunchResult { Message = "abandoned", Retryable = false, Abandoned = true };

            try
            {
                // Only clients that appear after this instant can belong to this launch. Backdated a
                // little: the protocol handler may have spawned the client before Process.Start returns.
                DateTime launchedAt = DateTime.Now.AddSeconds(-2);

                using (Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })) { }
                acc.LastUse = DateTime.Now;

                _ = Task.Run(async () =>
                {
                    // Presence flips to "In Game" a few seconds after the join; poll twice so the
                    // dashboard catches it quickly.
                    await Task.Delay(4000);
                    try { await PresenceService.PollNowAsync(); } catch { }
                    await Task.Delay(6000);
                    try { await PresenceService.PollNowAsync(); } catch { }
                });

                var client = Task.Run(() => AttributeClientAsync(acc, target, profile, launchTime, launchedAt));
                ReleaseAfter(client);
                handedOff = true;
                WatchdogService.Launched(acc, target, profile, launchTime);

                try { PluginService.RaiseLaunched(acc, target.PlaceId, jobId); } catch { }
                if (settings.ToastOnLaunch)
                    ToastService.Success(L.T("Toast.Launched.Title"), L.T("Toast.Launched.Body", acc.DisplayNameOrUser));
                if (settings.NotifyOnConnect && WebhookService.Configured)
                    WebhookService.Connected(acc, target.PlaceId, jobId);
                AuditLogService.Log(AuditLogService.Category.Launch, $"Launched {acc.DisplayNameOrUser} into {target}");
                return new LaunchResult { Success = true, Message = L.T("Launch.Launched"), Client = client, LaunchTime = launchTime };
            }
            catch (Exception ex)
            {
                return LaunchResult.Fail(ExplainLaunchFailure(ex), retryable: false);
            }
        }
        finally { if (!handedOff) _launchGate.Release(); }
    }

    /// <summary>
    /// Binds the client this launch produced to its account, recognised by the account's tracker id and
    /// this launch's timestamp on its command line. Looked for during <see cref="RecoverySlot.LaunchTimeout"/>:
    /// on a cold start (shader cache, a pending Roblox update, a slow disk) the client can take a long
    /// time to exist. A launch that never produces one is retried by the watchdog.
    /// </summary>
    private static async Task<int> AttributeClientAsync(Account acc, JoinTarget target, string? profile, long launchTime, DateTime launchedAt)
    {
        await Task.Delay(2000);

        int pid = 0;
        var deadline = DateTime.UtcNow + RecoverySlot.LaunchTimeout;
        while (pid == 0 && DateTime.UtcNow < deadline)
        {
            try { pid = ProcessRegistry.Register(acc, target, profile, launchTime, launchedAt); } catch { }
            if (pid == 0) await Task.Delay(1000);
        }

        if (pid != 0)
        {
            _lastProcess[acc.UserId] = pid;
            DiagnosticsService.Log("launcher", $"{acc.DisplayNameOrUser}'s client is pid {pid}");
            var start = ProcessRegistry.All.FirstOrDefault(t => t.Pid == pid)?.StartTimeLocal ?? default;
            bool exact = ProcessCommandLine.IdentityOf(pid, start)?.LaunchTime == launchTime;
            EventLog.Write("MATCHED", EventLog.Who(acc), ("pid", pid), ("launch", launchTime),
                           ("how", exact ? "tracker id and launch time on its command line" : "start time (command line unreadable)"));
            if (SettingsService.Current.ResizeClientWindows) _ = PlaceWindowAsync(pid);
        }
        else
        {
            DiagnosticsService.Warn("launcher", $"No client appeared for {acc.DisplayNameOrUser} within {(int)RecoverySlot.LaunchTimeout.TotalMinutes} min of launch");
            EventLog.Write("NO-CLIENT", EventLog.Who(acc), ("launch", launchTime), ("waited", EventLine.Duration(RecoverySlot.LaunchTimeout)));
        }
        return pid;
    }

    /// <summary>
    /// Sizes and places a new client's window as soon as it has one, and once more a minute later:
    /// the client can resize itself as it finishes loading. Unlike minimizing, a small window keeps
    /// loading and rendering, so the client stays connected.
    /// </summary>
    private static async Task PlaceWindowAsync(int pid)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000);
            if (!ProcessRegistry.All.Any(t => t.Pid == pid)) return;   // closed meanwhile
            if (InstanceControlService.ShrinkAndPlace(pid, place: true))
            {
                await Task.Delay(TimeSpan.FromSeconds(60));
                InstanceControlService.ShrinkAndPlace(pid, place: false);
                return;
            }
        }
    }

    /// <summary>Options for <see cref="LaunchBatchAsync"/>.</summary>
    /// <param name="Cause">Who started the batch, for the event log.</param>
    public sealed record BatchOptions(int DelaySeconds, int RandomDelaySeconds = 0, string? Profile = null, string Cause = "batch launch");

    /// <param name="NotStarted">Accounts skipped because the batch was stopped.</param>
    public sealed record BatchResult(int Launched, int Failed, IReadOnlyList<string> Errors, int NotStarted = 0);

    /// <summary>
    /// Launches accounts one after another into the same target. Before the next account starts, the
    /// previous client has to have appeared (or the attribution window run out) AND the delay has to
    /// have passed — whichever takes longer. Waiting for the client keeps each launch's graphics
    /// settings from being overwritten before that client has read them, and stops two clients that
    /// start together from being attributed to each other. A failed account never stops the batch
    /// or touches the ones that already launched.
    /// </summary>
    /// <param name="onLaunching">Account about to launch and its index (UI status).</param>
    /// <param name="onWaiting">Seconds left before the next launch; -1 while only waiting for the client.</param>
    /// <param name="ct">Stops the batch between accounts. Clients already launched keep running.</param>
    public static async Task<BatchResult> LaunchBatchAsync(IReadOnlyList<Account> accounts, JoinTarget target, BatchOptions options,
        Action<Account, int>? onLaunching = null, Action<int>? onWaiting = null, CancellationToken ct = default)
    {
        int launched = 0, attempted = 0;
        var errors = new List<string>();

        for (int i = 0; i < accounts.Count && !ct.IsCancellationRequested; i++)
        {
            var acc = accounts[i];
            attempted++;
            onLaunching?.Invoke(acc, i);

            LaunchResult r;
            try { r = await LaunchAsync(acc, target, options.Cause, options.Profile); }
            catch (Exception ex)
            {
                DiagnosticsService.Warn("launcher", $"Launch of {acc.DisplayNameOrUser} threw", ex);
                r = LaunchResult.Fail(L.T("Launch.Failed", ex.GetType().Name));
            }
            if (r.Success) launched++;
            else errors.Add($"{acc.DisplayNameOrUser}: {r.Message}");

            if (i == accounts.Count - 1) break;

            int delay = Math.Max(0, options.DelaySeconds);
            if (options.RandomDelaySeconds > 0) delay += Random.Shared.Next(options.RandomDelaySeconds + 1);

            var waitForClient = r.Success ? r.Client : Task.FromResult(0);
            try
            {
                for (int left = delay; left > 0; left--)
                {
                    onWaiting?.Invoke(left);
                    await Task.Delay(1000, ct);
                }
                if (!waitForClient.IsCompleted)
                {
                    onWaiting?.Invoke(-1);
                    await waitForClient.WaitAsync(ct);
                }
            }
            catch (OperationCanceledException) { break; }
        }

        return new BatchResult(launched, attempted - launched, errors, accounts.Count - attempted);
    }

    /// <summary>
    /// Turns a Process.Start failure on the roblox-player: URI into something actionable. The raw
    /// message ("The system cannot find the file specified") points at nothing; the cause is almost
    /// always a missing or hijacked protocol handler.
    /// </summary>
    /// <remarks>
    /// Never echoes <c>ex.Message</c>: Process.Start puts the file name in it, and here that is the
    /// roblox-player: URI carrying the auth ticket and any private-server code. The message goes to the
    /// status bar, toasts and the crash webhook.
    /// </remarks>
    private static string ExplainLaunchFailure(Exception ex)
    {
        if (ex is System.ComponentModel.Win32Exception or FileNotFoundException) return L.T("Launch.NoHandler");
        DiagnosticsService.Warn("launcher", $"Starting the Roblox client failed ({ex.GetType().Name})");
        return L.T("Launch.Failed", ex.GetType().Name);
    }

    /// <summary>Opens the Roblox app itself (home screen), signed in as this account — no game.</summary>
    public static async Task<LaunchResult> OpenRobloxAppAsync(Account acc, string cause)
    {
        if (LockService.IsLocked) return LaunchResult.Fail(L.T("Lock.Blocked"));
        EnsureMultiInstance(SettingsService.Current.EnableMultiInstance);
        string tracker = EnsureTrackerId(acc);

        await _launchGate.WaitAsync();
        bool handedOff = false;
        try
        {
            var (ticket, rotated, error) = await RobloxApi.GetAuthTicketDetailedAsync(acc.Cookie);
            if (string.IsNullOrEmpty(ticket))
            {
                var (identity, rejected) = await RobloxApi.GetAuthenticatedUserDetailedAsync(acc.Cookie);
                if (identity == null && rejected)
                {
                    acc.MarkValidated(false);
                    return LaunchResult.Fail(L.T("Launch.CookieExpired"));
                }
                return LaunchResult.Fail(L.T("Launch.NoTicket", error));
            }
            acc.MarkValidated(true);
            PersistRotatedCookie(acc, rotated);

            long launchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string uri = "roblox-player:1+launchmode:app"
                + $"+gameinfo:{ticket}+launchtime:{launchTime}+browsertrackerid:{tracker}"
                + "+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";
            WatchdogService.ClearBeforeLaunch();
            RobloxSingletonService.SweepBeforeLaunch();
            try
            {
                DateTime launchedAt = DateTime.Now.AddSeconds(-2);
                using (Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })) { }
                acc.LastUse = DateTime.Now;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000);
                    try { await PresenceService.PollNowAsync(); } catch { }
                });
                var client = Task.Run(() => AttributeClientAsync(acc, new JoinTarget(0), null, launchTime, launchedAt));
                ReleaseAfter(client);
                handedOff = true;
                if (SettingsService.Current.ToastOnLaunch)
                    ToastService.Success(L.T("Toast.Launched.Title"), L.T("Toast.Launched.Body", acc.DisplayNameOrUser));
                EventLog.Write("LAUNCH", EventLog.Who(acc), ("launch", launchTime), ("target", "home screen"), ("cause", cause));
                return LaunchResult.Ok(client);
            }
            catch (Exception ex) { return LaunchResult.Fail(ExplainLaunchFailure(ex)); }
        }
        finally { if (!handedOff) _launchGate.Release(); }
    }

    /// <summary>Closes the account's running clients before it launches again: one account, one client.</summary>
    private static void CloseLast(Account acc)
    {
        var pids = ProcessRegistry.ForUser(acc.UserId).Where(t => !t.IsExternal).Select(t => t.Pid).ToHashSet();
        if (_lastProcess.TryRemove(acc.UserId, out int last)) pids.Add(last);
        foreach (int pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited && p.ProcessName.StartsWith("RobloxPlayer", StringComparison.OrdinalIgnoreCase))
                {
                    // A deliberate close, not a crash: flag it so the watchdog does not answer it with an
                    // auto-rejoin (the session is still booked as playtime).
                    ProcessRegistry.MarkClosing(pid, "launching the same account again (close previous client)");
                    p.CloseMainWindow();
                    if (!p.WaitForExit(1500)) p.Kill();
                }
            }
            catch { }
        }
    }

    /// <summary>Closes every running Roblox client and returns how many were closed.</summary>
    public static int CloseAllClients(string cause)
    {
        int closed = 0;
        foreach (var p in Process.GetProcessesByName("RobloxPlayerBeta"))
        {
            try
            {
                if (!p.HasExited)
                {
                    // Deliberate close: flag it so the watchdog does not auto-rejoin it.
                    ProcessRegistry.MarkClosing(p.Id, cause);
                    p.CloseMainWindow();
                    if (!p.WaitForExit(1500)) p.Kill();
                    closed++;
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
        _lastProcess.Clear();
        try { ProcessRegistry.Prune(); } catch { }
        return closed;
    }
}
