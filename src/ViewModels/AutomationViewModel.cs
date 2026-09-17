using System.Collections.ObjectModel;
using System.Globalization;
using RobloxAccountManager.Models;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.ViewModels;

/// <summary>An account's membership in a preset (a tick box in the editor).</summary>
public class PresetMember : ObservableObject
{
    private readonly Action _changed;
    public Account Account { get; }

    public PresetMember(Account account, bool included, Action changed)
    {
        Account = account;
        _isIncluded = included;
        _changed = changed;
    }

    private bool _isIncluded;
    public bool IsIncluded { get => _isIncluded; set { if (SetField(ref _isIncluded, value)) _changed(); } }
}

/// <summary>Editable wrapper around a <see cref="LaunchPreset"/>; every change is saved straight away.</summary>
public class PresetItem : ObservableObject
{
    private readonly AutomationViewModel _owner;
    public LaunchPreset Model { get; }

    public PresetItem(LaunchPreset model, AutomationViewModel owner)
    {
        Model = model;
        _owner = owner;
    }

    public string Name
    {
        get => Model.Name;
        set
        {
            string v = string.IsNullOrWhiteSpace(value) ? L.T("Automation.Preset.Untitled") : value.Trim();
            if (v == Model.Name) return;
            string old = Model.Name;
            Model.Name = v;
            _owner.OnPresetRenamed(old, v);
            OnPropertyChanged();
            _owner.Persist();
        }
    }

    public string PlaceIdText
    {
        get => Model.PlaceId > 0 ? Model.PlaceId.ToString() : "";
        set
        {
            var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
            Model.PlaceId = long.TryParse(digits, out long id) ? id : 0;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Summary));
            _owner.Persist();
        }
    }

    public string JobId
    {
        get => Model.JobId;
        set { Model.JobId = (value ?? "").Trim(); OnPropertyChanged(); _owner.Persist(); }
    }

    public int JoinDelaySeconds
    {
        get => Model.JoinDelaySeconds;
        set { Model.JoinDelaySeconds = Math.Clamp(value, 0, 600); OnPropertyChanged(); _owner.Persist(); }
    }

    public ObservableCollection<PresetMember> Members { get; } = new();

    /// <summary>Rebuilds the tick list from the current accounts, keeping this preset's picks.</summary>
    public void SyncMembers(IEnumerable<Account> accounts)
    {
        Members.Clear();
        foreach (var a in accounts.OrderBy(a => a.DisplayNameOrUser, StringComparer.CurrentCultureIgnoreCase))
        {
            bool included = Model.Aliases.Any(k => Matches(a, k));
            Members.Add(new PresetMember(a, included, OnMembersChanged));
        }
        OnPropertyChanged(nameof(Summary));
    }

    private void OnMembersChanged()
    {
        // Usernames are what get stored: an alias can be renamed at any time, a username cannot.
        Model.Aliases = Members.Where(m => m.IsIncluded).Select(m => m.Account.Username).Where(u => u.Length > 0).ToList();
        OnPropertyChanged(nameof(Summary));
        _owner.Persist();
    }

    public static bool Matches(Account a, string key)
        => string.Equals(a.Username, key, StringComparison.OrdinalIgnoreCase)
        || (a.Alias.Length > 0 && string.Equals(a.Alias, key, StringComparison.OrdinalIgnoreCase));

    public string Summary => Model.PlaceId > 0
        ? L.N("Automation.Preset.Summary", Model.Aliases.Count, Model.PlaceId)
        : L.N("Automation.Preset.SummaryNoPlace", Model.Aliases.Count);

    public void RaiseAll() => OnPropertyChanged(string.Empty);
}

/// <summary>One weekday toggle in the schedule editor.</summary>
public class DayToggle : ObservableObject
{
    private readonly Action _changed;
    public DayOfWeek Day { get; }
    public string Label { get; }

    public DayToggle(DayOfWeek day, string label, bool on, Action changed)
    {
        Day = day;
        Label = label;
        _isOn = on;
        _changed = changed;
    }

    private bool _isOn;
    public bool IsOn { get => _isOn; set { if (SetField(ref _isOn, value)) _changed(); } }
}

/// <summary>Editable wrapper around a <see cref="ScheduledTask"/>.</summary>
public class ScheduleItem : ObservableObject
{
    private readonly AutomationViewModel _owner;
    public ScheduledTask Model { get; }

    public ScheduleItem(ScheduledTask model, AutomationViewModel owner)
    {
        Model = model;
        _owner = owner;
        BuildDays();
    }

