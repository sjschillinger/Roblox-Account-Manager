using System.Collections.ObjectModel;
using System.Windows;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.ViewModels;

public class NavItem : ObservableObject
{
    /// <summary>Localization key (e.g. "Nav.Accounts"); the shown <see cref="Title"/>
    /// is resolved live so a language switch relabels the rail without a rebuild.</summary>
    public string TitleKey { get; init; } = "";
    public string Title => LocalizationService.Get(TitleKey, LocalizationService.Current);
    public string IconKey { get; init; } = "";
    public int Index { get; init; }

    private bool _isActive;
    public bool IsActive { get => _isActive; set => SetField(ref _isActive, value); }

    /// <summary>Re-reads <see cref="Title"/> after the active language changed.</summary>
    public void RefreshTitle() => OnPropertyChanged(nameof(Title));
}

public class MainViewModel : ObservableObject
{
    public AccountStore Store { get; }
    public AccountsViewModel Accounts { get; }
    public ServerBrowserViewModel Servers { get; }
    public SettingsViewModel Settings { get; }
    public DashboardViewModel Dashboard { get; }
    public FriendsViewModel Friends { get; }

    /// <summary>In-app toast stack, surfaced by the main-window overlay (#30).</summary>
    public ObservableCollection<ToastItem> Toasts => ToastService.Items;

    public ObservableCollection<NavItem> NavItems { get; } = new()
    {
        new NavItem { TitleKey = "Nav.Dashboard", IconKey = "Icon.Activity", Index = 0 },
        new NavItem { TitleKey = "Nav.Accounts",  IconKey = "Icon.Accounts", Index = 1 },
        new NavItem { TitleKey = "Nav.Friends",   IconKey = "Icon.Friends",  Index = 2 },
        new NavItem { TitleKey = "Nav.Servers",   IconKey = "Icon.Servers",  Index = 3 },
        new NavItem { TitleKey = "Nav.Settings",  IconKey = "Icon.Settings", Index = 4 },
    };

