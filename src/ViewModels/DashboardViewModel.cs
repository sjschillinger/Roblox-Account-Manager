using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using RobloxAccountManager.Models;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.ViewModels;

/// <summary>
/// Always-live view of every account's presence for the dashboard. The list binds
/// straight through to the shared account collection while the headline counters
/// (online / in-game / in-studio / offline) are kept in sync with the
/// <see cref="PresenceService"/> polling loop and any add/remove of accounts.
/// </summary>
public class DashboardViewModel : ObservableObject
{
    private readonly AccountStore _store;

    public DashboardViewModel(AccountStore store)
    {
        _store = store;
        RefreshCommand = new AsyncRelayCommand(() => PresenceService.PollNowAsync());
        PresenceService.PresenceUpdated += OnPresenceUpdated;
        _store.Accounts.CollectionChanged += OnAccountsChanged;
        Recompute();
    }

    /// <summary>The shared, observable account collection — rendered as the live list.</summary>
    public ObservableCollection<Account> Accounts => _store.Accounts;

    /// <summary>Mirrors Settings.HideUsernames so the live list can mask names (RefreshMask cross-VM pattern).</summary>
    public bool MaskUsernames => SettingsService.Current.HideUsernames;
    public void RefreshMask() => OnPropertyChanged(nameof(MaskUsernames));

    private int _total, _online, _inGame, _inStudio, _offline;
    public int Total    { get => _total;    private set => SetField(ref _total, value); }
    public int Online   { get => _online;   private set => SetField(ref _online, value); }
    public int InGame   { get => _inGame;   private set => SetField(ref _inGame, value); }
    public int InStudio { get => _inStudio; private set => SetField(ref _inStudio, value); }
    public int Offline  { get => _offline;  private set => SetField(ref _offline, value); }

    private long _totalRobux, _totalRap;
    private int _premiumCount;
    public long TotalRobux   { get => _totalRobux;   private set => SetField(ref _totalRobux, value); }
    public long TotalRap     { get => _totalRap;     private set => SetField(ref _totalRap, value); }
    public int  PremiumCount { get => _premiumCount; private set => SetField(ref _premiumCount, value); }

    private string _lastUpdated = "never";
    public string LastUpdated { get => _lastUpdated; private set => SetField(ref _lastUpdated, value); }

    // ---- playtime (#1.7.0) ----

    /// <summary>Combined playtime over the last seven days, e.g. "12h 40m".</summary>
    public string PlaytimeWeekText => PlaytimeService.Format(PlaytimeService.Last7DaysTotal);

    /// <summary>Combined playtime across the whole recorded history.</summary>
    public string PlaytimeTotalText => PlaytimeService.Format(PlaytimeService.AllTimeTotal);

    /// <summary>True once anything has been recorded — the tiles read "—" before that.</summary>
    public bool HasPlaytime => PlaytimeService.AllTimeTotal > TimeSpan.Zero;

    /// <summary>Re-reads the totals after a session was recorded.</summary>
    public void RefreshPlaytime()
    {
        OnPropertyChanged(nameof(PlaytimeWeekText));
        OnPropertyChanged(nameof(PlaytimeTotalText));
        OnPropertyChanged(nameof(HasPlaytime));
    }

    public AsyncRelayCommand RefreshCommand { get; }

    private void OnAccountsChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnPresenceUpdated();

    private void OnPresenceUpdated()
    {
        // Poll callback arrives on a threadpool thread; marshal count updates to the UI.
        var d = Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess()) d.BeginInvoke(new Action(Recompute));
        else Recompute();
    }

    private void Recompute()
    {
        var list = _store.Accounts.ToList();
        Total    = list.Count;
        Online   = list.Count(a => a.Presence == "Online");
        InGame   = list.Count(a => a.Presence == "In Game");
        InStudio = list.Count(a => a.Presence == "In Studio");
        Offline  = list.Count - Online - InGame - InStudio;
        TotalRobux   = list.Where(a => a.Robux > 0).Sum(a => a.Robux);
        TotalRap     = list.Where(a => a.Rap > 0).Sum(a => a.Rap);
        PremiumCount = list.Count(a => a.IsPremium);
        LastUpdated = DateTime.Now.ToString("HH:mm:ss");
    }
}
