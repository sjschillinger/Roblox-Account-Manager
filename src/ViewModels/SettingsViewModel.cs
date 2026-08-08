using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using RobloxAccountManager.Models;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.ViewModels;

/// <summary>One editable swatch in the theme editor. Setting <see cref="Hex"/>
/// writes the override into settings and repaints the app live.</summary>
public class ThemeColorRow : ObservableObject
{
    private readonly System.Action<string, string> _onChanged;
    public string Key { get; }
    public string Label { get; }
    public string Group { get; }

    private string _hex;
    public string Hex
    {
        get => _hex;
        set
        {
            var v = (value ?? "").Trim();
            if (v == _hex) return;
            _hex = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsValid));
            if (ThemeService.TryColor(v, out _)) _onChanged(Key, v);
        }
    }

    public bool IsValid => ThemeService.TryColor(_hex, out _);

    public ThemeColorRow(string key, string label, string group, string hex,
                         System.Action<string, string> onChanged)
    {
        Key = key; Label = label; Group = group; _hex = hex; _onChanged = onChanged;
    }

    /// <summary>Silently updates the shown value without firing the change hook
    /// (used when a preset switch rewrites the whole palette).</summary>
    public void SetSilently(string hex)
    {
        _hex = hex;
        OnPropertyChanged(nameof(Hex));
        OnPropertyChanged(nameof(IsValid));
    }
}

