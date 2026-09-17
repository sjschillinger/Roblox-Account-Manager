using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace RobloxAccountManager.Services;

/// <summary>
/// Locks the manager behind the master password — on demand, after the PC has been idle for a while,
/// or when the window is hidden to the tray. While locked the window shows only the unlock screen,
/// and launches from hotkeys, the tray or the watchdog are refused.
///
/// This protects the running session from someone walking up to an unattended PC. It is not a
/// substitute for the encryption at rest: the store stays decrypted in memory while the app runs.
/// </summary>
public static class LockService
{
    private static AccountStore? _store;
    private static DispatcherTimer? _idleTimer;
    private static int _failedAttempts;
    private static DateTime _retryAfterUtc = DateTime.MinValue;

    public static bool IsLocked { get; private set; }

    /// <summary>Raised on the UI thread when the lock state changes.</summary>
    public static event Action? Changed;

    /// <summary>Locking needs something to unlock with.</summary>
    public static bool CanLock => _store?.MasterPassword is { Length: > 0 };

    public static void Init(AccountStore store)
    {
        _store = store;
        _idleTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => CheckIdle(),
            System.Windows.Application.Current.Dispatcher);
        _idleTimer.Start();
    }

    public static void Stop() => _idleTimer?.Stop();

    private static void CheckIdle()
    {
        var s = SettingsService.Current;
        if (IsLocked || !s.AutoLockEnabled || !CanLock) return;
        if (IdleTime() >= TimeSpan.FromMinutes(Math.Max(1, s.AutoLockMinutes)))
            Lock("idle");
    }

    /// <summary>Locks the app. Returns false when there is no master password to unlock with.</summary>
    public static bool Lock(string reason = "manual")
    {
        if (IsLocked) return true;
        if (!CanLock) return false;
        IsLocked = true;
        ClipboardService.ClearSecretIfPresent();
        AuditLogService.Log(AuditLogService.Category.Lock, $"App locked ({reason})");
        try { Changed?.Invoke(); } catch { }
        return true;
    }

    /// <summary>Seconds the unlock screen must wait before the next attempt (0 when it may try now).</summary>
    public static int RetryDelaySeconds => Math.Max(0, (int)Math.Ceiling((_retryAfterUtc - DateTime.UtcNow).TotalSeconds));

    /// <summary>
    /// Checks the password. Each wrong attempt after the third adds a growing wait, so guessing at the
    /// lock screen is slow; the count resets on success.
    /// </summary>
    public static bool TryUnlock(string password)
    {
        if (!IsLocked) return true;
        if (RetryDelaySeconds > 0) return false;

        if (_store?.VerifyMasterPassword(password) == true)
        {
            IsLocked = false;
            _failedAttempts = 0;
            _retryAfterUtc = DateTime.MinValue;
            AuditLogService.Log(AuditLogService.Category.Unlock, "App unlocked");
            try { Changed?.Invoke(); } catch { }
            return true;
        }

        _failedAttempts++;
        if (_failedAttempts >= 3)
            _retryAfterUtc = DateTime.UtcNow.AddSeconds(Math.Min(60, 2 * (_failedAttempts - 2)));
        AuditLogService.Log(AuditLogService.Category.Security, $"Failed unlock attempt #{_failedAttempts}");
        return false;
    }

    // ---- idle detection ----

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    /// <summary>Time since the last keyboard or mouse input anywhere on the desktop.</summary>
    private static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMs);
    }
}
