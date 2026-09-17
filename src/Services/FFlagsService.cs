using System.IO;
using System.Text.Json;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Writes FastFlags into ClientAppSettings.json before a launch.
///
/// Since September 2025 the Roblox client only honours flags on an allowlist and silently ignores
/// everything else. The convenience options therefore only ever produce allowlisted flags, and the
/// custom-flag editor points out the ones Roblox will ignore instead of letting them look applied.
/// The allowlist is Roblox's to change — it is kept here as data, and unknown flags are still
/// written (a future allowlist may accept them), just flagged in the UI.
/// </summary>
public static class FFlagsService
{
    /// <summary>Roblox's published allowlist for local client configuration.</summary>
    public static readonly HashSet<string> Allowlist = new(StringComparer.Ordinal)
    {
        // Geometry
        "DFIntCSGLevelOfDetailSwitchingDistance",
        "DFIntCSGLevelOfDetailSwitchingDistanceL12",
        "DFIntCSGLevelOfDetailSwitchingDistanceL23",
        "DFIntCSGLevelOfDetailSwitchingDistanceL34",
        // Rendering
        "FFlagHandleAltEnterFullscreenManually",
        "DFFlagTextureQualityOverrideEnabled",
        "DFIntTextureQualityOverride",
        "FIntDebugForceMSAASamples",
        "DFFlagDisableDPIScale",
        "FFlagDebugGraphicsPreferD3D11",
        "FFlagDebugSkyGray",
        "DFFlagDebugPauseVoxelizer",
        "DFIntDebugFRMQualityLevelOverride",
        "FIntFRMMaxGrassDistance",
        "FIntFRMMinGrassDistance",
        "FFlagDebugGraphicsPreferVulkan",
        "FFlagDebugGraphicsPreferOpenGL",
        // User interface
        "FIntGrassMovementReducedMotionFactor",
    };

    /// <summary>
    /// Everything a launch writes: the convenience options, the user's raw flags, then this account's
    /// own overrides. Also applies the frame-rate cap to Roblox's settings file. Returns how many
    /// flag files were written.
    /// </summary>
    public static int ApplyForLaunch(AppSettings s, Account? account = null)
    {
        if (s.FpsCap > 0)
        {
            try { RobloxClientSettingsService.WriteFramerateCap(s.FpsCap); } catch { }
        }

        var flags = s.ApplyFFlags ? BuildFlags(s) : new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in ParseRaw(account?.FFlags))
            flags[kv.Key] = kv.Value;

        return flags.Count == 0 ? 0 : Write(flags);
    }

    /// <summary>
    /// Parses a raw ClientAppSettings JSON blob into a flat string→string map. Non-string values are
    /// stringified so <c>{"X": true}</c> and <c>{"X": "True"}</c> behave the same. Invalid JSON yields
    /// an empty map rather than throwing — a half-typed blob must never block a launch.
    /// </summary>
    public static Dictionary<string, string> ParseRaw(string? json)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return map;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(prop.Name)) continue;
                map[prop.Name.Trim()] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.True => "True",
                    JsonValueKind.False => "False",
                    JsonValueKind.Null => "",
                    _ => prop.Value.GetRawText()
                };
            }
        }
        catch { }
        return map;
    }

    public static bool IsValidRaw(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return true;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch { return false; }
    }

    /// <summary>Flags in <paramref name="flags"/> the current client will ignore.</summary>
    public static List<string> NotAllowlisted(IEnumerable<string> flags)
        => flags.Where(f => !Allowlist.Contains(f)).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Writes the flags into every ClientSettings folder the installed client(s) read — for a
    /// bootstrapper (Bloxstrap, Fishstrap…) that is its own managed file. Existing flags are merged,
    /// not replaced: a bootstrapper's file is the user's configuration.
    /// </summary>
    private static int Write(Dictionary<string, string> flags)
    {
        int written = 0;
        foreach (var csDir in RobloxInstallService.FlagTargetDirectories())
        {
            try
            {
                Directory.CreateDirectory(csDir);
                string file = Path.Combine(csDir, "ClientAppSettings.json");

                var merged = new Dictionary<string, string>(StringComparer.Ordinal);
                if (File.Exists(file))
                    foreach (var kv in ParseRaw(File.ReadAllText(file)))
                        merged[kv.Key] = kv.Value;
                foreach (var kv in flags) merged[kv.Key] = kv.Value;

                string tmp = file + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, file, overwrite: true);
                written++;
            }
            catch (Exception ex)
            {
                DiagnosticsService.Warn("fflags", $"Could not write flags into {csDir}", ex);
            }
        }
        return written;
    }

    /// <summary>Removes ClientAppSettings.json from every flag target (revert to stock).</summary>
    public static int Clear()
    {
        int cleared = 0;
        foreach (var csDir in RobloxInstallService.FlagTargetDirectories())
        {
            try
            {
                var f = Path.Combine(csDir, "ClientAppSettings.json");
                if (File.Exists(f)) { File.Delete(f); cleared++; }
            }
            catch { }
        }
        return cleared;
    }

    /// <summary>Expands the convenience options plus the custom flags into the map Roblox expects.</summary>
    public static Dictionary<string, string> BuildFlags(AppSettings s)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);

        switch (s.GraphicsApi)
        {
            case "D3D11": flags["FFlagDebugGraphicsPreferD3D11"] = "True"; break;
            case "Vulkan": flags["FFlagDebugGraphicsPreferVulkan"] = "True"; break;
            case "OpenGL": flags["FFlagDebugGraphicsPreferOpenGL"] = "True"; break;
        }

        if (s.MsaaSamples is 0 or 1 or 2 or 4 or 8)
            flags["FIntDebugForceMSAASamples"] = s.MsaaSamples.ToString();

        if (s.TextureQuality is >= 0 and <= 3)
        {
            flags["DFFlagTextureQualityOverrideEnabled"] = "True";
            flags["DFIntTextureQualityOverride"] = s.TextureQuality.ToString();
        }

        if (s.QualityLevelOverride is >= 1 and <= 21)
            flags["DFIntDebugFRMQualityLevelOverride"] = s.QualityLevelOverride.ToString();

        if (s.DisableDpiScale) flags["DFFlagDisableDPIScale"] = "True";
        if (s.GraySky) flags["FFlagDebugSkyGray"] = "True";
        if (s.PauseVoxelizer) flags["DFFlagDebugPauseVoxelizer"] = "True";
        if (s.AltEnterFullscreen) flags["FFlagHandleAltEnterFullscreenManually"] = "False";
        if (s.HideGrass)
        {
            flags["FIntFRMMinGrassDistance"] = "0";
            flags["FIntFRMMaxGrassDistance"] = "0";
        }

        // Raw user flags win over the convenience options.
        foreach (var kv in s.CustomFFlags)
            if (!string.IsNullOrWhiteSpace(kv.Key))
                flags[kv.Key.Trim()] = kv.Value ?? "";

        return flags;
    }
}
