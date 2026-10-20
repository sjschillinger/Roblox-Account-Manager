using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

public enum RecoveryAction
{
    None,
    /// <summary>Nothing to carry out, but a relaunch was just booked (see <see cref="RecoveryDecision.Why"/>).</summary>
    Booked,
    /// <summary>Launch the account again (its relaunch is due).</summary>
    Launch,
    /// <summary>Close the client (it is out of its game or stuck) and relaunch it.</summary>
    Replace,
    /// <summary>Close the client and stop watching the account (it joined from somewhere else).</summary>
    CloseAndStop,
}

/// <param name="Action">What to do.</param>
/// <param name="Why">The cause, for the diagnostics log.</param>
/// <param name="Target">Where to launch (Launch only).</param>
public readonly record struct RecoveryDecision(RecoveryAction Action, string Why = "", JoinTarget? Target = null)
{
    public static readonly RecoveryDecision Nothing = new(RecoveryAction.None);
}

/// <summary>What the supervisor sees of one account's client on a pass.</summary>
/// <param name="Pid">The account's live client (the newest one when there are several), or 0.</param>
/// <param name="Uptime">How long that client has been running.</param>
/// <param name="HasWindow">Whether it has a window yet.</param>
/// <param name="Log">Its own log's view, or null when no log is matched to it.</param>
/// <param name="AnyProcess">True while any client of the account is still alive, including one being closed.</param>
/// <param name="LogsWorking">True while other clients' logs are being read: a client without one is then the odd one out.</param>
public readonly record struct ClientObservation(int Pid, TimeSpan Uptime, bool HasWindow, ClientLogState? Log, bool AnyProcess, bool LogsWorking = false)
{
    public static readonly ClientObservation None = new(0, TimeSpan.Zero, false, null, false);
}

/// <summary>
/// The recovery state of one account the manager keeps running: which client is its own, whether a
/// launch is waiting for its client, and when the next relaunch is due. Pure logic — the supervisor
/// (<see cref="WatchdogService"/>) feeds it observations and carries out what it decides — so every
/// failure seen in the field can be replayed in a test.
///
/// The rules:
/// <list type="bullet">
/// <item>A launch must produce its client within <see cref="LaunchTimeout"/>; otherwise it failed and is retried.</item>
/// <item>A client whose log shows it out of its game or still loading (see <see cref="ClientLogState.NeedsRecovery"/>),
///       that has no window after <see cref="NoWindowLimit"/>, or no log of its own after <see cref="NoLogLimit"/>,
///       is closed and relaunched.</item>
/// <item>Every relaunch in a row waits longer (<see cref="RejoinBackoff"/>); none is ever the last.</item>
/// <item>Only a client that has been in its game for <see cref="RejoinBackoff.StableAfter"/> ends the streak.</item>
/// </list>
/// </summary>
public sealed class RecoverySlot
{
    /// <summary>How long a launch may take to produce its client before it counts as failed.</summary>
    public static readonly TimeSpan LaunchTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How long a client may run without a window before it counts as stuck.</summary>
    public static readonly TimeSpan NoWindowLimit = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How long a freshly launched client may run without a log of its own while other clients' logs are
    /// read. Without one nothing can tell it is stuck loading (16 Oct: one sat on "no log" for hours).
    /// Only fresh clients: one that outlived a manager restart may have a log too old to be listed.
    /// </summary>
    public static readonly TimeSpan NoLogLimit = TimeSpan.FromMinutes(5);

    public RecoverySlot(long userId, JoinTarget target, string? profile)
    {
        UserId = userId;
        Target = target;
        Profile = profile;
    }

    public long UserId { get; }

    /// <summary>Why the pending relaunch was booked (set by the supervisor, for the event log).</summary>
    public string Why { get; set; } = "";

    /// <summary>Where the account belongs: the destination of its latest launch.</summary>
    public JoinTarget Target { get; private set; }
    public string? Profile { get; private set; }

    /// <summary>The client being watched, or 0.</summary>
    public int Pid { get; private set; }

