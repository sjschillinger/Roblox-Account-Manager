using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace RobloxAccountManager.Services;

/// <summary>
/// Reads another process's command line (NtQueryInformationProcess, ProcessCommandLineInformation),
/// which needs only the limited query right Roblox clients grant. A client's command line never
/// changes, so the parsed <see cref="ClientIdentity"/> is cached per process (pid + start time).
/// </summary>
public static class ProcessCommandLine
{
    private static readonly ConcurrentDictionary<(int Pid, DateTime Start), ClientIdentity?> _cache = new();
    private static int _failures;

    /// <summary>The client's identity, or null when it can't be read or carries no tracker id.</summary>
    public static ClientIdentity? IdentityOf(int pid, DateTime startLocal) => TryRead(pid, startLocal, out var id) ? id : null;

    /// <summary>
    /// False when the command line couldn't be read at all. True otherwise, with <paramref name="id"/>
    /// null for a client started without a tracker id (not one of the manager's launches).
    /// </summary>
    public static bool TryRead(int pid, DateTime startLocal, out ClientIdentity? id)
    {
        if (_cache.TryGetValue((pid, startLocal), out id)) return true;
        string? line = Read(pid);
        if (line == null)
        {
            // Not cached: a process that is still starting up may be readable a moment later.
            if (Interlocked.Increment(ref _failures) is 1 or 100)
                DiagnosticsService.Warn("clients", $"Could not read the command line of Roblox client pid {pid}; falling back to start times to tell clients apart");
            return false;
        }
        id = ClientIdentity.Parse(line);
        _cache[(pid, startLocal)] = id;
        return true;
    }

    /// <summary>
    /// The command line for the diagnostics log, with the auth ticket and other launch secrets cut out
    /// and at most 300 characters long; null when it can't be read.
    /// </summary>
    public static string? ForLog(int pid)
    {
        string? line = Read(pid);
        if (line == null) return null;
        line = System.Text.RegularExpressions.Regex.Replace(line, @"(gameinfo:|ticket=|accessCode=|linkCode=|-t\s+)[^+&\s""]*", "$1…",
                                                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return line.Length <= 300 ? line : line[..300] + "…";
    }

    /// <summary>Drops cached entries for processes that are no longer running.</summary>
    public static void Forget(IEnumerable<int> runningPids)
    {
        var live = runningPids.ToHashSet();
        foreach (var key in _cache.Keys.Where(k => !live.Contains(k.Pid)).ToList()) _cache.TryRemove(key, out _);
    }

    private static string? Read(int pid)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            int length = 4096;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    int status = NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, length, out int needed);
                    if (status is STATUS_INFO_LENGTH_MISMATCH or STATUS_BUFFER_TOO_SMALL or STATUS_BUFFER_OVERFLOW)
                    {
                        length = Math.Max(needed, length * 2);
                        if (length > 1 << 20) return null;
                        continue;
                    }
                    if (status < 0) return null;

                    // UNICODE_STRING: Length (bytes), MaximumLength, then the buffer pointer.
                    ushort bytes = (ushort)Marshal.ReadInt16(buffer, 0);
                    IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                    return bytes == 0 || text == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text, bytes / 2);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return null;
        }
        catch { return null; }
        finally { CloseHandle(process); }
    }

    private const int ProcessCommandLineInformation = 60;   // Windows 8.1+
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const int STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0000023);
    private const int STATUS_BUFFER_OVERFLOW = unchecked((int)0x80000005);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