public class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private AppSettings S => SettingsService.Current;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        SetPasswordCommand = new RelayCommand(_ => SetPassword());
        RemovePasswordCommand = new RelayCommand(_ => RemovePassword());
        OpenDataFolderCommand = new RelayCommand(_ => OpenDataFolder());
        DownloadChromiumCommand = new RelayCommand(_ => DownloadChromium());
        OpenPluginsFolderCommand = new RelayCommand(_ => OpenPluginsFolder());
        ReloadPluginsCommand = new RelayCommand(_ => ReloadPlugins());
        ResetThemeCommand = new RelayCommand(_ => ResetTheme());
        TestWebhookCommand = new RelayCommand(_ => TestWebhook());
        FixMultiInstanceCommand = new RelayCommand(_ => FixMultiInstance());
        RestartElevatedCommand = new RelayCommand(_ => RestartElevated());
        RunHealthCheckCommand = new RelayCommand(_ => RunHealthCheck());
        OpenDiagnosticsCommand = new RelayCommand(_ => DiagnosticsService.OpenLogFolder());
        ClearDiagnosticsCommand = new RelayCommand(_ =>
        {
            DiagnosticsService.Clear();
            OnPropertyChanged(nameof(DiagnosticsStatus));
            _main.SetStatus("Diagnostics log cleared.");
        });
        ArrangeWindowsCommand = new RelayCommand(_ => ReportClientAction(InstanceControlService.ArrangeGrid(), "arranged"));
        MinimizeClientsCommand = new RelayCommand(_ => ReportClientAction(InstanceControlService.MinimizeAll(), "minimized"));
        RestoreClientsCommand = new RelayCommand(_ => ReportClientAction(InstanceControlService.RestoreAll(), "restored"));

        TestAntiAfkCommand = new RelayCommand(_ => TestAntiAfk());
        ApplyFFlagsNowCommand = new RelayCommand(_ => ApplyFFlagsNow());
        ClearFFlagsCommand = new RelayCommand(_ => ClearFFlags());
        GenerateWebApiTokenCommand = new RelayCommand(_ => { WebApiToken = NewToken(); _main.SetStatus("New API token generated."); });
        CopyWebApiTokenCommand = new RelayCommand(_ => CopyWebApiToken());
        TestProxyCommand = new RelayCommand(_ => TestProxy());
        ExportBackupCommand = new RelayCommand(_ => ExportBackup());
        ImportBackupCommand = new RelayCommand(_ => ImportBackup());
        ValidateCookiesNowCommand = new RelayCommand(_ => ValidateCookiesNow());

        BuildThemeRows();
        BuildHotkeyRows();

        // Seed the flag editor from the saved map so the box shows what is actually applied.
        _customFFlagsText = S.CustomFFlags.Count == 0
            ? ""
            : System.Text.Json.JsonSerializer.Serialize(S.CustomFFlags,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        // The guard reports asynchronously (its watcher runs on a timer); mirror that into the
        // settings page so the status line is live instead of a snapshot from page load.
        RobloxSingletonService.StatusChanged += RefreshSingletonState;

        // Live RAM readout follows the monitor's sampling tick.
        RamMonitorService.Sampled += _ => RefreshRamStatus();
    }

    // ---- Discord webhook ----
    public string DiscordWebhookUrl { get => S.DiscordWebhookUrl; set { S.DiscordWebhookUrl = value; Persist(); } }
    public bool NotifyOnCrash   { get => S.NotifyOnCrash;   set { S.NotifyOnCrash = value; Persist(); } }
    public bool NotifyOnConnect { get => S.NotifyOnConnect; set { S.NotifyOnConnect = value; Persist(); } }

    public RelayCommand TestWebhookCommand { get; }

    private void TestWebhook()
    {
        if (!WebhookService.Configured) { _main.SetStatus("Enter a Discord webhook URL first."); return; }
        _ = WebhookService.SendEmbedAsync("✅ Webhook test", "Your Roblox Account Manager webhook is working.",
            WebhookService.ColorGreen, null,
            new (string, string)[] { ("Status", "Connected"), ("App", AppInfo.Long) });
        _main.SetStatus("Test embed sent to Discord.");
    }

    // Browser
    public bool ChromiumInstalled => ChromiumService.IsInstalled;
    public string ChromiumStatus => ChromiumService.IsInstalled
        ? "CloakBrowser is installed — accounts open in it, separate from your normal browser."
        : "Not installed yet. \"Open in browser\" will download a portable CloakBrowser (~540 MB) on first use.";
    public string ChromiumButtonText => ChromiumService.IsInstalled ? "Re-download CloakBrowser" : "Download CloakBrowser";
    public RelayCommand DownloadChromiumCommand { get; }

    private void DownloadChromium()
    {
        if (DialogService.ShowChromiumDownload())
            _main.SetStatus("CloakBrowser ready.");
        RefreshChromium();
    }

    /// <summary>Re-reads Chromium install state (called when the Settings page becomes visible).</summary>
    public void RefreshChromium()
    {
        OnPropertyChanged(nameof(ChromiumInstalled));
        OnPropertyChanged(nameof(ChromiumStatus));
        OnPropertyChanged(nameof(ChromiumButtonText));
    }

    // Launch
    public bool EnableMultiInstance
    {
        get => S.EnableMultiInstance;
        set { S.EnableMultiInstance = value; Persist(); LauncherService.EnsureMultiInstance(value); RefreshSingletonState(); }
    }

    /// <summary>
    /// Clears Roblox's per-client singleton lock so launches started outside the manager
    /// (website, home screen, invites) open their own window instead of hijacking a running one.
    /// </summary>
    public bool CloseSingletonEvent
    {
        get => S.CloseSingletonEvent;
        set { S.CloseSingletonEvent = value; Persist(); RobloxSingletonService.Apply(); RefreshSingletonState(); }
    }

    public bool AdoptExternalClients
    {
        get => S.AdoptExternalClients;
        set { S.AdoptExternalClients = value; Persist(); }
    }

    public string MultiInstanceStatus => RobloxSingletonService.StatusText;

    /// <summary>Only offer the UAC route when elevation would actually change the outcome.</summary>
    public bool ShowElevatePrompt => RobloxSingletonService.AccessDenied && !RobloxSingletonService.IsElevated;

    public RelayCommand FixMultiInstanceCommand { get; private set; } = null!;
    public RelayCommand RestartElevatedCommand { get; private set; } = null!;

    private void FixMultiInstance()
    {
        RobloxSingletonService.SweepNow(out string message);
        _main.SetStatus(message);
        RefreshSingletonState();
    }

    private void RestartElevated()
    {
        if (RobloxSingletonService.IsElevated) { _main.SetStatus("Already running as administrator."); return; }

        if (!DialogService.Confirm("Restart as administrator",
            "The manager will close and reopen with administrator rights so it can unlock Roblox "
            + "for additional clients. Continue?")) return;

        if (RobloxSingletonService.RestartElevated())
            System.Windows.Application.Current?.Shutdown();
        else
            _main.SetStatus("Restart cancelled — administrator rights were not granted.");
    }

    /// <summary>
    /// The guard reports from a background timer, so hop to the UI thread before raising
    /// change notifications — WPF bindings must not be poked from a Timer callback.
    /// </summary>
    private void RefreshSingletonState()
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher == null) return;
        app.Dispatcher.BeginInvoke(new System.Action(() =>
        {
            OnPropertyChanged(nameof(MultiInstanceStatus));
            OnPropertyChanged(nameof(ShowElevatePrompt));
        }));
    }
    public bool AutoCloseLastProcess { get => S.AutoCloseLastProcess; set { S.AutoCloseLastProcess = value; Persist(); } }
    public int AccountJoinDelay { get => S.AccountJoinDelay; set { S.AccountJoinDelay = value; Persist(); } }
    public bool ShuffleLowestServer { get => S.ShuffleLowestServer; set { S.ShuffleLowestServer = value; Persist(); } }
    public int ShufflePageCount { get => S.ShufflePageCount; set { S.ShufflePageCount = Math.Clamp(value, 1, 25); Persist(); } }

    // FPS
    public bool UnlockFps { get => S.UnlockFps; set { S.UnlockFps = value; Persist(); } }
    public int MaxFps { get => S.MaxFps; set { S.MaxFps = Math.Clamp(value, 30, 1000); Persist(); } }

    // Live data
    /// <summary>
    /// Turning presence back on has to restart the poll loop. <see cref="PresenceService.Start"/>
    /// bails out when the setting is off, so an app that started with presence disabled had no
    /// timer at all — flipping this switch then did nothing until the next restart.
    /// </summary>
    public bool ShowPresence
    {
        get => S.ShowPresence;
        set { S.ShowPresence = value; Persist(); if (value) PresenceService.Start(); else PresenceService.Stop(); }
    }
    public bool ShowThumbnails { get => S.ShowThumbnails; set { S.ShowThumbnails = value; Persist(); } }
    public bool ShowRobux { get => S.ShowRobux; set { S.ShowRobux = value; Persist(); } }
    public bool TrackEconomy { get => S.TrackEconomy; set { S.TrackEconomy = value; Persist(); } }

    /// <summary>Dashboard poll cadence. Restarts the timer so a new value takes effect at once.</summary>
    public int PresencePollSeconds
    {
        get => S.PresencePollSeconds;
        set { S.PresencePollSeconds = Math.Clamp(value, 5, 600); Persist(); PresenceService.Start(); }
    }

    // Interface
    public bool HideUsernames { get => S.HideUsernames; set { S.HideUsernames = value; Persist(); _main.Accounts.RefreshMask(); _main.Dashboard.RefreshMask(); _main.Friends.RefreshMask(); } }
    public bool MinimizeToTray { get => S.MinimizeToTray; set { S.MinimizeToTray = value; Persist(); } }

    // Security
    public bool HasMasterPassword => _main.Store.MasterPassword is { Length: > 0 };
    public string PasswordStatus => HasMasterPassword
        ? "Your account file is encrypted with a master password."
        : "Your account file is encrypted with Windows DPAPI (tied to your Windows user).";

    public RelayCommand SetPasswordCommand { get; }
    public RelayCommand RemovePasswordCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }

    // Plugins
    public System.Collections.Generic.IReadOnlyList<PluginService.LoadedPlugin> Plugins => PluginService.Plugins;
    public string PluginStatus
    {
        get
        {
            var all = PluginService.Plugins;
            if (all.Count == 0) return "No plugins loaded. Drop a plugin DLL into the plugins folder and reload.";
            int ok = all.Count(p => p.Ok), bad = all.Count - ok;
            return bad == 0 ? $"{ok} plugin(s) loaded." : $"{ok} loaded, {bad} failed.";
        }
    }
    public RelayCommand OpenPluginsFolderCommand { get; }
    public RelayCommand ReloadPluginsCommand { get; }

    public string AppVersion => AppInfo.Long;

    // ---- Diagnostics / self-check / running clients ----

    public ObservableCollection<HealthCheckService.Check> HealthChecks { get; } = new();

    private bool _healthRunning;
    public bool HealthRunning { get => _healthRunning; private set => SetField(ref _healthRunning, value); }

    public string DiagnosticsStatus
    {
        get
        {
            int errors = DiagnosticsService.ErrorCount;
            int clients = InstanceControlService.Count;
            string errorPart = errors == 0 ? "No errors recorded this session." : $"{errors} error(s) recorded this session.";
            return $"{errorPart}  {clients} Roblox client(s) tracked.";
        }
    }

    public RelayCommand RunHealthCheckCommand { get; private set; } = null!;
    public RelayCommand OpenDiagnosticsCommand { get; private set; } = null!;
    public RelayCommand ClearDiagnosticsCommand { get; private set; } = null!;
    public RelayCommand ArrangeWindowsCommand { get; private set; } = null!;
    public RelayCommand MinimizeClientsCommand { get; private set; } = null!;
    public RelayCommand RestoreClientsCommand { get; private set; } = null!;

    /// <summary>Reports a window action, including the "nothing happened" case — a silently
    /// no-op button reads as broken.</summary>
    private void ReportClientAction(int affected, string verb)
    {
        _main.SetStatus(affected > 0
            ? $"{affected} Roblox window(s) {verb}."
            : "No Roblox client windows are open yet.");
        OnPropertyChanged(nameof(DiagnosticsStatus));
    }

    private async void RunHealthCheck()
    {
        if (HealthRunning) return;   // the button stays clickable; don't stack runs
        HealthRunning = true;
        _main.SetStatus("Running self-check…");
        try
        {
            var results = await HealthCheckService.RunAsync();
            HealthChecks.Clear();
            foreach (var c in results) HealthChecks.Add(c);
            int failed = results.Count(c => !c.Ok);
            _main.SetStatus(failed == 0
                ? "Self-check passed — everything looks healthy."
                : $"Self-check found {failed} problem(s) — see the list above.");
        }
        catch (Exception ex)
        {
            // async void: an escaping exception would take the whole app down.
            DiagnosticsService.Error("settings", "Health check failed", ex);
            _main.SetStatus("Self-check could not be completed.");
        }
        finally
        {
            HealthRunning = false;
            OnPropertyChanged(nameof(DiagnosticsStatus));
        }
    }

    private void Persist() { SettingsService.Save(); OnPropertyChanged(""); }

    private void SetPassword()
    {
        string? pw = DialogService.PromptPassword("Set master password",
            "Choose a password (4+ characters). You'll need it every time you open the app.");
        if (pw == null) return;
        if (pw.Length < 4) { _main.SetStatus("Password must be at least 4 characters."); return; }
        _main.Store.SetMasterPassword(pw);
        _main.SetStatus("Master password set.");
        OnPropertyChanged(nameof(HasMasterPassword));
        OnPropertyChanged(nameof(PasswordStatus));
    }

    private void RemovePassword()
    {
        if (!HasMasterPassword) return;
        if (!DialogService.Confirm("Remove master password",
            "The account file will fall back to Windows DPAPI encryption. Continue?")) return;
        _main.Store.SetMasterPassword(null);
        _main.SetStatus("Master password removed.");
        OnPropertyChanged(nameof(HasMasterPassword));
        OnPropertyChanged(nameof(PasswordStatus));
    }

    private void OpenDataFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Paths.DataDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Paths.DataDir) { UseShellExecute = true });
        }
        catch { }
    }

    private void OpenPluginsFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(PluginService.PluginDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PluginService.PluginDir) { UseShellExecute = true });
        }
        catch { }
    }

    private void ReloadPlugins()
    {
        // Re-scans the plugins folder and re-runs OnLoad. Note: Assembly.LoadFrom cannot truly
        // unload an assembly, so newly-added DLLs are picked up, but a changed DLL needs an app restart.
        try { PluginService.Unload(); PluginService.Load(); }
        catch { }
        OnPropertyChanged(nameof(Plugins));
        OnPropertyChanged(nameof(PluginStatus));
        _main.SetStatus(PluginStatus);
    }

    // ---- Appearance / Theme editor (#30 UX-Politur) ----

    /// <summary>The built-in preset names, for the picker.</summary>
    public IEnumerable<string> ThemePresets => ThemeService.Presets.Keys;

    /// <summary>Selected preset. Switching rewrites the custom overrides to the
    /// preset's colours, repaints live, and refreshes the editable swatches.</summary>
    public string ThemeName
    {
        get => S.ThemeName;
        set
        {
            if (value == null || value == S.ThemeName) return;
            S.ThemeName = value;
            // Adopt the preset as the new editable baseline so the swatches match
            // what is on screen and further edits layer cleanly on top.
            S.CustomTheme = ThemeService.Presets.TryGetValue(value, out var p)
                ? new Dictionary<string, string>(p)
                : new Dictionary<string, string>();
            ThemeService.Apply(S);
            SettingsService.Save();
            RefreshThemeRows();
            OnPropertyChanged();
        }
    }

    /// <summary>Editable colour swatches (label + hex), grouped for display.</summary>
    public ObservableCollection<ThemeColorRow> ThemeColors { get; } = new();

    public RelayCommand ResetThemeCommand { get; }

    private void BuildThemeRows()
    {
        var eff = ThemeService.Resolve(S);
        foreach (var (key, label, group) in ThemeService.Editable)
        {
            var hex = eff.TryGetValue(key, out var v) ? v : BaselineHex(key);
            ThemeColors.Add(new ThemeColorRow(key, label, group, hex, OnThemeColorChanged));
        }
    }

    private void RefreshThemeRows()
    {
        var eff = ThemeService.Resolve(S);
        foreach (var row in ThemeColors)
            row.SetSilently(eff.TryGetValue(row.Key, out var v) ? v : BaselineHex(row.Key));
    }

    /// <summary>Reads the current live colour so a fresh row shows the real value
    /// even for keys the user has never overridden.</summary>
    private static string BaselineHex(string key)
    {
        var app = System.Windows.Application.Current;
        if (app?.Resources[key] is System.Windows.Media.Color c)
            return c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        return "#000000";
    }

    private void OnThemeColorChanged(string key, string hex)
    {
        S.CustomTheme ??= new Dictionary<string, string>();
        S.CustomTheme[key] = hex;
        ThemeService.Apply(S);
        SettingsService.Save();
    }

    private void ResetTheme()
    {
        S.ThemeName = "Avallon (mono)";
        S.CustomTheme = new Dictionary<string, string>();
        ThemeService.Apply(S);
        SettingsService.Save();
        RefreshThemeRows();
        OnPropertyChanged(nameof(ThemeName));
    }

    // ---- Views / i18n / Notifications ----
    public IEnumerable<string> ViewModes => new[] { "Card", "Compact" };
    public string AccountViewMode
    {
        get => S.AccountViewMode;
        set { if (value != null && value != S.AccountViewMode) { S.AccountViewMode = value; Persist(); _main.Accounts.RefreshViewMode(); } }
    }

    public IEnumerable<string> Languages => LocalizationService.Languages.Select(l => l.Label);
    public string Language
    {
        get => LocalizationService.LabelFor(S.Language);
        set
        {
            var code = LocalizationService.CodeFor(value);
            if (code == S.Language) return;
            S.Language = code;
            LocalizationService.Apply(code);
            Persist();
        }
    }

    public bool EnableToasts { get => S.EnableToasts; set { S.EnableToasts = value; Persist(); } }
    public bool ToastOnLaunch { get => S.ToastOnLaunch; set { S.ToastOnLaunch = value; Persist(); } }
    public bool ToastOnCrash { get => S.ToastOnCrash; set { S.ToastOnCrash = value; Persist(); } }

    // =================================================================
    //  Automation — Anti-AFK, crash watchdog, RAM monitor
    //
    //  These engines were all implemented and wired at startup, but nothing could ever switch
    //  them on: every flag defaulted to false and no page bound to them. The controls below are
    //  what makes them reachable; each setter re-applies its service so changes take effect now
    //  rather than on the next start.
    // =================================================================

    // ---- Anti-AFK ----
    public bool AntiAfkEnabled
    {
        get => S.AntiAfkEnabled;
        set { S.AntiAfkEnabled = value; Persist(); AntiAfkService.Apply(); }
    }
    public int AntiAfkIntervalMinutes
    {
        get => S.AntiAfkIntervalMinutes;
        set { S.AntiAfkIntervalMinutes = Math.Clamp(value, 1, 120); Persist(); AntiAfkService.Apply(); }
    }
    public IEnumerable<string> AntiAfkKeys => new[] { "Space", "Shift", "Ctrl", "W", "A", "S", "D", "0", "F13" };
    public string AntiAfkKey
    {
        get => S.AntiAfkKey;
        set { if (!string.IsNullOrEmpty(value)) { S.AntiAfkKey = value; Persist(); } }
    }
    public bool AntiAfkRestoreFocus { get => S.AntiAfkRestoreFocus; set { S.AntiAfkRestoreFocus = value; Persist(); } }

    public RelayCommand TestAntiAfkCommand { get; private set; } = null!;

    private void TestAntiAfk()
    {
        int clients = InstanceControlService.Count;
        if (clients == 0) { _main.SetStatus("No Roblox clients are running — nothing to keep awake."); return; }
        AntiAfkService.RunOnce();
        _main.SetStatus($"Anti-AFK pass sent to {clients} client(s).");
    }

    // ---- Crash watchdog ----
    public bool WatchdogEnabled
    {
        get => S.WatchdogEnabled;
        set { S.WatchdogEnabled = value; Persist(); WatchdogService.Apply(); }
    }
    public int WatchdogCheckSeconds
    {
        get => S.WatchdogCheckSeconds;
        set { S.WatchdogCheckSeconds = Math.Clamp(value, 5, 600); Persist(); WatchdogService.Apply(); }
    }

    // ---- RAM monitor ----
    public bool RamMonitorEnabled
    {
        get => S.RamMonitorEnabled;
        set { S.RamMonitorEnabled = value; Persist(); RamMonitorService.Apply(); RefreshRamStatus(); }
    }
    public int RamMonitorSeconds
    {
        get => S.RamMonitorSeconds;
        set { S.RamMonitorSeconds = Math.Clamp(value, 2, 600); Persist(); RamMonitorService.Apply(); }
    }
    public bool AutoCloseOnHighRam { get => S.AutoCloseOnHighRam; set { S.AutoCloseOnHighRam = value; Persist(); } }
    public int RamLimitMb
    {
        get => S.RamLimitMb;
        set { S.RamLimitMb = Math.Clamp(value, 256, 65536); Persist(); }
    }

    /// <summary>Live per-client RAM readout, refreshed from the monitor's sampling event.</summary>
    public string RamStatus
    {
        get
        {
            if (!S.RamMonitorEnabled) return "Monitor is off — turn it on to see per-client memory use.";
            var latest = RamMonitorService.Latest;
            if (latest.Count == 0) return "No tracked clients yet.";
            long total = latest.Sum(x => x.WorkingSetMb);
            var lines = latest.OrderByDescending(x => x.WorkingSetMb)
                              .Select(x => $"{x.Alias} — {x.WorkingSetMb:N0} MB");
            return $"{latest.Count} client(s) · {total:N0} MB total\n" + string.Join("\n", lines);
        }
    }

    private void RefreshRamStatus()
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher == null) return;
        app.Dispatcher.BeginInvoke(new System.Action(() => OnPropertyChanged(nameof(RamStatus))));
    }

    // =================================================================
    //  FastFlags
    // =================================================================

    public bool ApplyFFlags { get => S.ApplyFFlags; set { S.ApplyFFlags = value; Persist(); } }
    public bool FFlagUnlockFps { get => S.FFlagUnlockFps; set { S.FFlagUnlockFps = value; Persist(); } }
    public bool FFlagDisableTelemetry { get => S.FFlagDisableTelemetry; set { S.FFlagDisableTelemetry = value; Persist(); } }
    public bool FFlagLightingTechVoxel { get => S.FFlagLightingTechVoxel; set { S.FFlagLightingTechVoxel = value; Persist(); } }
    public bool FFlagDisableVoiceChat { get => S.FFlagDisableVoiceChat; set { S.FFlagDisableVoiceChat = value; Persist(); } }

    private string _customFFlagsText = "";
    /// <summary>Raw flag JSON the user edits. Parsed on save; invalid text is kept but not applied.</summary>
    public string CustomFFlagsText
    {
        get => _customFFlagsText;
        set
        {
            if (!SetField(ref _customFFlagsText, value ?? "")) return;
            // Commit before announcing: the summary counts the flags that are actually stored.
            if (FFlagsService.IsValidRaw(_customFFlagsText))
            {
                S.CustomFFlags = FFlagsService.ParseRaw(_customFFlagsText);
                SettingsService.Save();
            }
            OnPropertyChanged(nameof(CustomFFlagsValid));
            OnPropertyChanged(nameof(FFlagsSummary));
        }
    }

    public bool CustomFFlagsValid => FFlagsService.IsValidRaw(_customFFlagsText);

    public string FFlagsSummary
    {
        get
        {
            if (!CustomFFlagsValid) return "That is not valid JSON — the custom flags are not being applied.";
            int n = FFlagsService.BuildFlags(S).Count;
            return S.ApplyFFlags
                ? $"{n} flag(s) will be written to every installed Roblox version before each launch."
                : $"{n} flag(s) configured — switch FastFlags on to apply them.";
        }
    }

    public RelayCommand ApplyFFlagsNowCommand { get; private set; } = null!;
    public RelayCommand ClearFFlagsCommand { get; private set; } = null!;

    private void ApplyFFlagsNow()
    {
        int n = FFlagsService.ApplyForLaunch(S);
        _main.SetStatus(n > 0
            ? $"Flags written to {n} Roblox version folder(s)."
            : "Nothing to write — no flags are configured, or Roblox isn't installed.");
        OnPropertyChanged(nameof(FFlagsSummary));
    }

    private void ClearFFlags()
    {
        if (!DialogService.Confirm("Clear FastFlags",
            "Delete ClientAppSettings.json from every installed Roblox version, returning the client to stock settings?"))
            return;
        int n = FFlagsService.Clear();
        _main.SetStatus(n > 0 ? $"Cleared flags from {n} version folder(s)." : "No flag files were present.");
    }

    // =================================================================
    //  Local Web API
    // =================================================================

    public bool WebApiEnabled
    {
        get => S.WebApiEnabled;
        set
        {
            // A surface with no token is closed anyway; mint one instead of silently no-op'ing.
            if (value && string.IsNullOrWhiteSpace(S.WebApiToken)) S.WebApiToken = NewToken();
            S.WebApiEnabled = value;
            Persist();
            WebApiService.Apply();
            RefreshWebApi();
        }
    }
    public int WebApiPort
    {
        get => S.WebApiPort;
        set { S.WebApiPort = Math.Clamp(value, 1024, 65535); Persist(); WebApiService.Apply(); RefreshWebApi(); }
    }
    public string WebApiToken
    {
        get => S.WebApiToken;
        set { S.WebApiToken = (value ?? "").Trim(); Persist(); WebApiService.Apply(); RefreshWebApi(); }
    }

    public string WebApiStatus => S.WebApiEnabled && !string.IsNullOrWhiteSpace(S.WebApiToken)
        ? $"Listening on http://127.0.0.1:{S.WebApiPort}/ — localhost only. Send \"Authorization: Bearer <token>\"."
        : "Off. When enabled, scripts on this PC can list accounts, launch, close and read status.";

    public RelayCommand GenerateWebApiTokenCommand { get; private set; } = null!;
    public RelayCommand CopyWebApiTokenCommand { get; private set; } = null!;

    private static string NewToken()
        => System.Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private void RefreshWebApi()
    {
        OnPropertyChanged(nameof(WebApiToken));
        OnPropertyChanged(nameof(WebApiStatus));
    }

    private void CopyWebApiToken()
    {
        if (string.IsNullOrWhiteSpace(S.WebApiToken)) { _main.SetStatus("No token to copy — generate one first."); return; }
        try { System.Windows.Clipboard.SetText(S.WebApiToken); _main.SetStatus("API token copied to clipboard."); }
        catch { _main.SetStatus("Could not access the clipboard."); }
    }

    // =================================================================
    //  Proxy
    // =================================================================

    public bool EnableProxy { get => S.EnableProxy; set { S.EnableProxy = value; Persist(); } }
    public string ProxyAddress { get => S.ProxyAddress; set { S.ProxyAddress = (value ?? "").Trim(); Persist(); } }
    public string ProxyUsername { get => S.ProxyUsername; set { S.ProxyUsername = value ?? ""; Persist(); } }
    public string ProxyPassword { get => S.ProxyPassword; set { S.ProxyPassword = value ?? ""; Persist(); } }

    public RelayCommand TestProxyCommand { get; private set; } = null!;

    private async void TestProxy()
    {
        if (string.IsNullOrWhiteSpace(S.ProxyAddress)) { _main.SetStatus("Enter a proxy address first."); return; }
        _main.SetStatus("Testing proxy…");
        try
        {
            var (ok, message) = await RobloxApi.TestProxyAsync(S.ProxyAddress, S.ProxyUsername, S.ProxyPassword);
            _main.SetStatus(message);
            if (!ok) DialogService.Info("Proxy test failed", message);
        }
        catch (Exception ex)
        {
            // async void: an escaping exception would take the app down.
            DiagnosticsService.Error("settings", "Proxy test failed", ex);
            _main.SetStatus("Proxy test could not be completed.");
        }
    }

    // =================================================================
    //  Global hotkeys
    // =================================================================

    public ObservableCollection<HotkeyRow> HotkeyRows { get; } = new();

    private void BuildHotkeyRows()
    {
        // Settings only persist bindings the user has touched; show every action this build
        // knows how to dispatch so a fresh install still lists them all.
        foreach (var (action, label) in HotkeyBinding.ActionLabels)
        {
            var binding = S.Hotkeys.FirstOrDefault(h => h.Action == action);
            if (binding == null)
            {
                binding = new HotkeyBinding { Action = action };
                S.Hotkeys.Add(binding);
            }
            HotkeyRows.Add(new HotkeyRow(binding, label, OnHotkeyChanged));
        }
    }

    private void OnHotkeyChanged()
    {
        SettingsService.Save();
        HotkeyService.Apply();
    }

    /// <summary>Lets the settings page surface a one-line hint through the shared status bar.</summary>
    public void SetStatusHint(string message) => _main.SetStatus(message);

    // =================================================================
    //  Encrypted backup / restore
    // =================================================================

    public RelayCommand ExportBackupCommand { get; private set; } = null!;
    public RelayCommand ImportBackupCommand { get; private set; } = null!;

    private void ExportBackup()
    {
        var accounts = _main.Store.Accounts.ToList();
        if (accounts.Count == 0) { _main.SetStatus("No accounts to back up."); return; }

        // Deliberately stricter than the master password on accounts.dat. That file keeps each
        // cookie individually DPAPI-wrapped, so cracking its password still leaves the cookies
        // bound to the original Windows user. A backup stores raw cookies by design — that is
        // what makes it portable — so this password is the only thing standing between the file
        // and full account takeover, and the file is meant to travel (USB, cloud sync, email).
        const int MinBackupPasswordLength = 12;
        string? password = DialogService.PromptPassword("Encrypt backup",
            $"Choose a password for this backup file ({MinBackupPasswordLength}+ characters).\n\n"
            + "This file contains your account cookies in a portable form, so anyone who gets both "
            + "the file and the password gets the accounts. Use a long, unique passphrase — and "
            + "keep in mind there is no way to recover the file if you forget it.");
        if (password == null) return;
        if (password.Length < MinBackupPasswordLength)
        {
            _main.SetStatus($"Backup password must be at least {MinBackupPasswordLength} characters.");
            return;
        }

        string? path = DialogService.SaveFile("Save encrypted backup",
            "Account backup (*.rambk)|*.rambk|All files|*.*",
            $"accounts-{DateTime.Now:yyyyMMdd-HHmmss}.rambk");
        if (path == null) return;

        try
        {
            BackupService.Export(accounts, path, password);
            _main.SetStatus($"Backed up {accounts.Count} account(s) → {System.IO.Path.GetFileName(path)}.");
            AuditLogService.Log(AuditLogService.Category.Account, $"Encrypted backup written ({accounts.Count} accounts).");
        }
        catch (Exception ex)
        {
            DialogService.Info("Backup failed", ex.Message);
        }
    }

    private void ImportBackup()
    {
        string? path = DialogService.PickFile("Restore encrypted backup",
            "Account backup (*.rambk)|*.rambk|All files|*.*");
        if (path == null) return;

        string? password = DialogService.PromptPassword("Decrypt backup", "Password for this backup file.");
        if (password == null) return;

        List<Account> restored;
        try { restored = BackupService.Import(path, password); }
        catch (Exception ex)
        {
            DialogService.Info("Restore failed",
                "The backup could not be read. That usually means the password is wrong or the file "
                + $"is not a Roblox Account Manager backup.\n\nDetails: {ex.Message}");
            return;
        }

        // Merge, never replace: an account already in the list keeps its place and only has its
        // cookie refreshed, so restoring a backup can't silently drop newer accounts.
        int added = 0, refreshed = 0;
        foreach (var r in restored)
        {
            var existing = _main.Store.Accounts.FirstOrDefault(a =>
                (a.UserId != 0 && a.UserId == r.UserId) ||
                (!string.IsNullOrEmpty(r.Username) && string.Equals(a.Username, r.Username, StringComparison.OrdinalIgnoreCase)));

            if (existing != null)
            {
                if (!string.IsNullOrEmpty(r.Cookie) && existing.Cookie != r.Cookie)
                {
                    existing.Cookie = r.Cookie;
                    existing.IsValid = true;
                    refreshed++;
                }
                if (string.IsNullOrEmpty(existing.TotpSecret)) existing.TotpSecret = r.TotpSecret;
            }
            else
            {
                _main.Store.Accounts.Add(r);
                added++;
            }
        }

        _main.Store.Save();
        _main.SetStatus($"Restore complete — {added} added, {refreshed} cookie(s) refreshed.");
        AuditLogService.Log(AuditLogService.Category.Account, $"Backup restored ({added} added, {refreshed} refreshed).");
        _ = FinishRestoreAsync(added, refreshed);
    }

    /// <summary>
    /// Validates the restored cookies, then loads live data. The validation pass is what
    /// backfills the user id and username on accounts from a pre-1.5 backup, which did not
    /// record them — without it those rows stay inert (no presence, no launch attribution).
    /// </summary>
    private async Task FinishRestoreAsync(int added, int refreshed)
    {
        try
        {
            await CookieHealthService.ValidateAllAsync(_main.Store.Accounts.ToList());
            _main.Store.Save();
            await _main.Store.RefreshLiveDataAsync();
            _main.SetStatus($"Restore complete — {added} added, {refreshed} cookie(s) refreshed.");
        }
        catch (Exception ex)
        {
            DiagnosticsService.Error("settings", "Post-restore refresh failed", ex);
        }
    }

    // =================================================================
    //  Security extras
    // =================================================================

    public bool ValidateCookiesOnStartup { get => S.ValidateCookiesOnStartup; set { S.ValidateCookiesOnStartup = value; Persist(); } }
    public bool RotationDetectionEnabled { get => S.RotationDetectionEnabled; set { S.RotationDetectionEnabled = value; Persist(); } }
    public bool AuditLogEnabled { get => S.AuditLogEnabled; set { S.AuditLogEnabled = value; Persist(); } }

    public RelayCommand ValidateCookiesNowCommand { get; private set; } = null!;

    private async void ValidateCookiesNow()
    {
        var accounts = _main.Store.Accounts.ToList();
        if (accounts.Count == 0) { _main.SetStatus("No accounts to check."); return; }
        _main.SetStatus($"Checking {accounts.Count} cookie(s)…");
        try
        {
            int invalid = await CookieHealthService.ValidateAllAsync(
                accounts, new Progress<string>(p => _main.SetStatus(p)));
            _main.SetStatus(invalid == 0
                ? $"All {accounts.Count} cookie(s) are still valid."
                : $"{invalid} of {accounts.Count} cookie(s) are no longer accepted by Roblox.");
        }
        catch (Exception ex)
        {
            DiagnosticsService.Error("settings", "Cookie validation failed", ex);
            _main.SetStatus("Cookie check could not be completed.");
        }
    }
}

