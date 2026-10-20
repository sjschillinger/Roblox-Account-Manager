namespace RobloxAccountManager.Services;

/// <summary>When a stray Roblox process is looked at (see <see cref="StrayPolicy"/>).</summary>
public enum StrayCheck
{
    /// <summary>The supervisor's regular pass.</summary>
    Pass,
    /// <summary>Right before a launch: a windowless client would take that launch's ticket and the new client would exit.</summary>
    BeforeLaunch,
    /// <summary>One of the manager's clients just exited within a minute of starting: it probably handed its launch to a stray.</summary>
    AfterFastExit,
}

/// <summary>
/// Which Roblox processes no account of ours owns are closed. Pure, so it can be tested.
///
/// A client that leaves its game (a disconnect, or being closed while disconnected) starts a new,
/// windowless Roblox process within a second, without the manager's tracker id. That process holds
/// on to Roblox's single-instance handover: the next launch gives it its ticket and exits within
/// seconds, and it then shows as "started outside the manager" playing someone else's account.
/// Seen on 13, 14, 16 and 17 Oct. A process with a window is never closed: someone may be using it.
/// </summary>
public static class StrayPolicy
{
    /// <summary>How long any untracked Roblox process may run without a window before it counts as stuck.</summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(1);

    /// <param name="spawnedByOurs">Its parent is, or was, one of the manager's clients.</param>
    public static bool ShouldClose(bool spawnedByOurs, bool hasWindow, TimeSpan age, StrayCheck check)
    {
        if (hasWindow) return false;
        if (spawnedByOurs) return check != StrayCheck.Pass || age >= TimeSpan.FromSeconds(3);
        return check switch
        {
            StrayCheck.AfterFastExit => true,
            StrayCheck.BeforeLaunch => age >= TimeSpan.FromSeconds(20),   // a client started by hand gets its window well before
            _ => age >= StuckAfter,
        };
    }
}

/// <summary>What the supervisor should do about Roblox itself.</summary>
public enum RobloxRepair
{
    None,
    /// <summary>Close stray and orphaned Roblox processes and take Roblox's single-instance lock afresh.</summary>
    Cleanup,
    /// <summary>Close every Roblox process the manager keeps running and start them again one at a time.</summary>
    FullReset,
}

/// <summary>
/// Notices when Roblox as a whole stops starting clients — every launch dying within seconds, as on
/// 17 Oct when only a reboot brought clients back — and escalates: first a cleanup, then a full
/// reset of every Roblox process (what a reboot does for Roblox). Any launch reaching its game ends
/// it. Pure, so it can be tested.
/// </summary>
public sealed class LaunchHealth
{
    /// <summary>Failed launches in a row (any account) before stray and orphaned processes are cleaned up.</summary>
    public const int CleanupAfter = 2;

    /// <summary>Failed launches in a row (any account) before every Roblox process is reset.</summary>
    public const int ResetAfter = 4;

    /// <summary>Launches that failed in a row since the last one that reached its game (or the last reset).</summary>
    public int FailuresInARow { get; private set; }

    /// <summary>Resets in a row without a launch reaching its game.</summary>
    public int ResetsInARow { get; private set; }

    private DateTime? _lastResetUtc;

    /// <summary>A reset that didn't help is not repeated right away: 10 min after the first, then 20, then every 30.</summary>
    public static TimeSpan ResetSpacing(int resetsInARow) => TimeSpan.FromMinutes(Math.Min(30, 10 * resetsInARow));

    /// <summary>A launched client reached its game: Roblox works.</summary>
    public void Succeeded()
    {
        FailuresInARow = 0;
        ResetsInARow = 0;
    }

    /// <summary>A launched client died before reaching its game, or never appeared.</summary>
    public RobloxRepair Failed(DateTime nowUtc)
    {
        FailuresInARow++;
        if (FailuresInARow >= ResetAfter
            && (_lastResetUtc is not { } last || nowUtc - last >= ResetSpacing(ResetsInARow)))
        {
            _lastResetUtc = nowUtc;
            ResetsInARow++;
            FailuresInARow = 0;
            return RobloxRepair.FullReset;
        }
        return FailuresInARow == CleanupAfter ? RobloxRepair.Cleanup : RobloxRepair.None;
    }
}
