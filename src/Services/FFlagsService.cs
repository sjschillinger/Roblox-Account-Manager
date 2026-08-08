using System.IO;
using System.Text.Json;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Writes FastFlags to <c>%LOCALAPPDATA%\Roblox\Versions\&lt;hash&gt;\ClientSettings\ClientAppSettings.json</c>.
/// Convenience toggles in <see cref="AppSettings"/> expand into well-known flags; the user's
/// raw <see cref="AppSettings.CustomFFlags"/> are merged last so they always win.
/// </summary>
public static class FFlagsService
{
    /// <summary>
    /// Everything the launch path needs to write before starting a client: the FPS cap from
    /// "Unlock FPS", the FastFlag toggles, the user's raw flags, and finally this account's own
    /// per-account overrides. Returns how many version folders were written (0 = nothing to do).
    ///
    /// This is what actually makes "Unlock FPS" work. It used to write a single
    /// <c>%LOCALAPPDATA%\Roblox\ClientSettings\ClientAppSettings.json</c>, a path the modern
    /// client does not read — the cap was silently ignored on every launch. The client reads
    /// per-version settings, which is where <see cref="VersionFolders"/> points.
    /// </summary>
    public static int ApplyForLaunch(AppSettings s, Account? account = null)
    {
        var flags = s.ApplyFFlags ? BuildFlags(s) : new Dictionary<string, string>(StringComparer.Ordinal);

        // "Unlock FPS" is its own toggle, independent of the FastFlags section, but both end up
        // in the same file — merged here so enabling one never wipes the other's settings.
        if (s.UnlockFps)
            flags["DFIntTaskSchedulerTargetFps"] = s.MaxFps > 0 ? s.MaxFps.ToString() : "9999";

        foreach (var kv in ParseRaw(account?.FFlags))
            flags[kv.Key] = kv.Value;

        return flags.Count == 0 ? 0 : Write(flags);
    }

    /// <summary>
    /// Parses a raw ClientAppSettings JSON blob (per-account overrides, or the custom-flags box)
    /// into a flat string→string map. Non-string values are stringified so <c>{"X": true}</c> and
    /// <c>{"X": "True"}</c> behave the same. Invalid JSON yields an empty map rather than throwing.
    /// </summary>
    public static Dictionary<string, string> ParseRaw(string? json)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return map;
        try
        {
            using var doc = JsonDocument.Parse(json);
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
        catch { /* a half-typed flag blob must never block a launch */ }
        return map;
    }

    /// <summary>True when the text parses as a flag object (drives the editor's validity hint).</summary>
    public static bool IsValidRaw(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return true;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch { return false; }
    }

    /// <summary>
    /// Writes the flags into every ClientSettings folder that the installed client(s) actually
    /// read — which, on a machine running a bootstrapper such as Bloxstrap or Froststrap, is the
    /// launcher's own managed file rather than a version folder. See
    /// <see cref="RobloxInstallService.FlagTargetDirectories"/>.
    ///
    /// Existing flags are <em>merged</em>, not replaced. A bootstrapper's settings file is the
    /// user's own configuration; overwriting it wholesale would silently wipe every flag they
    /// had set there. Ours win on a key collision, theirs survive otherwise.
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

                File.WriteAllText(file,
                    JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true }));
                written++;
            }
            catch { /* a locked/permission-denied folder shouldn't block the rest */ }
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

    /// <summary>Expands toggles + custom flags into the final string→string map Roblox expects.</summary>
    public static Dictionary<string, string> BuildFlags(AppSettings s)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);

        if (s.FFlagUnlockFps)
        {
            // The scheduler cap is what actually gates FPS; 0 = uncapped, then set a high target.
            flags["DFIntTaskSchedulerTargetFps"] = s.MaxFps > 0 ? s.MaxFps.ToString() : "9999";
            flags["FFlagDebugGraphicsDisableDirect3D11"] = "False";
        }
        if (s.FFlagDisableVoiceChat)
        {
            flags["FFlagDisableVoiceChat"] = "True";
            flags["FFlagEnableVoiceChatSpatialAudio"] = "False";
        }
        if (s.FFlagDisableTelemetry)
        {
            flags["FFlagDebugDisableTelemetryEphemeralCounter"] = "True";
            flags["FFlagDebugDisableTelemetryEventIngest"] = "True";
            flags["FFlagDebugDisableTelemetryPoint"] = "True";
            flags["FFlagDebugDisableTelemetryV2Counter"] = "True";
        }
        if (s.FFlagLightingTechVoxel)
        {
            flags["FFlagDebugForceFutureIsBrightPhase3"] = "False";
            flags["DFFlagDebugRenderForceTechnologyVoxel"] = "True";
        }

        // Raw user flags win over the convenience toggles.
        foreach (var kv in s.CustomFFlags)
            if (!string.IsNullOrWhiteSpace(kv.Key))
                flags[kv.Key.Trim()] = kv.Value ?? "";

        return flags;
    }

}