    public string Name
    {
        get => Model.Name;
        set { Model.Name = string.IsNullOrWhiteSpace(value) ? L.T("Automation.Schedule.Untitled") : value.Trim(); OnPropertyChanged(); _owner.Persist(); }
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set { Model.Enabled = value; RaiseSchedule(); _owner.Persist(); }
    }

    /// <summary>"Launch" or "Close".</summary>
    public string Action
    {
        get => Model.Action.ToString();
        set
        {
            Model.Action = value == "Close" ? ScheduleAction.Close : ScheduleAction.Launch;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLaunch));
            OnPropertyChanged(nameof(Summary));
            _owner.Persist();
        }
    }

    public bool IsLaunch => Model.Action == ScheduleAction.Launch;

    /// <summary>"Preset" or "Account".</summary>
    public string TargetKind
    {
        get => string.IsNullOrEmpty(Model.PresetName) && !_preferPreset ? "Account" : "Preset";
        set
        {
            _preferPreset = value == "Preset";
            if (_preferPreset) Model.Alias = "";
            else Model.PresetName = "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAccountTarget));
            OnPropertyChanged(nameof(PresetName));
            OnPropertyChanged(nameof(Account));
            OnPropertyChanged(nameof(Summary));
            _owner.Persist();
        }
    }

    private bool _preferPreset;
    public bool IsAccountTarget => TargetKind == "Account";

    public string? PresetName
    {
        get => string.IsNullOrEmpty(Model.PresetName) ? null : Model.PresetName;
        set { Model.PresetName = value ?? ""; if (Model.PresetName.Length > 0) Model.Alias = ""; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); _owner.Persist(); }
    }

    public Account? Account
    {
        get => _owner.Accounts.FirstOrDefault(a => Model.Alias.Length > 0 && PresetItem.Matches(a, Model.Alias));
        set { Model.Alias = value?.Username ?? ""; if (Model.Alias.Length > 0) Model.PresetName = ""; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); _owner.Persist(); }
    }

    public string PlaceIdText
    {
        get => Model.PlaceId > 0 ? Model.PlaceId.ToString() : "";
        set
        {
            var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
            Model.PlaceId = long.TryParse(digits, out long id) ? id : 0;
            OnPropertyChanged();
            _owner.Persist();
        }
    }

    public string Time
    {
        get => Model.TimeOfDay;
        set
        {
            string normalized = SchedulerService.NormalizeTime(value);
            TimeValid = normalized.Length > 0;
            if (TimeValid) Model.TimeOfDay = normalized;
            OnPropertyChanged();
            RaiseSchedule();
            _owner.Persist();
        }
    }

    private bool _timeValid = true;
    public bool TimeValid { get => _timeValid; private set => SetField(ref _timeValid, value); }

    public int AutoCloseAfterMinutes
    {
        get => Model.AutoCloseAfterMinutes;
        set { Model.AutoCloseAfterMinutes = Math.Clamp(value, 0, 24 * 60); OnPropertyChanged(); _owner.Persist(); }
    }

    public ObservableCollection<DayToggle> Days { get; } = new();

    private void BuildDays()
    {
        Days.Clear();
        CultureInfo culture;
        try { culture = new CultureInfo(LocalizationService.Current); }
        catch { culture = CultureInfo.CurrentUICulture; }

        // Start the week where the user's locale starts it.
        var first = culture.DateTimeFormat.FirstDayOfWeek;
        for (int i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)(((int)first + i) % 7);
            Days.Add(new DayToggle(day, culture.DateTimeFormat.GetShortestDayName(day), Model.Days.Contains(day), OnDaysChanged));
        }
    }

    private void OnDaysChanged()
    {
        Model.Days = Days.Where(d => d.IsOn).Select(d => d.Day).ToList();
        RaiseSchedule();
        _owner.Persist();
    }

    public string NextRunText
    {
        get
        {
            if (!Model.Enabled) return L.T("Automation.Schedule.Paused");
            var next = SchedulerService.NextRun(Model);
            return next == null ? L.T("Automation.Schedule.Never") : L.T("Automation.Schedule.Next", next.Value.ToString("ddd t", CultureInfo.CurrentCulture));
        }
    }

    public string Summary
    {
        get
        {
            string target = !string.IsNullOrEmpty(Model.PresetName) ? Model.PresetName
                          : Account?.DisplayNameOrUser ?? L.T("Automation.Schedule.NoTarget");
            string days = Model.Days.Count is 0 or 7 ? L.T("Automation.Schedule.Daily")
                        : string.Join(", ", Days.Where(d => d.IsOn).Select(d => d.Label));
            return L.T(IsLaunch ? "Automation.Schedule.SummaryLaunch" : "Automation.Schedule.SummaryClose", target, Model.TimeOfDay, days);
        }
    }

    public void RaiseSchedule()
    {
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(NextRunText));
        OnPropertyChanged(nameof(Summary));
    }

    public void RaiseAll()
    {
        BuildDays();
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>Launch presets (named sets of accounts that start together) and the scheduler that runs them.</summary>
public class AutomationViewModel : ObservableObject
{
    private readonly AccountStore _store;
    private readonly MainViewModel _main;
    private readonly System.Windows.Threading.DispatcherTimer _saveDebounce;

    public AutomationViewModel(AccountStore store, MainViewModel main)
    {
        _store = store;
        _main = main;

        _saveDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SettingsService.Save(); };

        foreach (var p in SettingsService.Current.LaunchPresets) Presets.Add(new PresetItem(p, this));
        foreach (var t in SettingsService.Current.ScheduledTasks) Schedules.Add(new ScheduleItem(t, this));

        AddPresetCommand = new RelayCommand(_ => AddPreset());
        DeletePresetCommand = new RelayCommand(p => DeletePreset(p as PresetItem ?? SelectedPreset));
        RunPresetCommand = new AsyncRelayCommand(p => RunPresetAsync(p as PresetItem ?? SelectedPreset));
        AddScheduleCommand = new RelayCommand(_ => AddSchedule());
        DeleteScheduleCommand = new RelayCommand(p => DeleteSchedule(p as ScheduleItem ?? SelectedSchedule));
        RunScheduleNowCommand = new AsyncRelayCommand(p => RunScheduleAsync(p as ScheduleItem ?? SelectedSchedule));

        _store.Accounts.CollectionChanged += (_, _) => _selectedPreset?.SyncMembers(_store.Accounts);

        // "Next run" text moves with the clock.
        var tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        tick.Tick += (_, _) => { foreach (var s in Schedules) s.RaiseSchedule(); };
        tick.Start();
    }

    public ObservableCollection<Account> Accounts => _store.Accounts;
    public ObservableCollection<PresetItem> Presets { get; } = new();
    public ObservableCollection<ScheduleItem> Schedules { get; } = new();
    public IEnumerable<string> PresetNames => Presets.Select(p => p.Name).ToList();

    private string _section = "Presets";
    public string Section { get => _section; set => SetField(ref _section, value ?? "Presets"); }

    /// <summary>Opens the first preset and schedule so the editor isn't empty on the first visit.</summary>
    public void OnShown()
    {
        SelectedPreset ??= Presets.FirstOrDefault();
        SelectedSchedule ??= Schedules.FirstOrDefault();
    }

    private PresetItem? _selectedPreset;
    public PresetItem? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetField(ref _selectedPreset, value)) return;
            _selectedPreset?.SyncMembers(_store.Accounts);
            OnPropertyChanged(nameof(HasPreset));
        }
    }
    public bool HasPreset => _selectedPreset != null;

    private ScheduleItem? _selectedSchedule;
    public ScheduleItem? SelectedSchedule
    {
        get => _selectedSchedule;
        set { if (SetField(ref _selectedSchedule, value)) OnPropertyChanged(nameof(HasSchedule)); }
    }
    public bool HasSchedule => _selectedSchedule != null;

    public RelayCommand AddPresetCommand { get; }
    public RelayCommand DeletePresetCommand { get; }
    public AsyncRelayCommand RunPresetCommand { get; }
    public RelayCommand AddScheduleCommand { get; }
    public RelayCommand DeleteScheduleCommand { get; }
    public AsyncRelayCommand RunScheduleNowCommand { get; }

    public void Persist()
    {
        SettingsService.Current.LaunchPresets = Presets.Select(p => p.Model).ToList();
        SettingsService.Current.ScheduledTasks = Schedules.Select(s => s.Model).ToList();
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    public void OnPresetRenamed(string oldName, string newName)
    {
        foreach (var s in Schedules.Where(s => string.Equals(s.Model.PresetName, oldName, StringComparison.OrdinalIgnoreCase)))
        {
            s.Model.PresetName = newName;
            s.RaiseAll();
        }
        OnPropertyChanged(nameof(PresetNames));
    }

    public void RefreshLocalized()
    {
        foreach (var p in Presets) p.RaiseAll();
        foreach (var s in Schedules) s.RaiseAll();
        OnPropertyChanged(string.Empty);
    }

    private void AddPreset()
    {
        var model = new LaunchPreset
        {
            Name = UniqueName(L.T("Automation.Preset.NewName"), Presets.Select(p => p.Name)),
            PlaceId = SettingsService.Current.DefaultPlaceId,
            JoinDelaySeconds = SettingsService.Current.AccountJoinDelay,
            Aliases = _store.Accounts.Where(a => a.IsChecked).Select(a => a.Username).ToList(),
        };
        var item = new PresetItem(model, this);
        Presets.Add(item);
        SelectedPreset = item;
        Persist();
        OnPropertyChanged(nameof(PresetNames));
    }

    private void DeletePreset(PresetItem? item)
    {
        if (item == null) return;
        if (!DialogService.Confirm(L.T("Automation.Preset.Delete.Title"), L.T("Automation.Preset.Delete.Body", item.Name), L.T("Common.Delete"), danger: true))
            return;
        Presets.Remove(item);
        if (SelectedPreset == item) SelectedPreset = Presets.FirstOrDefault();
        Persist();
        OnPropertyChanged(nameof(PresetNames));
    }

    private async Task RunPresetAsync(PresetItem? item)
    {
        if (item == null) return;
        SettingsService.Save();
        if (item.Model.Aliases.Count == 0) { _main.SetStatus(L.T("Automation.Preset.NoAccounts")); return; }
        if (item.Model.PlaceId <= 0) { _main.SetStatus(L.T("Launch.NeedPlace")); return; }
        _main.SetStatus(L.T("Automation.Preset.Running", item.Name));
        var (launched, failed) = await PresetService.LaunchAsync(item.Model);
        _main.SetStatus(L.T("Automation.Preset.Done", item.Name, launched, failed));
        if (failed > 0) ToastService.Warning(item.Name, L.T("Automation.Preset.Done", item.Name, launched, failed));
    }

    private void AddSchedule()
    {
        var model = new ScheduledTask
        {
            Name = UniqueName(L.T("Automation.Schedule.NewName"), Schedules.Select(s => s.Name)),
            TimeOfDay = DateTime.Now.AddHours(1).ToString("HH:00"),
            PresetName = SelectedPreset?.Name ?? Presets.FirstOrDefault()?.Name ?? "",
            PlaceId = SettingsService.Current.DefaultPlaceId,
            Enabled = true,
        };
        var item = new ScheduleItem(model, this);
        Schedules.Add(item);
        SelectedSchedule = item;
        Section = "Schedules";
        Persist();
    }

    private void DeleteSchedule(ScheduleItem? item)
    {
        if (item == null) return;
        if (!DialogService.Confirm(L.T("Automation.Schedule.Delete.Title"), L.T("Automation.Schedule.Delete.Body", item.Name), L.T("Common.Delete"), danger: true))
            return;
        Schedules.Remove(item);
        if (SelectedSchedule == item) SelectedSchedule = Schedules.FirstOrDefault();
        Persist();
    }

    private async Task RunScheduleAsync(ScheduleItem? item)
    {
        if (item == null) return;
        SettingsService.Save();
        var preset = string.IsNullOrEmpty(item.Model.PresetName) ? null : PresetService.Find(item.Model.PresetName);
        if (item.Model.Action == ScheduleAction.Launch)
        {
            if (preset != null) await RunPresetAsync(Presets.FirstOrDefault(p => p.Model == preset));
            else if (item.Account is { } acc && item.Model.PlaceId > 0)
            {
                var r = await LauncherService.LaunchAsync(acc, item.Model.PlaceId);
                _main.SetStatus(r.Success ? L.T("Status.Launched", acc.DisplayNameOrUser) : r.Message);
            }
            else _main.SetStatus(L.T("Automation.Schedule.Incomplete"));
        }
        else
        {
            var ids = preset != null
                ? _store.Accounts.Where(a => preset.Aliases.Any(k => PresetItem.Matches(a, k))).Select(a => a.UserId).ToList()
                : item.Account is { } a ? new List<long> { a.UserId } : new List<long>();
            int closed = await Task.Run(() => ids.Sum(InstanceControlService.CloseFor));
            _main.SetStatus(closed > 0 ? L.N("Status.ClosedClients", closed) : L.T("Status.NoClients"));
        }
    }

    private static string UniqueName(string baseName, IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;
        for (int i = 2; ; i++)
            if (!taken.Contains($"{baseName} {i}")) return $"{baseName} {i}";
    }
}