    private int _selectedIndex;
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (SetField(ref _selectedIndex, value))
            {
                OnPropertyChanged(nameof(SelectedNav));
                foreach (var n in NavItems) n.IsActive = n.Index == value;
                if (value == 4) Settings.RefreshChromium();
                if (value == 2) Friends.EnsureLoaded();
                // Servers tab (index 3): auto-fill the Place ID from the game currently
                // targeted on the Accounts tab so the user doesn't retype it.
                if (value == 3 && string.IsNullOrWhiteSpace(Servers.PlaceIdText)
                                && !string.IsNullOrWhiteSpace(Accounts.PlaceIdText))
                    Servers.PlaceIdText = Accounts.PlaceIdText;
            }
        }
    }

    public NavItem SelectedNav => NavItems[_selectedIndex];

    private string _status = "Ready";
    public string Status { get => _status; set => SetField(ref _status, value); }

    /// <summary>Version badge in the title bar, e.g. "v1.1.0" — read from the built assembly.</summary>
    public string AppVersionShort => AppInfo.Short;

    public RelayCommand NavCommand { get; }

    // ---- self-update ----
    private static bool s_updateCheckStarted; // at most one check per process run

    private UpdateInfo? _update;
    private string? _promptedVersion;                 // last version the modal was shown for
    private System.Windows.Threading.DispatcherTimer? _updateTimer;
    public bool UpdateAvailable => _update != null;

    private string _updateVersionText = "";
    public string UpdateVersionText
    {
        get => _updateVersionText;
        private set => SetField(ref _updateVersionText, value);
    }

    public RelayCommand UpdateNowCommand { get; }

    public MainViewModel()
    {
        NavCommand = new RelayCommand(p =>
        {
            if (p is int i) SelectedIndex = i;
            else if (p != null && int.TryParse(p.ToString(), out var pi)) SelectedIndex = pi;
        });

        Store = new AccountStore();
        Accounts = new AccountsViewModel(Store, this);
        Servers = new ServerBrowserViewModel(Store, this);
        Settings = new SettingsViewModel(this);
        Dashboard = new DashboardViewModel(Store);
        Friends = new FriendsViewModel(Store, this);

        NavItems[0].IsActive = true;

        // A language switch relabels the rail live (Title resolves against the active code).
        LocalizationService.Changed += () =>
        {
            foreach (var n in NavItems) n.RefreshTitle();
            OnPropertyChanged(nameof(SelectedNav));
        };

        UpdateNowCommand = new RelayCommand(UpdateNow, () => UpdateAvailable);

        // Fire-and-forget update check on startup, at most once per run; failures are silent.
        if (!s_updateCheckStarted)
        {
            s_updateCheckStarted = true;
            _ = CheckForUpdateAsync();
        }

        // Then keep polling every 5 minutes so a freshly published release is picked up
        // without hammering GitHub with a request every single minute. The modal only
        // appears once per version (see CheckForUpdateAsync); the title-bar pill just stays
        // put, so the recurring check never nags the user.
        _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _updateTimer.Tick += (_, _) => _ = CheckForUpdateAsync();
        _updateTimer.Start();

        // "What's new" once after every update — the first run of a freshly installed version.
        _ = ShowWhatsNewIfUpdatedAsync();
    }

    public void SetStatus(string s) => Status = s;

    // ---- update-check status, surfaced on the Settings page ----

    private string _updateCheckStatus = "Not checked yet this session.";
    /// <summary>Plain-language result of the last update check, for the Settings → About card.</summary>
    public string UpdateCheckStatus { get => _updateCheckStatus; private set => SetField(ref _updateCheckStatus, value); }

    private bool _updateCheckRunning;
    public bool UpdateCheckRunning { get => _updateCheckRunning; private set => SetField(ref _updateCheckRunning, value); }

    /// <summary>
    /// Manual "check for updates". Unlike the background poll this always reports an outcome —
    /// including "you are up to date" and "the check failed", so the button never looks like it
    /// did nothing.
    /// </summary>
    public async Task CheckForUpdateNowAsync()
    {
        if (UpdateCheckRunning) return;
        UpdateCheckRunning = true;
        UpdateCheckStatus = "Checking GitHub…";
        SetStatus("Checking for updates…");
        try
        {
            // No ConfigureAwait(false) anywhere in here on purpose: the command is invoked on the
            // UI thread, so every continuation below resumes there and can touch bindings and
            // show a modal directly.
            var info = await UpdateService.CheckForUpdateAsync();

            string stamp = DateTime.Now.ToString("HH:mm");
            if (info == null)
            {
                UpdateCheckStatus = $"You're on the latest version ({AppInfo.Short}). Last checked {stamp}.";
                SetStatus($"No update available — {AppInfo.Short} is current.");
                return;
            }

            Adopt(info);
            UpdateCheckStatus = $"{info.VersionText} is available ({info.SizeText}). Last checked {stamp}.";
            SetStatus($"Update available — {info.VersionText}.");
            _promptedVersion = info.VersionText;
            PromptForUpdate(info);
        }
        catch (Exception ex)
        {
            UpdateCheckStatus = $"Check failed: {ex.Message}";
            SetStatus("Update check failed — check your connection and try again.");
        }
        finally { UpdateCheckRunning = false; }
    }

    /// <summary>Records a discovered update and lights up the title-bar pill.</summary>
    private void Adopt(UpdateInfo info)
    {
        _update = info;
        UpdateVersionText = $"Update available — {info.VersionText}";
        OnPropertyChanged(nameof(UpdateAvailable));
        UpdateNowCommand.RaiseCanExecuteChanged();
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var info = await UpdateService.CheckForUpdateAsync().ConfigureAwait(false);

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            if (info == null)
            {
                // Record the negative result too, so the Settings card can say when we last
                // looked instead of sitting on "not checked yet" forever.
                await dispatcher.InvokeAsync(() =>
                    UpdateCheckStatus = $"You're on the latest version ({AppInfo.Short}). Last checked {DateTime.Now:HH:mm}.");
                return;
            }

            await dispatcher.InvokeAsync(() =>
            {
                Adopt(info);
                UpdateCheckStatus = $"{info.VersionText} is available ({info.SizeText}). Last checked {DateTime.Now:HH:mm}.";

                // Surface the modal once per discovered version. On every following poll the
                // pill stays visible but we don't re-prompt, so the recurring check never nags.
                if (_promptedVersion != info.VersionText)
                {
                    _promptedVersion = info.VersionText;
                    PromptForUpdate(info);
                }
            });
        }
        catch
        {
            // Best-effort background poll: a network/parse failure must not bubble up as an
            // unobserved task exception every 5 minutes. The pill simply stays as-is.
        }
    }

    /// <summary>Re-opens this build's changelog on demand (the post-update window is once-only).</summary>
    public async Task ShowWhatsNewAsync()
    {
        try
        {
            string current = UpdateService.CurrentVersionText;
            // Invoked from the UI thread; keep the continuation there so the window can be shown.
            var notes = await UpdateService.GetNotesForCurrentVersionAsync();

            var win = new Views.WhatsNewWindow(current, notes?.Notes, notes?.PageUrl);
            if (Application.Current?.MainWindow is { IsVisible: true } owner) win.Owner = owner;
            win.ShowDialog();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open the changelog: {ex.Message}");
        }
    }

    // Triggered by the title-bar update pill.
    private void UpdateNow()
    {
        if (_update is { } info) PromptForUpdate(info);
    }

    private void PromptForUpdate(UpdateInfo info)
    {
        var win = new Views.UpdatePromptWindow(info);
        if (Application.Current?.MainWindow is { IsVisible: true } owner) win.Owner = owner;
        if (win.ShowDialog() != true)
            return; // "Later" — keep the pill, do nothing

        if (UpdateService.BeginUpdate(info))
        {
            // The prompt already showed this version's notes — flag it so the post-update
            // "What's new" window doesn't repeat the same changelog after the restart.
            SettingsService.Current.UpdateNotesSeenFor = info.VersionText;
            SettingsService.Save();
            Application.Current?.Shutdown();
        }
        else
            SetStatus("Update could not be started — please try again later.");
    }

    /// <summary>
    /// Shows the changelog window exactly once after an update: when the running version
    /// differs from the last one recorded in settings. Fresh installs only record the
    /// version silently — there is nothing "new" to announce.
    /// </summary>
    private async Task ShowWhatsNewIfUpdatedAsync()
    {
        try
        {
            var s = SettingsService.Current;
            string current = UpdateService.CurrentVersionText;

            // One-shot flag set by the update prompt: its dialog already displayed this
            // version's release notes right before the download, so showing the same
            // changelog again here would be a duplicate. A stale flag (aborted/failed
            // update, current version differs) is simply cleared.
            bool seenInPrompt = s.UpdateNotesSeenFor == current;
            bool flagWasSet = s.UpdateNotesSeenFor.Length > 0;
            if (flagWasSet) s.UpdateNotesSeenFor = "";

            if (s.LastSeenVersion == current)
            {
                if (flagWasSet) SettingsService.Save();
                return;
            }

            bool firstInstall = string.IsNullOrEmpty(s.LastSeenVersion);
            s.LastSeenVersion = current;
            SettingsService.Save();
            if (firstInstall || seenInPrompt) return;

            var notes = await UpdateService.GetNotesForCurrentVersionAsync().ConfigureAwait(false);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            await dispatcher.InvokeAsync(() =>
            {
                var win = new Views.WhatsNewWindow(current, notes?.Notes, notes?.PageUrl);
                if (Application.Current?.MainWindow is { IsVisible: true } owner) win.Owner = owner;
                win.ShowDialog();
            });
        }
        catch
        {
            // The changelog must never break startup; worst case the window is skipped.
        }
    }

    public void OpenServersFor(long placeId)
    {
        Servers.PlaceIdText = placeId.ToString();
        SelectedIndex = 3;
        _ = Servers.RefreshAsync();
    }
}
