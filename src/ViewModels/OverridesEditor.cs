using System.Globalization;
using RobloxAccountManager.Models;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.ViewModels;

/// <summary>
/// Edits one account's own values (<see cref="AccountOverrides"/>) on the inspector's Automation tab.
/// Text boxes: empty = use the global setting; for restarts "0" = never. Combos offer "Use global".
/// Every change is saved and written to the event log.
/// </summary>
public sealed class OverridesEditor : ObservableObject
{
    private readonly Account _acc;
    private readonly Action _save;
    private readonly Func<(string Place, string Server)> _launchBar;

    public OverridesEditor(Account acc, Action save, Func<(string Place, string Server)> launchBar)
    {
        _acc = acc;
        _save = save;
        _launchBar = launchBar;
        AfkModeChoices = new[]
        {
            new Choice("global", L.T("Overrides.UseGlobal", L.T(G.AntiAfkEnabled ? "Common.On" : "Common.Off"))),
            new Choice("on", L.T("Common.On")),
            new Choice("off", L.T("Common.Off")),
        };
        AfkKeyChoices = new[] { new Choice("", L.T("Overrides.UseGlobal", G.AntiAfkKey)) }
            .Concat(new[] { "Space", "Shift", "Ctrl", "W", "A", "S", "D", "0", "F13" }.Select(k => new Choice(k, k))).ToList();
        UseLaunchBarCommand = new RelayCommand(_ => UseLaunchBar());
        ClearCommand = new RelayCommand(_ => Clear());
    }

    private AccountOverrides Ov => _acc.Overrides;
    private static AppSettings G => SettingsService.Current;

    // ---- Anti-AFK ----

    public IReadOnlyList<Choice> AfkModeChoices { get; }

    public string AfkMode
    {
        get => Ov.AfkEnabled switch { true => "on", false => "off", _ => "global" };
        set
        {
            if (value == null) return;   // a combo being rebound, not a choice
            Ov.AfkEnabled = value switch { "on" => true, "off" => false, _ => null };
            Commit("Anti-AFK", value);
        }
    }

    public IReadOnlyList<Choice> AfkKeyChoices { get; }

    public string AfkKey
    {
        get => Ov.AfkKey ?? "";
        set
        {
            if (value == null) return;
            Ov.AfkKey = value.Length == 0 ? null : value;
            Commit("Anti-AFK key", value);
        }
    }

    public string AfkInterval
    {
        get => Ov.AfkIntervalMinutes?.ToString() ?? "";
        set { Ov.AfkIntervalMinutes = ParseOwn(value, 1, 19); Commit("Anti-AFK interval", value); }
    }

    public string AfkIntervalGlobal => L.T("Overrides.UseGlobal", G.AntiAfkRandomize
        ? $"{G.AntiAfkIntervalMinutes}–{G.AntiAfkIntervalMaxMinutes} {L.T("Common.Unit.Minutes")}"
        : $"{G.AntiAfkIntervalMinutes} {L.T("Common.Unit.Minutes")}");

    // ---- recovery ----

    public string DisconnectMinutes
    {
        get => Ov.DisconnectMinutes?.ToString() ?? "";
        set { Ov.DisconnectMinutes = ParseOwn(value, 1, 60); Commit("disconnect wait", value); }
    }

    public string DisconnectGlobal => L.T("Overrides.UseGlobal", $"{G.DisconnectMinutes} {L.T("Common.Unit.Minutes")}");

    public string RestartMinutes
    {
        get => Ov.RestartMinutes?.ToString() ?? "";
        set
        {
            int? v = ParseOwn(value, 0, 24 * 60);
            Ov.RestartMinutes = v is > 0 and < 30 ? 30 : v;   // same floor as the global setting
            Commit("timed restart", value);
        }
    }

    public string RestartGlobal => L.T("Overrides.UseGlobal",
        G.RestartClientsEnabled ? $"{G.RestartClientsMinutes} {L.T("Common.Unit.Minutes")}" : L.T("Common.Off"));

    public string RamGrowth
    {
        get => Ov.RamGrowthFactor?.ToString("0.#", CultureInfo.InvariantCulture) ?? "";
        set
        {
            string t = (value ?? "").Trim().Replace(',', '.');
            if (t.Length == 0) Ov.RamGrowthFactor = null;
            else if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double f))
                Ov.RamGrowthFactor = f <= 0 ? 0 : Math.Clamp(f, 1.2, 10);
            Commit("memory-growth restart", value);
        }
    }

    public string RamGrowthGlobal => L.T("Overrides.UseGlobal", G.RestartOnRamGrowth ? $"{G.RamGrowthFactor:0.#}×" : L.T("Common.Off"));

    // ---- default destination ----

    public string DefaultPlace
    {
        get => Ov.DefaultPlaceId > 0 ? Ov.DefaultPlaceId.ToString() : "";
        set { Ov.DefaultPlaceId = Math.Max(0, JoinLinks.ParsePlaceId(value ?? "")); Commit("default destination", null); }
    }

    public string DefaultServer
    {
        get => Ov.DefaultServer;
        set { Ov.DefaultServer = (value ?? "").Trim(); Commit("default destination", null); }
    }

    public RelayCommand UseLaunchBarCommand { get; }
    public RelayCommand ClearCommand { get; }

    private void UseLaunchBar()
    {
        var (place, server) = _launchBar();
        Ov.DefaultPlaceId = Math.Max(0, JoinLinks.ParsePlaceId(place ?? ""));
        Ov.DefaultServer = (server ?? "").Trim();
        Commit("default destination", "from the launch bar");
        OnPropertyChanged(string.Empty);
    }

    private void Clear()
    {
        _acc.Overrides = new AccountOverrides();
        Commit("all own values", "cleared");
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Empty = use the global setting; otherwise the number, clamped.</summary>
    private static int? ParseOwn(string? text, int min, int max)
        => int.TryParse((text ?? "").Trim(), out int n) ? Math.Clamp(n, min, max) : null;

    private void Commit(string what, string? value)
    {
        _save();
        AntiAfkService.Apply();
        RamMonitorService.Apply();
        EventLog.Write("SETTINGS", EventLog.Who(_acc), ("changed", what), ("value", value), ("by", "user: account panel"));
    }
}
