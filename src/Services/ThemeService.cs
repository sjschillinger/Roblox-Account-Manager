using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Runtime theme engine. A theme is three layers, applied in order:
/// <list type="number">
///   <item><description>the <b>mode</b> — a complete dark or light base palette (or whatever Windows uses, for System);</description></item>
///   <item><description>the <b>accent</b> — a handful of keys tuned separately for each mode, so an accent stays legible on both;</description></item>
///   <item><description>the user's <b>custom overrides</b> from the colour editor.</description></item>
/// </list>
/// Every colour is published into the application resources as both a <see cref="Color"/> and a
/// <c>…Brush</c>. Brushes are created once and then recoloured in place, so anything holding a
/// brush — a <c>DynamicResource</c>, a converter result, a code-behind reference — follows a theme
/// switch without having to be re-evaluated.
/// </summary>
public static class ThemeService
{
    public const string ModeDark = "Dark";
    public const string ModeLight = "Light";
    public const string ModeSystem = "System";

    public static readonly string[] Modes = { ModeDark, ModeLight, ModeSystem };

    public static readonly string[] AccentNames = { "Mono", "Iris", "Ocean", "Emerald", "Amber", "Rose" };

    /// <summary>Keys the colour editor exposes, grouped for display. Labels are localized as Theme.Key.&lt;Key&gt;.</summary>
    public static readonly (string Key, string Group)[] Editable =
    {
        ("Canvas", "Surfaces"), ("Chrome", "Surfaces"), ("Surface", "Surfaces"), ("SurfaceAlt", "Surfaces"),
        ("Elevated", "Surfaces"), ("Selected", "Surfaces"), ("Hairline", "Surfaces"), ("HairlineStrong", "Surfaces"),
        ("TextPrimary", "Text"), ("TextSecondary", "Text"), ("TextMuted", "Text"),
        ("Accent", "Accent"), ("AccentHover", "Accent"), ("AccentPressed", "Accent"), ("AccentSoft", "Accent"), ("OnAccent", "Accent"),
        ("Success", "Status"), ("Warning", "Status"), ("Danger", "Status"), ("Info", "Status"),
        ("PresenceOnline", "Presence"), ("PresenceInGame", "Presence"), ("PresenceStudio", "Presence"), ("PresenceOffline", "Presence"),
    };

    private static readonly Dictionary<string, string> Dark = new()
    {
        ["Canvas"] = "#101216", ["Chrome"] = "#0B0C0F", ["Surface"] = "#15171C", ["SurfaceAlt"] = "#1B1E24",
        ["Elevated"] = "#1E2128", ["HoverOverlay"] = "#0DFFFFFF", ["PressedOverlay"] = "#17FFFFFF",
        ["Selected"] = "#232733", ["Hairline"] = "#22252C", ["HairlineStrong"] = "#2E323B",
        ["TextPrimary"] = "#EDEEF2", ["TextSecondary"] = "#A2A7B2", ["TextMuted"] = "#6C717C",
        ["Success"] = "#34C77B", ["SuccessSoft"] = "#1A2E24", ["Warning"] = "#F2A93B", ["WarningSoft"] = "#2F2616",
        ["Danger"] = "#F0616A", ["DangerSoft"] = "#301B1E", ["Info"] = "#4AA8FF", ["InfoSoft"] = "#172636",
        ["PresenceOnline"] = "#3AA0FF", ["PresenceInGame"] = "#2FBF71", ["PresenceStudio"] = "#F5A623", ["PresenceOffline"] = "#5A5F69",
        ["Scrim"] = "#99050608",
    };

    private static readonly Dictionary<string, string> Light = new()
    {
        ["Canvas"] = "#F5F6F8", ["Chrome"] = "#ECEEF1", ["Surface"] = "#FFFFFF", ["SurfaceAlt"] = "#F2F3F6",
        ["Elevated"] = "#FFFFFF", ["HoverOverlay"] = "#0A10131A", ["PressedOverlay"] = "#1410131A",
        ["Selected"] = "#E7E9EE", ["Hairline"] = "#E3E5EA", ["HairlineStrong"] = "#D0D4DB",
        ["TextPrimary"] = "#14161B", ["TextSecondary"] = "#4A505B", ["TextMuted"] = "#858B96",
        ["Success"] = "#15994F", ["SuccessSoft"] = "#DDF3E6", ["Warning"] = "#B7700A", ["WarningSoft"] = "#FBEFD9",
        ["Danger"] = "#D6303E", ["DangerSoft"] = "#FBE3E5", ["Info"] = "#1B74D8", ["InfoSoft"] = "#E1EEFB",
        ["PresenceOnline"] = "#1686F5", ["PresenceInGame"] = "#15994F", ["PresenceStudio"] = "#D98200", ["PresenceOffline"] = "#9AA0A9",
        ["Scrim"] = "#5910131A",
    };

