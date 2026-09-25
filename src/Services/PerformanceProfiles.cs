namespace RobloxAccountManager.Services;

/// <summary>
/// Named bundles of the graphics options the manager already writes (FastFlags + the frame-rate
/// cap), applied for one launch. Only allowlisted flags are used — see <see cref="FFlagsService.Allowlist"/>.
///
/// Roblox reads both files once, when a client starts, and both are shared by every client on the
/// PC. A profile therefore affects the clients launched while it is applied, and the next launch
/// without a profile puts the normal settings back (see <see cref="UndoEdits"/>). There is no
/// per-client setting to change afterwards, and clients started at the same instant can race.
/// </summary>
public static class PerformanceProfiles
{
    public const string Normal = "";
    public const string UltraLowAfk = "UltraLowAfk";

    public static readonly string[] All = { Normal, UltraLowAfk };

    public static bool IsKnown(string? id) => Array.IndexOf(All, id ?? "") >= 0;

    /// <summary>Flags a profile sets on top of the user's normal FastFlags. Empty for the normal profile.</summary>
    public static Dictionary<string, string> Flags(string? id)
    {
        var f = new Dictionary<string, string>(StringComparer.Ordinal);
        if (id != UltraLowAfk) return f;

        f["DFIntDebugFRMQualityLevelOverride"] = "1";      // graphics quality 1
        f["FIntDebugForceMSAASamples"] = "0";              // anti-aliasing off
        f["DFFlagTextureQualityOverrideEnabled"] = "True";
        f["DFIntTextureQualityOverride"] = "0";            // lowest textures
        f["FIntFRMMinGrassDistance"] = "0";                // no grass
        f["FIntFRMMaxGrassDistance"] = "0";
        f["FFlagDebugSkyGray"] = "True";                   // gray sky
        f["DFFlagDebugPauseVoxelizer"] = "True";           // freeze lighting voxel updates
        return f;
    }

    /// <summary>Frame-rate cap the profile wants, or 0 to leave the normal cap in place.</summary>
    public static int FpsCap(string? id, int afkFpsCap) => id == UltraLowAfk ? Math.Clamp(afkFpsCap, 5, 1000) : 0;

    /// <summary>
    /// Undoes what an earlier profile launch left in the shared flag file. For every flag the profile
    /// wrote that this launch does not set itself, and whose value in the file is still exactly what
    /// the profile wrote, the value from before the profile comes back (or the flag is removed when
    /// there was none). A flag someone changed since — by hand or through a bootstrapper — is left alone.
    /// </summary>
    /// <returns>Flag → value to write, or null to delete it.</returns>
    public static Dictionary<string, string?> UndoEdits(
        IReadOnlyDictionary<string, string> writtenByProfile,
        IReadOnlyDictionary<string, string> previousValues,
        IReadOnlyDictionary<string, string> thisLaunch,
        IReadOnlyDictionary<string, string> inFile)
    {
        var edits = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, written) in writtenByProfile)
        {
            if (thisLaunch.ContainsKey(key)) continue;
            if (!inFile.TryGetValue(key, out var current) || !string.Equals(current, written, StringComparison.Ordinal)) continue;
            edits[key] = previousValues.TryGetValue(key, out var previous) ? previous : null;
        }
        return edits;
    }
}
