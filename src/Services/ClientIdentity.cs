using System.Text.RegularExpressions;
using System.Web;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Who a running Roblox client belongs to, read from its own command line. Every launch passes the
/// account's browser tracker id (stored with the account, so it survives manager restarts), the
/// launch's own timestamp and the destination. That ties a client to its account and to the exact
/// launch that started it, with no guessing from start times.
/// </summary>
/// <param name="Tracker">The account's browser tracker id.</param>
/// <param name="LaunchTime">The launch's Unix-millisecond timestamp, or 0 when absent.</param>
/// <param name="Target">Where the client was sent, or null when the command line names no game (the home screen).</param>
public sealed record ClientIdentity(string Tracker, long LaunchTime, JoinTarget? Target)
{
    // The client's own "-b" argument or the protocol's "+browsertrackerid:" — not the copy inside the PlaceLauncher URL.
    private static readonly Regex TrackerPattern = new(@"(?:browsertrackerid:|(?:^|\s)-b\s+)(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UrlTrackerPattern = new(@"browsertrackerid=(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LaunchTimePattern = new(@"launchtime[:=](\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // The protocol form ("+placelauncherurl:https%3A%2F%2F…+") or the argument form ("-j https://…").
    private static readonly Regex UrlPattern = new(@"(?:placelauncherurl:|(?:^|\s)-j\s+""?)([^+\s""]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Reads a client's command line. Null when it carries no tracker id (a client started some other way).</summary>
    public static ClientIdentity? Parse(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var tracker = TrackerPattern.Match(commandLine);
        if (!tracker.Success) tracker = UrlTrackerPattern.Match(commandLine);
        if (!tracker.Success) return null;

        var time = LaunchTimePattern.Match(commandLine);
        long launchTime = time.Success && long.TryParse(time.Groups[1].Value, out long t) ? t : 0;

        var url = UrlPattern.Match(commandLine);
        return new ClientIdentity(tracker.Groups[1].Value, launchTime, url.Success ? TargetFrom(url.Groups[1].Value) : null);
    }

    /// <summary>The destination a PlaceLauncher URL asks for (see <c>LauncherService.PlaceLauncherUrl</c>).</summary>
    private static JoinTarget? TargetFrom(string url)
    {
        try
        {
            if (url.Contains('%')) url = Uri.UnescapeDataString(url);
            int q = url.IndexOf('?');
            if (q < 0) return null;
            var query = HttpUtility.ParseQueryString(url[(q + 1)..]);
            long Num(string key) => long.TryParse(query[key], out long n) ? n : 0;
            string? Text(string key) => string.IsNullOrEmpty(query[key]) ? null : query[key];

            return query["request"] switch
            {
                "RequestFollowUser" when Num("userId") > 0 => new JoinTarget(0, FollowUserId: Num("userId")),
                "RequestPrivateGame" when Num("placeId") > 0 => new JoinTarget(Num("placeId"), LinkCode: Text("linkCode"), AccessCode: Text("accessCode")),
                "RequestGameJob" when Num("placeId") > 0 => new JoinTarget(Num("placeId"), Text("gameId")),
                "RequestGame" when Num("placeId") > 0 => new JoinTarget(Num("placeId")),
                _ => null,
            };
        }
        catch { return null; }
    }
}