    /// <summary>Timestamp of a launch still waiting for its client, or 0.</summary>
    public long PendingLaunch { get; private set; }
    public DateTime PendingSinceUtc { get; private set; }

    /// <summary>True while the supervisor's own launch call (or a restart) is in progress, before it reports back.</summary>
    public bool Issuing { get; private set; }
    private DateTime _issuingSinceUtc;

    /// <summary>A launch call that never reported back is given up on after this (it would block every other relaunch).</summary>
    public static readonly TimeSpan IssueTimeout = TimeSpan.FromMinutes(10);

    /// <summary>When the next relaunch is due, or null when none is waiting.</summary>
    public DateTime? RelaunchAtUtc { get; private set; }

    /// <summary>Relaunches in a row without a stable session.</summary>
    public int Streak { get; private set; }

    /// <summary>Since when the watched client has been in its game, or null.</summary>
    public DateTime? InGameSinceUtc { get; private set; }

    private DateTime _replaceHoldUntilUtc;

    /// <summary>Closing the client failed: don't try to replace it again before <paramref name="untilUtc"/>.</summary>
    public void HoldReplace(DateTime untilUtc) => _replaceHoldUntilUtc = untilUtc;

    /// <summary>
    /// A restart (timed, scheduled) is about to close the client and launch it again itself: let go of
    /// the client so its exit isn't taken as the user closing it, and hold off any relaunch of our own.
    /// The restart reports back through <see cref="Launched"/> or <see cref="LaunchFailed"/>.
    /// </summary>
    public void Handover(DateTime nowUtc)
    {
        Pid = 0;
        PendingLaunch = 0;
        Issuing = true;
        _issuingSinceUtc = nowUtc;
        RelaunchAtUtc = null;
        InGameSinceUtc = null;
    }

    /// <summary>True while the account is waiting for its client or its relaunch (it counts against "one launch at a time").</summary>
    public bool Launching => PendingLaunch != 0 || Issuing;

    /// <summary>Any launch of this account — the user's, a preset's, a schedule's or the supervisor's own. Its client is the one to watch now.</summary>
    public void Launched(JoinTarget target, string? profile, long launchTime, DateTime nowUtc)
    {
        Target = target;
        Profile = profile;
        PendingLaunch = launchTime;
        PendingSinceUtc = nowUtc;
        Issuing = false;
        RelaunchAtUtc = null;
        Pid = 0;
        InGameSinceUtc = null;
    }

    /// <summary>
    /// The supervisor is closing the client for a reset of Roblox (see <see cref="LaunchHealth"/>) and will
    /// launch it again after <paramref name="delay"/>; the streak is left as it is.
    /// </summary>
    public void Reset(DateTime nowUtc, TimeSpan delay)
    {
        Pid = 0;
        PendingLaunch = 0;
        Issuing = false;
        InGameSinceUtc = null;
        RelaunchAtUtc = nowUtc + delay;
    }

    /// <summary>The supervisor is starting a relaunch; it reports back through <see cref="Launched"/> or <see cref="LaunchFailed"/>.</summary>
    public void BeginLaunch(DateTime nowUtc)
    {
        Issuing = true;
        _issuingSinceUtc = nowUtc;
        RelaunchAtUtc = null;
    }

    /// <summary>The launch call itself failed (no ticket, network). Books the next try.</summary>
    public TimeSpan LaunchFailed(DateTime nowUtc) => Book(nowUtc);

    /// <summary>The account's client was found: claimed by its launch, or recognised by its tracker id.</summary>
    public void Found(int pid)
    {
        Pid = pid;
        PendingLaunch = 0;
        Issuing = false;
        RelaunchAtUtc = null;
    }

    /// <summary>The watched client is gone and nobody closed it on purpose, or the supervisor just closed it. Books the relaunch.</summary>
    public TimeSpan Lost(DateTime nowUtc) => Book(nowUtc);

    private TimeSpan Book(DateTime nowUtc)
    {
        if (InGameSinceUtc is { } since && nowUtc - since >= RejoinBackoff.StableAfter) Streak = 0;
        var delay = RejoinBackoff.DelayFor(Streak);
        Streak++;
        Pid = 0;
        PendingLaunch = 0;
        Issuing = false;
        InGameSinceUtc = null;
        RelaunchAtUtc = nowUtc + delay;
        return delay;
    }