/// <summary>
/// One editable global-hotkey row. The chord itself is captured by the settings page
/// (a key press is a view concern); this wrapper persists it and re-registers the hotkeys.
/// </summary>
public class HotkeyRow : ObservableObject
{
    private readonly HotkeyBinding _binding;
    private readonly System.Action _onChanged;

    public string Action => _binding.Action;
    public string Label { get; }

    public HotkeyRow(HotkeyBinding binding, string label, System.Action onChanged)
    {
        _binding = binding; Label = label; _onChanged = onChanged;
    }

    public bool Enabled
    {
        get => _binding.Enabled;
        set { if (_binding.Enabled == value) return; _binding.Enabled = value; OnPropertyChanged(); _onChanged(); }
    }

    public string ChordText => _binding.ChordText;

    private bool _capturing;
    /// <summary>True while the row is waiting for the user to press a chord.</summary>
    public bool Capturing
    {
        get => _capturing;
        set { if (SetField(ref _capturing, value)) OnPropertyChanged(nameof(DisplayText)); }
    }

    public string DisplayText => _capturing ? "Press a chord…" : ChordText;

    /// <summary>Stores a newly captured chord and re-registers every hotkey.</summary>
    public void SetChord(uint modifiers, uint key)
    {
        _binding.Modifiers = modifiers;
        _binding.Key = key;
        Capturing = false;
        OnPropertyChanged(nameof(ChordText));
        OnPropertyChanged(nameof(DisplayText));
        _onChanged();
    }

    public void CancelCapture() => Capturing = false;
}
