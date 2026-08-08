using System.IO;
using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace RobloxAccountManager.Services;

/// <summary>
/// Finds the Roblox client that will actually run, wherever it lives.
///
/// The stock installer is only one of several layouts in the wild, and looking solely at
/// <c>%LOCALAPPDATA%\Roblox\Versions</c> misses most of the others:
///
///  - Third-party bootstrappers (Bloxstrap, Froststrap, Fishstrap, Voidstrap and friends) take
///    over the <c>roblox-player://</c> protocol and keep their own <c>Versions</c> folder under
///    their own directory. The stock folder may not exist at all on such a machine.
///  - Newer stock installs use the <c>UniversalApp</c> layout, where <c>Versions</c> is absent.
///  - Older installs sit under Program Files (x86).
///
/// The protocol handler is the authoritative answer to "what happens when we fire a
/// roblox-player: URI", so it is consulted first; the well-known locations are a fallback for
/// the case where the handler is missing or points at something unreadable.
/// </summary>
public static class RobloxInstallService
{
    private const string ClientExe = "RobloxPlayerBeta.exe";

    /// <summary>One discovered install root.</summary>
    /// <param name="VersionsRoot">Folder whose sub-directories are client versions.</param>
    /// <param name="LauncherName">"Roblox" for a stock install, else the bootstrapper's name.</param>
    /// <param name="ManagedSettingsDir">
    /// A bootstrapper's own ClientSettings folder, when it has one. Bootstrappers regenerate the
    /// per-version <c>ClientAppSettings.json</c> from this file on every launch, so it is the only
    /// place a flag written by us survives. Null for a stock install.
    /// </param>
    public sealed record InstallRoot(string VersionsRoot, string LauncherName, string? ManagedSettingsDir);

    /// <summary>
    /// Every install root on this machine, most authoritative first. Empty when Roblox genuinely
    /// isn't installed.
    /// </summary>
    public static IReadOnlyList<InstallRoot> FindRoots()
    {
        var roots = new List<InstallRoot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? versionsRoot, string launcher, string? managedSettings)
        {
            if (string.IsNullOrEmpty(versionsRoot)) return;
            try
            {
                if (!Directory.Exists(versionsRoot)) return;
                string full = Path.GetFullPath(versionsRoot);
                if (!seen.Add(full)) return;
                roots.Add(new InstallRoot(full, launcher, managedSettings));
            }
            catch { /* unreadable path — just skip it */ }
        }

        // 1. Whatever owns roblox-player://. This is what a launch actually invokes, so if a
        //    bootstrapper has taken the protocol over, its versions are the ones that matter.
        var handler = HandlerDirectory();
        if (handler != null)
        {
            string launcher = SafeName(handler);
            Add(Path.Combine(handler, "Versions"), launcher,
                ManagedSettingsIfPresent(Path.Combine(handler, "ClientSettings")));
        }