    /// <param name="watchDisconnects">Whether disconnects and stuck clients are acted on (Settings → rejoin on disconnect).</param>
    /// <param name="disconnectWait">How long a client may stay disconnected before it is replaced.</param>
    /// <param name="canLaunch">False while another account's launch is still waiting for its client, or the manager is locked.</param>
    public RecoveryDecision Tick(ClientObservation c, DateTime nowUtc, bool watchDisconnects, TimeSpan disconnectWait, bool canLaunch)
    {
        if (c.Pid != 0) return Watch(c, nowUtc, watchDisconnects, disconnectWait);

        // A client was being watched and nothing is alive any more, but no exit was reported: lost.
        if (Pid != 0 && !c.AnyProcess)
        {
            Lost(nowUtc);
            return new RecoveryDecision(RecoveryAction.Booked, "its client is gone");
        }

        if (Issuing && nowUtc - _issuingSinceUtc >= IssueTimeout)
        {
            Book(nowUtc);
            return new RecoveryDecision(RecoveryAction.Booked, "its relaunch never reported back");
        }

        if (PendingLaunch != 0)
        {
            if (nowUtc - PendingSinceUtc < LaunchTimeout) return RecoveryDecision.Nothing;
            Book(nowUtc);
            return new RecoveryDecision(RecoveryAction.Booked, $"no client appeared within {(int)LaunchTimeout.TotalMinutes} min of its launch");
        }

        // Relaunch only once every client of the account is gone: a second one would get the first
        // kicked with "joined from another device".
        if (RelaunchAtUtc is { } due && nowUtc >= due && canLaunch && !c.AnyProcess)
            return new RecoveryDecision(RecoveryAction.Launch, "relaunch due", Target.ForRejoin(Streak));

        return RecoveryDecision.Nothing;
    }

    private RecoveryDecision Watch(ClientObservation c, DateTime nowUtc, bool watchDisconnects, TimeSpan disconnectWait)
    {
        if (Pid != c.Pid) Found(c.Pid);

        var log = c.Log;
        bool inGame = log != null ? log.Phase == ClientPhase.InGame : c.HasWindow;
        if (!inGame) InGameSinceUtc = null;
        else
        {
            InGameSinceUtc ??= log != null ? log.PhaseSinceUtc : nowUtc - c.Uptime;
            if (nowUtc - InGameSinceUtc.Value >= RejoinBackoff.StableAfter) Streak = 0;
        }

        if (!watchDisconnects || nowUtc < _replaceHoldUntilUtc) return RecoveryDecision.Nothing;

        if (log != null && log.Phase == ClientPhase.Out && log.Reason == ClientLogState.ReasonJoinedElsewhere)
            return new RecoveryDecision(RecoveryAction.CloseAndStop, "the account joined from somewhere else");

        if (log != null && log.NeedsRecovery(nowUtc, disconnectWait))
            return new RecoveryDecision(RecoveryAction.Replace, Describe(log));

        if (!c.HasWindow && c.Uptime >= NoWindowLimit)
            return new RecoveryDecision(RecoveryAction.Replace, $"no window after {(int)c.Uptime.TotalMinutes} min");

        if (log == null && c.LogsWorking && c.Uptime >= NoLogLimit && c.Uptime < TimeSpan.FromMinutes(30))
            return new RecoveryDecision(RecoveryAction.Replace, $"no log of its own after {(int)c.Uptime.TotalMinutes} min (it cannot be told apart from stuck loading)");

        return RecoveryDecision.Nothing;
    }

    private static string Describe(ClientLogState log)
    {
        string why = log.Phase switch
        {
            ClientPhase.Out => log.Reason > 0 ? $"disconnected (reason {log.Reason})" : "disconnected",
            ClientPhase.Teleporting => "a teleport never finished",
            _ => "still loading",
        };
        return $"{why} since {log.PhaseSinceUtc:HH:mm:ss} UTC";
    }
}
