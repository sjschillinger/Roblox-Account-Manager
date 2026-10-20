using System.Runtime.InteropServices;

namespace RobloxAccountManager.Services;

/// <summary>
/// How much memory Windows has left. When the commit limit (RAM plus page file) is used up, no new
/// process can reserve memory and every client dies while starting, until something is closed or
/// the PC restarts — one thing a restart fixes that closing Roblox alone may not.
/// </summary>
public static class MemoryStatus
{
    /// <summary>Below this much free commit (or free RAM), a client can't be expected to start.</summary>
    private const long LowMb = 1536;

    /// <param name="Low">True when Windows is nearly out of memory.</param>
    public readonly record struct Snapshot(long FreeRamMb, long TotalRamMb, long FreeCommitMb, long CommitLimitMb, bool Low)
    {
        public override string ToString()
            => $"RAM {FreeRamMb:N0} of {TotalRamMb:N0} MB free, commit {FreeCommitMb:N0} of {CommitLimitMb:N0} MB free";
    }

    public static Snapshot? Read()
    {
        try
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref m)) return null;
            long Mb(ulong b) => (long)(b / (1024 * 1024));
            long freeRam = Mb(m.ullAvailPhys), freeCommit = Mb(m.ullAvailPageFile);
            return new Snapshot(freeRam, Mb(m.ullTotalPhys), freeCommit, Mb(m.ullTotalPageFile), freeRam < LowMb / 2 || freeCommit < LowMb);
        }
        catch { return null; }
    }

    /// <summary>The processes holding the most memory, for telling what used it up ("name 1,234 MB, …").</summary>
    public static string TopUsers(int count = 5)
    {
        var rows = new List<(string Name, long Mb)>();
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try { rows.Add((p.ProcessName, p.PrivateMemorySize64 / (1024 * 1024))); }
            catch { }
            finally { p.Dispose(); }
        }
        return string.Join(", ", rows.GroupBy(r => r.Name)
                                     .Select(g => (Name: g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key, Mb: g.Sum(r => r.Mb)))
                                     .OrderByDescending(r => r.Mb).Take(count)
                                     .Select(r => $"{r.Name} {r.Mb:N0} MB"));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
