using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Tracks the RobloxPlayerBeta processes this app launched, keyed by PID, with the
/// account + destination they belong to. Foundation for Anti-AFK, the crash watchdog,
/// the RAM monitor and the window-grid manager.
/// </summary>
public static class ProcessRegistry
{
    /// <summary>The only process name a Roblox game client ever has.</summary>
    private const string ClientProcess = "RobloxPlayerBeta";

    /// <summary>
    /// How long a client must have been running before it counts as "started outside the app".
    /// A launch of ours registers a few seconds after Process.Start, so adopting any younger
    /// process would let the adopter steal a client the launch is still trying to claim.
    /// </summary>
    private static readonly TimeSpan AdoptionGrace = TimeSpan.FromSeconds(30);

    public class Tracked
    {
        public long UserId { get; init; }
        public string Alias { get; init; } = "";
        public int Pid { get; set; }

        /// <summary>
        /// Where the launch was sent — kept whole so a rejoin goes back to the same private server or
        /// followed player instead of degrading to "place + job".
        /// </summary>
        public JoinTarget Target { get; init; } = new(0);
        public long PlaceId => Target.PlaceId;
        public string? JobId => Target.JobId;

        /// <summary>Performance profile the client was launched with (reapplied on a rejoin).</summary>
        public string? Profile { get; init; }

        public DateTime LaunchedUtc { get; init; } = DateTime.UtcNow;

        /// <summary>Timestamp of the launch that started this client (see <see cref="ClientIdentity"/>), or 0.</summary>
        public long LaunchTime { get; init; }

        /// <summary>True for clients adopted from outside the manager (see <see cref="AdoptUntracked"/>).</summary>
        public bool IsExternal { get; init; }

        /// <summary>
        /// Process identity, captured at registration. Windows recycles PIDs aggressively, and
        /// every consumer of this registry acts on the PID — the RAM monitor kills it, Anti-AFK
        /// types into its window, the scheduler closes it. Without an identity check those
        /// actions eventually land on whatever unrelated program inherited the number.
        /// </summary>
        public string ProcessName { get; init; } = ClientProcess;
        public DateTime StartTimeLocal { get; init; }

        /// <summary>How long this client has been running.</summary>
        public TimeSpan Uptime => DateTime.UtcNow - LaunchedUtc;

        /// <summary>
        /// Set when the manager (or the user through it) is closing this client on purpose. The exit
        /// is still reported — playtime is booked — but the crash watchdog leaves it alone instead of
        /// answering a deliberate close with an auto-rejoin.
        /// </summary>
        public volatile bool ClosingIntentionally;

        /// <summary>When <see cref="ClosingIntentionally"/> was last set (see <see cref="MarkClosing"/>).</summary>
        public DateTime ClosingSinceUtc;

        /// <summary>Who closed or killed this client and why, for the event log; null when nobody did.</summary>
        public volatile string? CloseCause;

        /// <summary>
        /// A handle kept open for the client's lifetime: it lets the exit code be read once the process
        /// is gone (and Windows can't hand the pid to another process while it is open).
        /// </summary>
        internal SafeProcessHandle? ExitHandle { get; init; }
    }

    private static readonly ConcurrentDictionary<int, Tracked> _byPid = new();

    /// <summary>Raised (off the UI thread) when a tracked client exits/crashes.</summary>
    public static event Action<Tracked>? Exited;

    /// <summary>Raised (off the UI thread) whenever a client was registered or dropped.</summary>
    public static event Action? Changed;

    private static void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>
    /// Flags a tracked client as being closed on purpose (see <see cref="Tracked.ClosingIntentionally"/>)
    /// and records who is closing it and why.
    /// </summary>
    public static void MarkClosing(int pid, string cause)
    {
        if (_byPid.TryGetValue(pid, out var t))
        {
            bool repeat = t.ClosingIntentionally && t.CloseCause == cause;   // marked first, then closed by the same caller
            t.ClosingSinceUtc = DateTime.UtcNow;
            t.CloseCause = cause;
            t.ClosingIntentionally = true;
            if (!repeat) EventLog.Write("CLOSE", t.IsExternal ? "external client" : EventLog.Who(t.Alias, t.UserId), ("pid", pid), ("launch", t.LaunchTime == 0 ? null : t.LaunchTime), ("cause", cause));
        }
        else EventLog.Write("CLOSE", "untracked client", ("pid", pid), ("cause", cause));
    }