        // 2. Stock locations.
        Add(Path.Combine(Local, "Roblox", "Versions"), "Roblox", null);
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox", "Versions"),
            "Roblox", null);

        // 3. Any other bootstrapper installed side by side. One level deep under %LOCALAPPDATA%,
        //    which is cheap, and only folders that really contain a client are kept.
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(Local))
            {
                string versions = Path.Combine(dir, "Versions");
                if (!Directory.Exists(versions)) continue;
                if (!HasClient(versions)) continue;
                Add(versions, SafeName(dir), ManagedSettingsIfPresent(Path.Combine(dir, "ClientSettings")));
            }
        }
        catch { /* %LOCALAPPDATA% unreadable — the checks above already covered the usual cases */ }

        return roots;
    }

    /// <summary>Every version folder that contains a client binary, across all install roots.</summary>
    public static IEnumerable<string> VersionFolders()
    {
        foreach (var root in FindRoots())
        {
            List<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root.VersionsRoot).ToList(); }
            catch { continue; }

            foreach (string dir in dirs)
            {
                bool hasClient;
                try { hasClient = File.Exists(Path.Combine(dir, ClientExe)); }
                catch { continue; }
                if (hasClient) yield return dir;
            }
        }
    }

    /// <summary>
    /// Where FastFlags should be written. For a bootstrapper this is its managed ClientSettings
    /// folder — writing only into the version folder would be undone the next time it launches,
    /// because it rewrites that file from its own copy.
    /// </summary>
    public static IEnumerable<string> FlagTargetDirectories()
    {
        var targets = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in FindRoots())
        {
            if (root.ManagedSettingsDir != null)
            {
                if (seen.Add(root.ManagedSettingsDir)) targets.Add(root.ManagedSettingsDir);
                continue;   // the managed file is the source of truth for this launcher
            }

            List<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root.VersionsRoot).ToList(); }
            catch { continue; }

            foreach (string dir in dirs)
            {
                try { if (!File.Exists(Path.Combine(dir, ClientExe))) continue; }
                catch { continue; }
                string cs = Path.Combine(dir, "ClientSettings");
                if (seen.Add(cs)) targets.Add(cs);
            }
        }

        return targets;
    }

    /// <summary>
    /// A short human description of what is installed, for the self-check row.
    /// Returns null when no client could be found anywhere.
    /// </summary>
    public static string? DescribeInstall()
    {
        var byLauncher = new Dictionary<string, (int count, string newest, DateTime stamp)>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in FindRoots())
        {
            List<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root.VersionsRoot).ToList(); }
            catch { continue; }

            foreach (string dir in dirs)
            {
                try
                {
                    string exe = Path.Combine(dir, ClientExe);
                    if (!File.Exists(exe)) continue;
                    var stamp = File.GetLastWriteTimeUtc(exe);

                    byLauncher.TryGetValue(root.LauncherName, out var cur);
                    byLauncher[root.LauncherName] = stamp > cur.stamp
                        ? (cur.count + 1, Path.GetFileName(dir), stamp)
                        : (cur.count + 1, cur.newest ?? Path.GetFileName(dir), cur.stamp);
                }
                catch { /* one unreadable version folder must not hide the rest */ }
            }
        }

        if (byLauncher.Count == 0) return null;

        return string.Join(" · ", byLauncher.Select(kv =>
        {
            var (count, newest, _) = kv.Value;
            string extra = count > 1 ? $" (+{count - 1} older)" : "";
            return $"{kv.Key}: {newest}{extra}";
        }));
    }

    /// <summary>True when a client binary exists anywhere we know to look.</summary>
    public static bool IsInstalled() => VersionFolders().Any();

    /// <summary>Name of whatever currently owns roblox-player://, or null when nothing does.</summary>
    public static string? ProtocolOwner()
    {
        string? exe = HandlerExecutable();
        return exe == null ? null : Path.GetFileNameWithoutExtension(exe);
    }

    // ---------------------------------------------------------------- internals

    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string? ManagedSettingsIfPresent(string dir)
    {
        try { return Directory.Exists(dir) ? dir : null; }
        catch { return null; }
    }

    private static bool HasClient(string versionsRoot)
    {
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(versionsRoot))
            {
                try { if (File.Exists(Path.Combine(dir, ClientExe))) return true; }
                catch { }
            }
        }
        catch { }
        return false;
    }

    /// <summary>Folder of the executable registered for roblox-player://.</summary>
    private static string? HandlerDirectory()
    {
        try
        {
            string? exe = HandlerExecutable();
            return exe == null ? null : Path.GetDirectoryName(exe);
        }
        catch { return null; }
    }

    private static readonly Regex QuotedExe = new(@"^\s*""([^""]+)""|^\s*(\S+\.exe)", RegexOptions.IgnoreCase);

    /// <summary>
    /// Full path of the executable behind <c>HKCR\roblox-player\shell\open\command</c>.
    /// The command line is a template ("C:\...\Froststrap.exe" -player "%1"), so the exe has to
    /// be pulled out of it rather than used verbatim.
    /// </summary>
    private static string? HandlerExecutable()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"roblox-player\shell\open\command");
            if (key?.GetValue(null) is not string command || command.Length == 0) return null;

            var m = QuotedExe.Match(command);
            if (!m.Success) return null;
            string path = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    /// <summary>Folder name as a launcher label, e.g. "…\Local\Froststrap" -> "Froststrap".</summary>
    private static string SafeName(string dir)
    {
        try
        {
            string name = new DirectoryInfo(dir).Name;
            return string.IsNullOrWhiteSpace(name) ? "Roblox" : name;
        }
        catch { return "Roblox"; }
    }
}