    // Accent keys per accent, per mode: Accent, AccentHover, AccentPressed, AccentSoft, OnAccent, Focus.
    private static readonly Dictionary<string, (string[] Dark, string[] Light)> Accents = new()
    {
        ["Mono"]    = (new[] { "#EDEEF2", "#FFFFFF", "#D2D4DA", "#262932", "#0E0F12", "#8A93A6" },
                       new[] { "#16181D", "#2B2E36", "#000000", "#E5E7EB", "#FFFFFF", "#6B7280" }),
        ["Iris"]    = (new[] { "#8E90FF", "#A2A4FF", "#7679F0", "#24253F", "#0C0C1A", "#8E90FF" },
                       new[] { "#5155DF", "#6166F0", "#4145C6", "#E8E8FD", "#FFFFFF", "#5155DF" }),
        ["Ocean"]   = (new[] { "#38BDF8", "#5CCBFA", "#23A6E0", "#12293A", "#03121C", "#38BDF8" },
                       new[] { "#0277BD", "#0A8FD8", "#01609A", "#DFF1FC", "#FFFFFF", "#0277BD" }),
        ["Emerald"] = (new[] { "#34D399", "#52DDAB", "#22B884", "#12302A", "#04140D", "#34D399" },
                       new[] { "#047857", "#059669", "#065F46", "#DDF5EC", "#FFFFFF", "#047857" }),
        ["Amber"]   = (new[] { "#F5B841", "#FFC85A", "#E0A62E", "#33290F", "#1A1204", "#F5B841" },
                       new[] { "#A55A06", "#C06A08", "#874A05", "#FBEFDC", "#FFFFFF", "#A55A06" }),
        ["Rose"]    = (new[] { "#FB7185", "#FF8A9B", "#E85D71", "#3A1A20", "#1C0709", "#FB7185" },
                       new[] { "#D0214A", "#E43A60", "#AE173C", "#FDE4E9", "#FFFFFF", "#D0214A" }),
    };

    private static readonly string[] AccentKeys = { "Accent", "AccentHover", "AccentPressed", "AccentSoft", "OnAccent", "Focus" };

    /// <summary>True while the light palette is on screen.</summary>
    public static bool IsLight { get; private set; }

    /// <summary>Raised on the UI thread after a theme was applied (window chrome listens to recolour its border).</summary>
    public static event Action? Changed;

    /// <summary>Keys whose brush this service created, and therefore may recolour in place.</summary>
    private static readonly HashSet<string> _ownedBrushes = new();

    /// <summary>Whether Windows is set to light apps. Read fresh every time — the user can flip it while we run.</summary>
    public static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    public static bool ResolvesToLight(AppSettings s) => s.ThemeMode switch
    {
        ModeLight => true,
        ModeSystem => SystemPrefersLight(),
        _ => false,
    };

    /// <summary>The effective palette for a settings object: mode, then accent, then custom overrides.</summary>
    public static Dictionary<string, string> Resolve(AppSettings s)
    {
        bool light = ResolvesToLight(s);
        var map = new Dictionary<string, string>(light ? Light : Dark);

        string accent = Accents.ContainsKey(s.AccentName ?? "") ? s.AccentName! : "Mono";
        var tones = light ? Accents[accent].Light : Accents[accent].Dark;
        for (int i = 0; i < AccentKeys.Length; i++) map[AccentKeys[i]] = tones[i];

        if (s.CustomTheme != null)
            foreach (var kv in s.CustomTheme)
                if (TryColor(kv.Value, out _)) map[kv.Key] = kv.Value;

        return map;
    }

    /// <summary>Swatch colour for an accent in the currently effective mode — drives the accent picker.</summary>
    public static string AccentPreview(string accentName)
    {
        if (!Accents.TryGetValue(accentName, out var tones)) tones = Accents["Mono"];
        return IsLight ? tones.Light[0] : tones.Dark[0];
    }

    /// <summary>The value a key would have without the user's custom overrides (the editor's "reset" target).</summary>
    public static string BaselineHex(AppSettings s, string key)
    {
        var copy = new AppSettings { ThemeMode = s.ThemeMode, AccentName = s.AccentName, CustomTheme = new() };
        return Resolve(copy).TryGetValue(key, out var hex) ? hex : "#000000";
    }

    public static void Apply(AppSettings s)
    {
        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.Invoke(() => Apply(s)); return; }

        var palette = Resolve(s);
        IsLight = ResolvesToLight(s);
        var res = app.Resources;

        foreach (var kv in palette)
        {
            if (!TryColor(kv.Value, out var color)) continue;
            res[kv.Key] = color;

            string brushKey = kv.Key + "Brush";
            if (_ownedBrushes.Contains(brushKey) && res[brushKey] is SolidColorBrush existing && !existing.IsFrozen)
            {
                existing.Color = color;
            }
            else
            {
                res[brushKey] = new SolidColorBrush(color);
                _ownedBrushes.Add(brushKey);
            }
        }

        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>Parses "#RRGGBB" / "#AARRGGBB" / named colours; false on anything else.</summary>
    public static bool TryColor(string? hex, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try { color = (Color)ColorConverter.ConvertFromString(hex.Trim()); return true; }
        catch { return false; }
    }

    /// <summary>Brush for a palette key from the live resources, with a neutral fallback.</summary>
    public static Brush BrushFor(string key)
        => Application.Current?.TryFindResource(key + "Brush") as Brush ?? Brushes.Gray;

    /// <summary>Hex string of a live palette colour (e.g. for a swatch that shows the current value).</summary>
    public static string HexOf(string key)
    {
        if (Application.Current?.TryFindResource(key) is Color c)
            return c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        return "#000000";
    }
}
