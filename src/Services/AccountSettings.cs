using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// The value a setting has for one account: its own (see <see cref="AccountOverrides"/>) when it has
/// one, otherwise the global setting. Clients started outside the manager (no account) always get the
/// global value.
/// </summary>
public static class AccountSettings
{
    private static Func<IReadOnlyList<Account>>? _accounts;

    /// <summary>Wires the account list. Call once at startup.</summary>
    public static void Init(Func<IReadOnlyList<Account>> accounts) => _accounts = accounts;

    private static AppSettings S => SettingsService.Current;

    private static IReadOnlyList<Account> _lastGood = Array.Empty<Account>();

    /// <summary>
    /// The accounts. The list lives on the UI thread; a copy taken from a timer thread while an account
    /// is being added can fail, and then the last good copy is used rather than "no accounts".
    /// </summary>
    private static IReadOnlyList<Account> All
    {
        get
        {
            try { return _lastGood = _accounts?.Invoke() ?? Array.Empty<Account>(); }
            catch { return _lastGood; }
        }
    }

    public static Account? For(long userId) => userId > 0 ? All.FirstOrDefault(a => a.UserId == userId) : null;

    // ---- Anti-AFK ----

    public static bool AfkOn(Account? a) => a?.Overrides.AfkEnabled ?? S.AntiAfkEnabled;

    /// <summary>True when Anti-AFK has anything to do: on globally, or on for at least one account.</summary>
    public static bool AnyAfkOn => S.AntiAfkEnabled || All.Any(a => a.Overrides.AfkEnabled == true);

    public static (int Min, int Max, bool Randomize) AfkInterval(Account? a)
        => a?.Overrides.AfkIntervalMinutes is int own
            ? (own, own, false)
            : (S.AntiAfkIntervalMinutes, S.AntiAfkIntervalMaxMinutes, S.AntiAfkRandomize);

    public static string AfkKey(Account? a) => a?.Overrides.AfkKey ?? S.AntiAfkKey;

    // ---- Recovery ----

    public static TimeSpan DisconnectWait(Account? a) => TimeSpan.FromMinutes(Math.Max(0, a?.Overrides.DisconnectMinutes ?? S.DisconnectMinutes));

    /// <summary>Minutes after which the account's client is restarted, or null for never.</summary>
    public static int? RestartMinutes(Account? a)
    {
        if (a?.Overrides.RestartMinutes is int own) return own > 0 ? own : null;
        return S.RestartClientsEnabled ? S.RestartClientsMinutes : null;
    }

    /// <summary>The memory-growth factor that restarts the account's client, or null for never.</summary>
    public static double? RamGrowthFactor(Account? a)
    {
        if (a?.Overrides.RamGrowthFactor is double own) return own > 0 ? own : null;
        return S.RestartOnRamGrowth ? S.RamGrowthFactor : null;
    }

    public static bool AnyRamGrowth => S.RestartOnRamGrowth || All.Any(a => a.Overrides.RamGrowthFactor > 0);

    // ---- for the settings pages ----

    /// <summary>"Own Anti-AFK settings: A, B", or empty when no account has any.</summary>
    public static string AfkOverridesText => Names(a => a.Overrides.HasAfk, "Overrides.AfkAccounts");

    /// <summary>"Own recovery settings: A, B", or empty when no account has any.</summary>
    public static string RecoveryOverridesText => Names(a => a.Overrides.HasRecovery, "Overrides.RecoveryAccounts");

    private static string Names(Func<Account, bool> has, string key)
    {
        var names = All.Where(has).Select(a => a.DisplayNameOrUser).ToList();
        return names.Count == 0 ? "" : L.T(key, string.Join(", ", names));
    }
}
