namespace RobloxAccountManager.Services;

/// <summary>Which Roblox log file belongs to which client (pure, so it can be tested; used by <see cref="ClientLogWatcher"/>).</summary>
public static class ClientLogFiles
{
    /// <summary>A game client's own log ("…_Player_XXXXX_last.log"), not a crash handler's or the installer's.</summary>
    public static bool IsClientLog(string name)
        => name.Contains("_Player_", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("CrashHandler", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("Installer", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The log for client <paramref name="pid"/> of <paramref name="userId"/>, started at <paramref name="startLocal"/>.
    /// Logs another client holds, and logs known to belong to a different account, are never candidates.
    /// When Windows can tell which process has a log open (<paramref name="usersOf"/>), the client's own
    /// log is the one it has open, and a client none of the candidates belongs to yet gets none (its log
    /// is not created yet: the next pass asks again). Otherwise, the log created closest to the client's
    /// start (3 s before to 20 s after) is taken. Null when none fits.
    /// </summary>
    public static string? PickLog(IEnumerable<(string Path, DateTime CreatedLocal)> logs, DateTime startLocal, long userId, int pid,
        ISet<string> held, IReadOnlyDictionary<string, long> owners, Func<string, IReadOnlyCollection<int>?>? usersOf = null)
    {
        var candidates = logs
            .Where(f => !held.Contains(f.Path) && !(owners.TryGetValue(f.Path, out long o) && o != userId))
            .Select(f => (f.Path, gap: (f.CreatedLocal - startLocal).TotalSeconds))
            .Where(x => x.gap > -3 && x.gap < 120)
            .OrderBy(x => Math.Abs(x.gap))
            .ToList();

        if (usersOf != null)
        {
            bool answered = false;
            foreach (var (path, _) in candidates)
            {
                // An empty answer is no answer: a client that is running always has its own log open.
                if (usersOf(path) is not { Count: > 0 } users) continue;
                answered = true;
                if (users.Contains(pid)) return path;
            }
            if (answered) return null;
        }
        return candidates.Where(x => x.gap < 20).Select(x => x.Path).FirstOrDefault();
    }
}
