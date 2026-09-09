using System.Diagnostics;
using Microsoft.Win32;

namespace RobloxAccountManager.Services;

/// <summary>
/// "Start with Windows", via the per-user Run key.
///
/// HKCU is deliberate: the machine-wide key needs administrator rights, and an entry there would
/// start the manager for every account on the PC — including ones that have no data folder of
/// their own. The per-user key needs no elevation and follows the person who enabled it.
/// </summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Value name under the Run key. Stable — renaming it would orphan existing entries.</summary>
    private const string ValueName = "RobloxAccountManager";

    /// <summary>Passed to the auto-started copy so it can tell a boot from a normal launch.</summary>
    public const string StartupArg = "--startup";

    /// <summary>The exe an autostart entry has to point at.</summary>
    private static string? ExePath => Environment.ProcessPath;

    /// <summary>True when the Run entry exists and still points at this executable.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                if (key?.GetValue(ValueName) is not string command || command.Length == 0) return false;
                return ExePath == null || command.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Adds or removes the Run entry. Returns true when the registry now matches
    /// <paramref name="enabled"/> — a locked-down machine can refuse the write, and the caller
    /// needs to know rather than show a toggle that silently does nothing.
    /// </summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key == null) return false;

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            string? exe = ExePath;
            if (string.IsNullOrEmpty(exe)) return false;

            // Quoted: the default install path ("Roblox Account Manager.exe") contains spaces,
            // and an unquoted Run value would be parsed as "Roblox" plus two arguments.
            key.SetValue(ValueName, $"\"{exe}\" {StartupArg}", RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StartupService] Could not write the Run entry: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Re-points an existing entry at the current exe. The app is portable, so it can be moved
    /// or renamed between runs — and the stale entry would then start nothing at all, silently.
    /// Called on startup; does nothing when autostart is off.
    /// </summary>
    public static void Reconcile(bool shouldBeEnabled)
    {
        try
        {
            if (!shouldBeEnabled)
            {
                if (IsEnabled) Set(false);
                return;
            }
            if (!IsEnabled) Set(true);
        }
        catch { /* best-effort */ }
    }
}