    /// <summary>A close or kill that didn't happen: the client is running normally again, and a later exit is nobody's doing.</summary>
    public static void UnmarkClosing(int pid)
    {
        if (!_byPid.TryGetValue(pid, out var t) || !IsAlive(pid)) return;
        t.ClosingIntentionally = false;
        t.CloseCause = null;
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    /// <summary>Records who is ending a client and why without making it a deliberate close: its exit still counts as a crash and is recovered.</summary>
    public static void NoteKill(int pid, string cause)
    {
        if (_byPid.TryGetValue(pid, out var t)) t.CloseCause = cause;
        EventLog.Write("KILL", t == null ? "untracked client" : EventLog.Who(t.Alias, t.UserId), ("pid", pid), ("cause", cause), ("recovered", "yes"));
    }

    private static SafeProcessHandle? OpenExitHandle(int pid)
    {
        try
        {
            var h = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (!h.IsInvalid) return h;
            h.Dispose();
        }
        catch { }
        return null;
    }

    /// <summary>The exit code as Windows reports it ("0x…"), or null when it can't be read.</summary>
    private static string? ExitCodeOf(Tracked t)
    {
        try
        {
            if (t.ExitHandle is not { IsInvalid: false } h) return null;
            if (!GetExitCodeProcess(h, out uint code) || code == STILL_ACTIVE) return null;
            return code == 0 ? "0" : $"0x{code:X8}";
        }
        catch { return null; }
        finally { try { t.ExitHandle?.Dispose(); } catch { } }
    }

    private const uint SYNCHRONIZE = 0x00100000;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint STILL_ACTIVE = 259;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    /// <summary>Pids tracked for an account right now.</summary>
    public static int CountFor(long userId) => _byPid.Values.Count(t => t.UserId == userId && !t.IsExternal);

    public static IReadOnlyCollection<Tracked> All
    {
        get { Prune(); return _byPid.Values.ToList(); }
    }

    public static IEnumerable<Tracked> ForUser(long userId) => All.Where(t => t.UserId == userId);

    private static Func<string, Account?>? _accountByTracker;

    /// <summary>Wires the tracker id → account lookup that recognises the manager's own clients. Call once at startup.</summary>
    public static void Init(Func<string, Account?> accountByTracker) => _accountByTracker = accountByTracker;

    /// <summary>
    /// Finds the client a launch produced and registers it for the account. The client is recognised
    /// by its command line: the account's tracker id and this launch's timestamp (see
    /// <see cref="ClientIdentity"/>), so it can't be confused with another account's client however
    /// slowly it starts or however many launches overlap. Returns the pid, or 0 when it isn't running
    /// yet (the caller retries).
    /// </summary>
    /// <param name="launchedAt">
    /// Local time the launch was fired. Only used when a client's command line can't be read: then the
    /// oldest unclaimed client that started after it is taken, as before command lines were read.
    /// </param>
    public static int Register(Account acc, JoinTarget target, string? profile, long launchTime, DateTime launchedAt)
    {
        var procs = Process.GetProcessesByName(ClientProcess);
        try
        {
            var candidates = procs
                .Select(p =>
                {
                    try { return (proc: p, start: p.StartTime, exited: p.HasExited); }
                    catch { return (proc: p, start: DateTime.MaxValue, exited: true); }
                })
                .Where(x => !x.exited)
                .Select(x =>
                {
                    bool readable = ProcessCommandLine.TryRead(x.proc.Id, x.start, out var id);
                    return (x.proc, x.start, id, readable);
                })
                .ToList();

            // Exact: this account's tracker id and this launch's timestamp.
            var exact = candidates.FirstOrDefault(x => x.id != null && x.id.Tracker == acc.BrowserTrackerId
                                                       && x.id.LaunchTime == launchTime);
            if (exact.proc != null)
            {
                // Already recognised (by AdoptOwn) as this account's: keep it, with this launch's details.
                if (_byPid.TryGetValue(exact.proc.Id, out var known) && known.UserId == acc.UserId && !known.IsExternal
                    && known.Profile == profile && known.Target == target)
                    return exact.proc.Id;
                if (!Claimable(exact.proc.Id, acc.UserId)) return 0;
                Track(acc, exact.proc.Id, exact.start, target, profile, launchTime);
                return exact.proc.Id;
            }

            // Fallback only for command lines that couldn't be read at all: the oldest unclaimed client
            // started after the launch. A client that was read and carries no (or another) tracker id is
            // never this launch's.
            var guess = candidates
                .Where(x => !x.readable && Claimable(x.proc.Id, acc.UserId) && x.start >= launchedAt)
                .OrderBy(x => x.start)
                .FirstOrDefault();
            if (guess.proc == null) return 0;
            Track(acc, guess.proc.Id, guess.start, target, profile, launchTime);
            return guess.proc.Id;
        }
        catch { return 0; }
        finally { foreach (var p in procs) { try { p.Dispose(); } catch { } } }
    }

    private static void Track(Account acc, int pid, DateTime start, JoinTarget target, string? profile, long launchTime)
    {
        if (_byPid.TryGetValue(pid, out var old)) { try { old.ExitHandle?.Dispose(); } catch { } }
        _byPid[pid] = new Tracked
        {
            ExitHandle = OpenExitHandle(pid),
            UserId = acc.UserId,
            Alias = acc.DisplayNameOrUser,
            Pid = pid,
            Target = target,
            Profile = profile,
            ProcessName = ClientProcess,
            StartTimeLocal = start,
            LaunchedUtc = start.ToUniversalTime(),
            LaunchTime = launchTime,
        };
        RaiseChanged();
    }

    /// <summary>
    /// A pid may be taken for an account when nothing tracks it yet, when only an "external"
    /// placeholder does, or when it is already that account's.
    /// </summary>
    private static bool Claimable(int pid, long userId)
        => !_byPid.TryGetValue(pid, out var existing) || existing.IsExternal || existing.UserId == userId;

    /// <summary>
    /// Registers every running client whose command line carries one of the manager's accounts'
    /// tracker ids under that account — a client that outlived a manager restart, the second process a
    /// client handed over to, a launch whose own lookup gave up. An account's client is therefore never
    /// shown as "started outside the manager", and a relaunch never starts next to a client that is
    /// already running. A launch still claiming the client re-registers it with its own details.
    /// Returns the clients newly registered.
    /// </summary>
    public static IReadOnlyList<Tracked> AdoptOwn()
    {
        var found = new List<Tracked>();
        if (_accountByTracker == null) return found;
        var procs = Process.GetProcessesByName(ClientProcess);
        try
        {
            ProcessCommandLine.Forget(procs.Select(p => p.Id));
            foreach (var p in procs)
            {
                try
                {
                    if (p.HasExited) continue;
                    if (_byPid.TryGetValue(p.Id, out var existing) && !existing.IsExternal) continue;
                    DateTime start = p.StartTime;
                    var id = ProcessCommandLine.IdentityOf(p.Id, start);
                    if (id == null || _accountByTracker(id.Tracker) is not { } acc) continue;

                    if (existing != null) { try { existing.ExitHandle?.Dispose(); } catch { } }
                    var t = new Tracked
                    {
                        ExitHandle = OpenExitHandle(p.Id),
                        UserId = acc.UserId,
                        Alias = acc.DisplayNameOrUser,
                        Pid = p.Id,
                        Target = id.Target ?? new JoinTarget(0),
                        ProcessName = ClientProcess,
                        StartTimeLocal = start,
                        LaunchedUtc = start.ToUniversalTime(),
                        LaunchTime = id.LaunchTime,
                    };
                    _byPid[p.Id] = t;
                    found.Add(t);
                    EventLog.Write("RECOGNISED", EventLog.Who(acc.DisplayNameOrUser, acc.UserId), ("pid", p.Id), ("launch", id.LaunchTime),
                                   ("running", EventLine.Duration(t.Uptime)), ("target", t.Target.ToString()), ("how", "tracker id on its command line"));
                }
                catch { /* raced with exit */ }
            }
        }
        catch { }
        finally { foreach (var p in procs) { try { p.Dispose(); } catch { } } }
        if (found.Count > 0) RaiseChanged();
        return found;
    }

    /// <summary>
    /// Clients started outside the manager (website Play button, Roblox home screen, a desktop
    /// shortcut) are invisible to Anti-AFK, the RAM monitor and the window grid because nothing
    /// ever registered them. Adopt them under a placeholder identity so those tools still work.
    /// Clients of the manager's own accounts are never adopted this way (see <see cref="AdoptOwn"/>).
    /// Returns how many were newly adopted.
    /// </summary>
    /// <remarks>
    /// Only clients older than <see cref="AdoptionGrace"/> are eligible, so a launch still looking for
    /// its client (one whose command line couldn't be read) is never beaten to it.
    /// </remarks>
    public static int AdoptUntracked()
    {
        AdoptOwn();
        int adopted = 0;
        var procs = Process.GetProcessesByName(ClientProcess);
        try
        {
            foreach (var p in procs)
            {
                try
                {
                    if (p.HasExited || _byPid.ContainsKey(p.Id)) continue;

                    DateTime start;
                    try { start = p.StartTime; } catch { continue; }   // can't age it -> leave it alone
                    if (DateTime.Now - start < AdoptionGrace) continue;
                    if (ProcessCommandLine.IdentityOf(p.Id, start) is { } id && _accountByTracker?.Invoke(id.Tracker) != null) continue;

                    // TryAdd, not the indexer: a launch in flight may claim this pid moments
                    // later and its real account/place must win over the placeholder.
                    var handle = OpenExitHandle(p.Id);
                    if (_byPid.TryAdd(p.Id, new Tracked
                    {
                        ExitHandle = handle,
                        UserId = 0,
                        Alias = "External client",
                        Pid = p.Id,
                        LaunchedUtc = start.ToUniversalTime(),
                        IsExternal = true,
                        ProcessName = ClientProcess,
                        StartTimeLocal = start,
                    }))
                    {
                        adopted++;
                        EventLog.Write("EXTERNAL", "external client", ("pid", p.Id), ("running", EventLine.Duration(DateTime.Now - start)),
                                       ("why", "no account of the manager's tracker id on its command line"));
                    }
                    else handle?.Dispose();
                }
                catch { /* raced with exit */ }
            }
        }
        catch { }
        finally { foreach (var p in procs) { try { p.Dispose(); } catch { } } }
        if (adopted > 0) RaiseChanged();
        return adopted;
    }

    /// <summary>Records a client's exit: how long it ran, its exit code, who closed it, and its last state per its own log.</summary>
    private static void LogExit(Tracked t)
    {
        try
        {
            var log = ClientLogWatcher.StateOf(t.Pid);
            string? state = log == null ? null
                : $"{log.Phase} since {log.PhaseSinceUtc:HH:mm:ss}Z" + (log.Reason > 0 ? $" reason {log.Reason}" : "");
            EventLog.Write("EXIT", t.IsExternal ? "external client" : EventLog.Who(t.Alias, t.UserId),
                ("pid", t.Pid), ("launch", t.LaunchTime == 0 ? null : t.LaunchTime), ("uptime", EventLine.Duration(t.Uptime)),
                ("code", ExitCodeOf(t)), ("closedBy", t.CloseCause ?? "nobody (crash, or closed in Roblox)"),
                ("lastState", state), ("lastDisconnect", log?.LastDisconnectLine));
        }
        catch { }
    }

    // Pids of the accounts' clients that exited recently: a process one of them started just before
    // exiting (see StrayPolicy) still names it as its parent.
    private static readonly ConcurrentDictionary<int, DateTime> _pastOwn = new();

    /// <summary>True when <paramref name="pid"/> is, or was in the last half hour, one of the accounts' clients.</summary>
    public static bool IsOrWasOwn(int pid)
    {
        foreach (var old in _pastOwn.Where(kv => DateTime.UtcNow - kv.Value > TimeSpan.FromMinutes(30)).ToList())
            _pastOwn.TryRemove(old.Key, out _);
        return (_byPid.TryGetValue(pid, out var t) && !t.IsExternal && t.UserId > 0) || _pastOwn.ContainsKey(pid);
    }

    /// <summary>Drops entries whose process has exited, firing <see cref="Exited"/> for each.</summary>
    public static void Prune()
    {
        bool removed = false;
        // Pids of every running client, from the process list itself. Asking a single process can
        // fail while it is still running (access denied, a busy moment), and treating that as an
        // exit made the watchdog launch a second client next to the first.
        HashSet<int>? running = null;
        try
        {
            var procs = Process.GetProcessesByName(ClientProcess);
            running = procs.Select(p => p.Id).ToHashSet();
            foreach (var p in procs) p.Dispose();
        }
        catch { }

        foreach (var kv in _byPid.ToArray())
        {
            bool gone;
            if (running != null && !running.Contains(kv.Key)) gone = true;
            else
            {
                try
                {
                    using var p = Process.GetProcessById(kv.Key);
                    gone = p.HasExited || !IsSameProcess(p, kv.Value, unreadable: running != null);
                }
                catch { gone = running == null; }   // listed as running: a failed lookup is not an exit
            }

            if (gone && _byPid.TryRemove(kv.Key, out var t))
            {
                if (!t.IsExternal && t.UserId > 0) _pastOwn[t.Pid] = DateTime.UtcNow;
                LogExit(t);
                removed = true;
                try { Exited?.Invoke(t); } catch { }
            }
        }
        if (removed) RaiseChanged();
    }

    /// <summary>
    /// True when the live process really is the one that was registered. Guards against PID
    /// reuse: name and start time together are unique enough that no other program can be
    /// mistaken for a tracked client.
    /// </summary>
    /// <param name="unreadable">Answer when the process can't be inspected (false everywhere an action
    /// would land on it; Prune passes true for a pid the process list shows as a running client).</param>
    private static bool IsSameProcess(Process live, Tracked tracked, bool unreadable = false)
    {
        try
        {
            if (!string.Equals(live.ProcessName, tracked.ProcessName, StringComparison.OrdinalIgnoreCase))
                return false;
            if (tracked.StartTimeLocal == default) return true;   // registered before this check existed
            return Math.Abs((live.StartTime - tracked.StartTimeLocal).TotalSeconds) < 2;
        }
        catch { return unreadable; }
    }

    /// <summary>Live main-window handle for a tracked PID (0 until the client has a window).</summary>
    public static IntPtr WindowHandle(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!_byPid.TryGetValue(pid, out var t) || IsSameProcess(p, t)) return p.MainWindowHandle;
            return IntPtr.Zero;   // pid was recycled — never hand out a stranger's window
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>Working-set bytes for a tracked PID, or -1 if it's gone.</summary>
    public static long MemoryBytes(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!_byPid.TryGetValue(pid, out var t) || IsSameProcess(p, t)) return p.WorkingSet64;
            return -1;            // recycled pid: reporting its RAM could get it auto-killed
        }
        catch { return -1; }
    }

    /// <summary>Drops a pid without raising <see cref="Exited"/> (the pid turned out not to be a client).</summary>
    public static void Forget(int pid)
    {
        if (_byPid.TryRemove(pid, out var t)) { try { t.ExitHandle?.Dispose(); } catch { } RaiseChanged(); }
    }
}
