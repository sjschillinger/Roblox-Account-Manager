using System.Collections.ObjectModel;
using System.Windows;
using RobloxAccountManager.Models;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.ViewModels;

/// <summary>
/// Backs the Friends page: pick one of your accounts, see its Roblox friends with who is online and
/// in which game, and follow one into their server with any of your accounts.
/// </summary>
public class FriendsViewModel : ObservableObject
{
    private readonly AccountStore _store;
    private readonly MainViewModel _main;
    private readonly List<Friend> _all = new();

    public FriendsViewModel(AccountStore store, MainViewModel main)
    {
        _store = store;
        _main = main;

        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        JoinCommand = new AsyncRelayCommand(p => JoinAsync(p as Friend));
        OpenProfileCommand = new RelayCommand(p => { if (p is Friend f) BrowserService.OpenProfile(f.UserId); });
        CopyUsernameCommand = new RelayCommand(p =>
        {
            if (p is Friend f && f.Username.Length > 0)
                _main.SetStatus(ClipboardService.CopyText(f.Username) ? L.T("Status.Copied") : L.T("Status.ClipboardBusy"));
        });

        // The store is filled a moment after this view-model is built, so pick the first account once
        // it actually arrives instead of leaving the page permanently empty.
        _selectedAccount = _store.Accounts.FirstOrDefault();
        _store.Accounts.CollectionChanged += (_, _) =>
        {
            if (_selectedAccount == null || !_store.Accounts.Contains(_selectedAccount))
                SelectedAccount = _store.Accounts.FirstOrDefault(a => a.IsValid) ?? _store.Accounts.FirstOrDefault();
        };
    }

    public ObservableCollection<Account> Accounts => _store.Accounts;
    public ObservableCollection<Friend> Friends { get; } = new();

    public bool MaskUsernames => SettingsService.Current.HideUsernames;
    public void RefreshMask() => OnPropertyChanged(nameof(MaskUsernames));

