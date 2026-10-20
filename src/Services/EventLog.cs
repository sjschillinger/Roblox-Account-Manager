using System.IO;
using System.Text;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// A record of everything that happens to Roblox clients: every launch, client found, close, exit,
/// state change, recovery decision and schedule run, each with when it happened, which account,
/// pid and launch it concerns, and what caused it. Written by whoever acts, at the moment it acts,
/// so a shutdown or relaunch can be read back instead of reconstructed.
///
/// One file per UTC day in <c>data/events</c>. Settings → Diagnostics turns it off or changes how
/// many days are kept; whatever the setting, the files never take more than <see cref="CapBytes"/>.
/// Never contains cookies or private-server codes (targets are logged through their safe ToString).
/// Like <see cref="DiagnosticsService"/>, nothing here ever throws.
/// </summary>
public static class EventLog
{
    /// <summary>The most the event files may take together.</summary>
    public const long CapBytes = 50L * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8 = new(false);
    private static DateTime _lastTrimUtc = DateTime.MinValue;
    private static System.Threading.Timer? _snapshotTimer;

    /// <summary>Folder holding the day files.</summary>
    public static string Folder
    {
        get { try { return Paths.InData("events"); } catch { return ""; } }
    }

    /// <summary>"Alias (userId)", the subject of an event about an account.</summary>
    public static string Who(string alias, long userId) => userId > 0 ? $"{alias} ({userId})" : alias;

    public static string Who(Account acc) => Who(acc.DisplayNameOrUser, acc.UserId);

    /// <summary>Writes one event. <paramref name="fields"/> with a null or empty value are left out.</summary>
    public static void Write(string kind, string subject, params (string Key, object? Value)[] fields)
    {
        try
        {
            if (!SettingsService.Current.EventLogEnabled) return;
            var now = DateTime.UtcNow;
            string line = EventLine.Format(now, kind, subject, fields) + Environment.NewLine;
            lock (Gate)
            {
                string dir = Folder;
                if (dir.Length == 0) return;
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, EventLine.FileFor(now)), line, Utf8);
                if (now - _lastTrimUtc > TimeSpan.FromMinutes(10)) Trim(now);
            }
        }
        catch { /* the log must never break what it records */ }
    }

    /// <summary>Applies the retention setting (also called when it changes).</summary>
    public static void Trim()
    {
        try { lock (Gate) Trim(DateTime.UtcNow); } catch { }
    }

    private static void Trim(DateTime nowUtc)
    {
        _lastTrimUtc = nowUtc;
        var dir = new DirectoryInfo(Folder);
        if (!dir.Exists) return;
        var files = dir.GetFiles("events-*.log").Select(f => (f.Name, f.Length)).ToList();
        foreach (var name in EventLine.ToDelete(files, nowUtc, SettingsService.Current.EventLogDays, CapBytes))
        {
            try { File.Delete(Path.Combine(dir.FullName, name)); } catch { }
        }
    }

    /// <summary>Starts the hourly snapshot of what is running. Call once at startup.</summary>
    public static void Start()
    {
        Write("MANAGER", "started", ("version", typeof(EventLog).Assembly.GetName().Version), ("settings", RecoverySettings()));
        _snapshotTimer ??= new System.Threading.Timer(_ => Snapshot(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    public static void Stop()
    {
        _snapshotTimer?.Dispose();
        _snapshotTimer = null;
        Write("MANAGER", "stopped");
    }

    /// <summary>The settings that decide what recovery does, for the start line and whenever they change.</summary>
    public static string RecoverySettings()
    {
        var s = SettingsService.Current;
        return $"watchdog={(s.WatchdogEnabled ? "on" : "off")} rejoinOnDisconnect={(s.RejoinOnDisconnect ? "on" : "off")} "
             + $"disconnectWait={s.DisconnectMinutes}m timedRestart={(s.RestartClientsEnabled ? s.RestartClientsMinutes + "m" : "off")} "
             + $"ramGrowthRestart={(s.RestartOnRamGrowth ? s.RamGrowthFactor + "x" : "off")} closePrevious={(s.AutoCloseLastProcess ? "on" : "off")} "
             + $"antiAfk={(s.AntiAfkEnabled ? "on" : "off")}";
    }

    /// <summary>One line listing every running client: account, pid, uptime, state and window.</summary>
    public static void Snapshot()
    {
        try
        {
            var rows = ProcessRegistry.All.Select(t =>
            {
                var state = ClientLogWatcher.StateOf(t.Pid);
                string who = t.IsExternal ? "external" : t.Alias;
                string window = ProcessRegistry.WindowHandle(t.Pid) != IntPtr.Zero ? "" : ",no window";
                return $"{who}:{t.Pid}:{EventLine.Duration(t.Uptime)}:{state?.Phase.ToString() ?? "no log"}{window}";
            }).ToList();
            Write("SNAPSHOT", $"{rows.Count} client(s)", ("clients", string.Join(" | ", rows)));
        }
        catch { }
    }
}
