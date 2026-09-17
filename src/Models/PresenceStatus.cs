using RobloxAccountManager.Services;

namespace RobloxAccountManager.Models;

/// <summary>
/// Canonical presence values. These exact strings are what the rest of the app compares against
/// (and what the local API reports), so they stay English; only <see cref="Label"/> is translated.
/// </summary>
public static class PresenceStatus
{
    public const string Online = "Online";
    public const string InGame = "In Game";
    public const string InStudio = "In Studio";
    public const string Offline = "Offline";

    /// <summary>Maps Roblox's userPresenceType (0 offline, 1 website, 2 in game, 3 Studio; 4 = invisible).</summary>
    public static string FromType(int type) => type switch
    {
        1 => Online,
        2 => InGame,
        3 => InStudio,
        _ => Offline,
    };

    public static bool IsOnline(string? presence) => presence is Online or InGame or InStudio;

    public static string PaletteKey(string? presence) => presence switch
    {
        Online => "PresenceOnline",
        InGame => "PresenceInGame",
        InStudio => "PresenceStudio",
        _ => "PresenceOffline",
    };

    public static string Label(string? presence) => presence switch
    {
        Online => L.T("Presence.Online"),
        InGame => L.T("Presence.InGame"),
        InStudio => L.T("Presence.InStudio"),
        _ => L.T("Presence.Offline"),
    };

    /// <summary>Sort weight: in game first, then Studio, online, offline.</summary>
    public static int Rank(string? presence) => presence switch
    {
        InGame => 0,
        InStudio => 1,
        Online => 2,
        _ => 3,
    };
}