    private Account? _selectedAccount;
    public Account? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!SetField(ref _selectedAccount, value)) return;
            // Switching accounts drops the previous list, or the "already loaded" check would keep it.
            _all.Clear();
            Friends.Clear();
            _loadedFor = null;
            RaiseList();
            if (_isShown) _ = RefreshAsync();
        }
    }

    private string _searchText = "";
    public string SearchText { get => _searchText; set { if (SetField(ref _searchText, value ?? "")) ApplyFilter(); } }

    private string _filter = "All";
    /// <summary>All | Online | InGame</summary>
    public string Filter { get => _filter; set { if (SetField(ref _filter, value ?? "All")) ApplyFilter(); } }

    private bool _busy;
    public bool Busy { get => _busy; private set { if (SetField(ref _busy, value)) RaiseList(); } }

    private string? _error;
    public string? Error { get => _error; private set { if (SetField(ref _error, value)) RaiseList(); } }

    public int CountAll => _all.Count;
    public int CountOnline => _all.Count(f => f.IsOnline);
    public int CountInGame => _all.Count(f => f.IsInGame);

    public bool IsEmpty => !_busy && Friends.Count == 0;
    public bool ShowLoading => _busy && Friends.Count == 0;

    public string EmptyText =>
        _error ?? (_selectedAccount == null ? L.T("Friends.Empty.NoAccount")
                 : _loadedFor == null ? L.T("Friends.Empty.NotLoaded")
                 : _all.Count == 0 ? L.T("Friends.Empty.None")
                 : L.T("Friends.Empty.NoMatch"));

    public string Subtitle => _all.Count == 0 ? L.T("Friends.Subtitle") : L.N("Friends.Count", _all.Count, CountOnline);

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand JoinCommand { get; }
    public RelayCommand OpenProfileCommand { get; }
    public RelayCommand CopyUsernameCommand { get; }

    private Account? _loadedFor;
    private bool _isShown;

    public void EnsureLoaded()
    {
        _isShown = true;
        if (AppInfo.IsDemo || Busy || SelectedAccount == null || ReferenceEquals(_loadedFor, SelectedAccount)) return;
        _ = RefreshAsync();
    }

    public void RefreshLocalized()
    {
        foreach (var f in _all) f.RaiseLocalized();
        OnPropertyChanged(string.Empty);
    }

    private void RaiseList()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowLoading));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(CountAll));
        OnPropertyChanged(nameof(CountOnline));
        OnPropertyChanged(nameof(CountInGame));
    }

    private async Task RefreshAsync()
    {
        var acc = SelectedAccount;
        if (acc == null) return;
        if (string.IsNullOrEmpty(acc.Cookie) || acc.UserId <= 0 || !acc.IsValid)
        {
            Error = L.T("Friends.NoSession", acc.DisplayNameOrUser);
            return;
        }

        Error = null;
        Busy = true;
        _main.SetStatus(L.T("Friends.Loading", acc.DisplayNameOrUser));
        try
        {
            var friends = await RobloxApi.GetFriendsAsync(acc.Cookie, acc.UserId);
            if (!ReferenceEquals(acc, SelectedAccount)) return;   // the user switched accounts meanwhile

            _all.Clear();
            _all.AddRange(friends.OrderBy(f => f.DisplayNameOrUser, StringComparer.CurrentCultureIgnoreCase));
            _loadedFor = acc;
            ApplyFilter();

            if (_all.Count > 0)
            {
                var ids = _all.Select(f => f.UserId).ToList();
                var presence = await RobloxApi.GetPresenceDetailsAsync(acc.Cookie, ids);
                var heads = await RobloxApi.GetHeadshotsAsync(ids);
                if (!ReferenceEquals(acc, SelectedAccount)) return;

                foreach (var f in _all)
                {
                    if (presence.TryGetValue(f.UserId, out var pd))
                    {
                        f.Presence = pd.Status;
                        f.LastLocation = pd.LastLocation;
                        f.PlaceId = pd.PlaceId;
                        f.RootPlaceId = pd.RootPlaceId;
                        f.JobId = pd.JobId;
                    }
                    if (heads.TryGetValue(f.UserId, out var url)) f.HeadshotUrl = url;
                }
                _all.Sort((a, b) =>
                {
                    int r = PresenceStatus.Rank(a.Presence).CompareTo(PresenceStatus.Rank(b.Presence));
                    return r != 0 ? r : string.Compare(a.DisplayNameOrUser, b.DisplayNameOrUser, StringComparison.CurrentCultureIgnoreCase);
                });
                ApplyFilter();
            }

            _main.SetStatus(L.N("Friends.Count", _all.Count, CountOnline));
        }
        catch (Exception ex)
        {
            Error = L.T("Friends.LoadFailed", ex.Message);
            _main.SetStatus(Error);
        }
        finally { Busy = false; }
    }

    private void ApplyFilter()
    {
        var q = _searchText.Trim();
        Friends.Clear();
        foreach (var f in _all)
        {
            if (_filter == "Online" && !f.IsOnline) continue;
            if (_filter == "InGame" && !f.IsInGame) continue;
            if (q.Length > 0
                && !f.Username.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                && !f.DisplayName.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                continue;
            Friends.Add(f);
        }
        RaiseList();
    }

    private async Task JoinAsync(Friend? friend)
    {
        if (friend == null) return;
        var acc = SelectedAccount;
        if (acc == null) return;
        if (!friend.CanJoin)
        {
            _main.SetStatus(L.T("Friends.NotJoinable", friend.DisplayNameOrUser));
            return;
        }

        long place = friend.RootPlaceId > 0 ? friend.RootPlaceId : friend.PlaceId;
        _main.SetStatus(L.T("Friends.Joining", friend.DisplayNameOrUser));
        var r = await LauncherService.LaunchAsync(acc, place, jobId: friend.JobId, followUserId: friend.UserId);
        _main.SetStatus(r.Success ? L.T("Friends.Joined", acc.DisplayNameOrUser, friend.DisplayNameOrUser) : r.Message);
        if (!r.Success) ToastService.Error(L.T("Launch.FailedTitle"), r.Message);
    }
}
