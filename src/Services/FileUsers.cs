using System.Runtime.InteropServices;

namespace RobloxAccountManager.Services;

/// <summary>
/// Which processes have a file open, asked of Windows' Restart Manager (the API installers use to
/// find what holds a file). A Roblox client keeps its own log open for as long as it runs, so this
/// ties a log to its client exactly, where start times only come close: six clients starting
/// within a minute created logs seconds apart and got each other's.
/// </summary>
public static class FileUsers
{
    /// <summary>Pids that have <paramref name="path"/> open (empty when none), or null when Windows could not say.</summary>
    public static IReadOnlyCollection<int>? Of(string path)
    {
        if (RmStartSession(out uint session, 0, Guid.NewGuid().ToString("N")) != 0) return null;
        try
        {
            if (RmRegisterResources(session, 1, new[] { path }, 0, null, 0, null) != 0) return null;

            uint needed = 0, count = 0;
            int rc = RmGetList(session, out needed, ref count, null, out _);
            if (rc == 0) return Array.Empty<int>();
            if (rc != ERROR_MORE_DATA) return null;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                var info = new RM_PROCESS_INFO[needed];
                count = needed;
                rc = RmGetList(session, out needed, ref count, info, out _);
                if (rc == 0) return info.Take((int)count).Select(i => i.Process.dwProcessId).ToArray();
                if (rc != ERROR_MORE_DATA) return null;
            }
            return null;
        }
        catch { return null; }
        finally { RmEndSession(session); }
    }

    private const int ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, out uint lpdwRebootReasons);
}
