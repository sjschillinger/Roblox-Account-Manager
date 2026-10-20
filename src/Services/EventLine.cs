using System.Globalization;
using System.Text;

namespace RobloxAccountManager.Services;

/// <summary>
/// The pure parts of the event log (<see cref="EventLog"/>): how one event becomes a line, and which
/// day files the retention rules delete.
/// </summary>
public static class EventLine
{
    /// <summary>Longest a single value may be; a stray multi-kilobyte message must not swamp the log.</summary>
    private const int MaxValueChars = 300;

    /// <summary>
    /// One line: time, kind, who it is about, then <c>key=value</c> pairs. Values containing spaces are
    /// quoted; line breaks are flattened so every event stays on one line.
    /// <example><c>2026-10-12 20:00:14Z  EXIT      JapasBiggestFan (117)  pid=30104  uptime=8s  closedBy=nobody</c></example>
    /// </summary>
    public static string Format(DateTime utc, string kind, string subject, IEnumerable<(string Key, object? Value)> fields)
    {
        var sb = new StringBuilder(160);
        sb.Append(utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("Z  ");
        sb.Append(kind.PadRight(9)).Append(' ').Append(Clean(subject));
        foreach (var (key, value) in fields)
        {
            if (value == null) continue;
            string text = Clean(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
            if (text.Length == 0) continue;
            sb.Append("  ").Append(key).Append('=');
            if (text.Contains(' ') || text.Contains('=')) sb.Append('"').Append(text.Replace("\"", "'")).Append('"');
            else sb.Append(text);
        }
        return sb.ToString();
    }

    private static string Clean(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > MaxValueChars ? s[..MaxValueChars] + "…" : s;
    }

    /// <summary>"8s", "3m 05s", "14h 12m": short, and unambiguous when read back.</summary>
    public static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalMinutes < 1) return $"{(int)d.TotalSeconds}s";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m {d.Seconds:00}s";
        return $"{(int)d.TotalHours}h {d.Minutes:00}m";
    }

    /// <summary>File name of the log for a UTC day.</summary>
    public static string FileFor(DateTime utc) => $"events-{utc:yyyy-MM-dd}.log";

    /// <summary>The UTC day a log file belongs to, or null for a name that isn't one of ours.</summary>
    public static DateTime? DayOf(string fileName)
        => fileName.StartsWith("events-", StringComparison.Ordinal) && fileName.EndsWith(".log", StringComparison.Ordinal)
           && DateTime.TryParseExact(fileName[7..^4], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                     DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var day)
            ? day : null;

    /// <summary>
    /// Day files to delete: those older than <paramref name="keepDays"/> days, then the oldest remaining
    /// ones until the rest fit in <paramref name="capBytes"/>. Today's file is never deleted.
    /// </summary>
    public static IReadOnlyList<string> ToDelete(IEnumerable<(string Name, long Bytes)> files, DateTime todayUtc, int keepDays, long capBytes)
    {
        var days = files.Select(f => (f.Name, f.Bytes, Day: DayOf(f.Name)))
                        .Where(f => f.Day != null)
                        .OrderBy(f => f.Day)
                        .ToList();
        var today = todayUtc.Date;
        var oldest = today.AddDays(-(Math.Max(1, keepDays) - 1));
        var delete = days.Where(f => f.Day < oldest).Select(f => f.Name).ToList();

        var kept = days.Where(f => f.Day >= oldest).ToList();
        long total = kept.Sum(f => f.Bytes);
        foreach (var f in kept)
        {
            if (total <= capBytes || f.Day >= today) break;
            delete.Add(f.Name);
            total -= f.Bytes;
        }
        return delete;
    }
}
