namespace RobloxAccountManager.Models;

/// <summary>
/// An account's own values for settings that otherwise come from Settings. Null means "use the
/// global setting". Resolved through <see cref="Services.AccountSettings"/>.
/// </summary>
public sealed class AccountOverrides
{
    /// <summary>Anti-AFK on or off for this account's clients.</summary>
    public bool? AfkEnabled { get; set; }

    /// <summary>Minutes between Anti-AFK visits (a fixed interval, not randomized).</summary>
    public int? AfkIntervalMinutes { get; set; }

    /// <summary>The key Anti-AFK presses.</summary>
    public string? AfkKey { get; set; }

    /// <summary>Minutes a client may stay disconnected before it is closed and relaunched.</summary>
    public int? DisconnectMinutes { get; set; }

    /// <summary>Restart the client after this many minutes; 0 = never.</summary>
    public int? RestartMinutes { get; set; }

    /// <summary>Restart the client once its memory has grown to this multiple of its settled size; 0 = never.</summary>
    public double? RamGrowthFactor { get; set; }

    /// <summary>Where the account launches when the launch bar is empty: a place (0 = none) ...</summary>
    public long DefaultPlaceId { get; set; }

    /// <summary>... and optionally a server: a Job ID, a private-server link or a share link.</summary>
    public string DefaultServer { get; set; } = "";

    public bool HasDefaultDestination => DefaultPlaceId > 0 || !string.IsNullOrWhiteSpace(DefaultServer);

    public bool HasAfk => AfkEnabled != null || AfkIntervalMinutes != null || AfkKey != null;

    public bool HasRecovery => DisconnectMinutes != null || RestartMinutes != null || RamGrowthFactor != null;
}
