using System.Collections.ObjectModel;
using RobloxAccountManager.Models;
using RobloxAccountManager.Mvvm;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.ViewModels;

public sealed class PaletteItem
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string IconKey { get; init; } = "Icon.ArrowRight";
    public string? ImageUrl { get; init; }
    public string? Presence { get; init; }
    public bool IsAccount { get; init; }
    public string Shortcut { get; init; } = "";
    public string Keywords { get; init; } = "";
    public Action Execute { get; init; } = () => { };
}

/// <summary>
/// Ctrl+K: one search box for every account and every action. Typing filters; Enter runs the top
/// result; arrow keys move the selection.
/// </summary>
public class CommandPaletteViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private List<PaletteItem> _all = new();

    public ObservableCollection<PaletteItem> Results { get; } = new();

    public CommandPaletteViewModel(MainViewModel main)
    {
        _main = main;
        CloseCommand = new RelayCommand(_ => IsOpen = false);
        RunCommand = new RelayCommand(p => Run(p as PaletteItem ?? Selected));
    }

    private bool _isOpen;
    public bool IsOpen { get => _isOpen; set => SetField(ref _isOpen, value); }

    private string _query = "";
    public string Query
    {
        get => _query;
        set { if (SetField(ref _query, value ?? "")) Filter(); }
    }

    private PaletteItem? _selected;
    public PaletteItem? Selected { get => _selected; set => SetField(ref _selected, value); }

    public RelayCommand CloseCommand { get; }
    public RelayCommand RunCommand { get; }

    public void Open()
    {
        if (LockService.IsLocked) return;
        _all = Build();
        _query = "";
        OnPropertyChanged(nameof(Query));
        Filter();
        IsOpen = true;
    }

    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        int i = Selected == null ? -1 : Results.IndexOf(Selected);
        i = Math.Clamp(i + delta, 0, Results.Count - 1);
        Selected = Results[i];
    }

    private void Run(PaletteItem? item)
    {
        if (item == null) return;
        IsOpen = false;
        try { item.Execute(); }
        catch (Exception ex) { DiagnosticsService.Warn("palette", $"Command '{item.Title}' failed", ex); }
    }

    private void Filter()
    {
        string q = _query.Trim();
        IEnumerable<PaletteItem> hits = _all;
        if (q.Length > 0)
        {
            hits = _all
                .Select(i => (item: i, score: Score(i, q)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .Select(x => x.item);
        }

        Results.Clear();
        foreach (var h in hits.Take(40)) Results.Add(h);
        Selected = Results.FirstOrDefault();
    }

    /// <summary>Prefix matches beat word matches beat substring matches; titles beat keywords.</summary>
    private static int Score(PaletteItem item, string q)
    {
        int best = 0;
        void Consider(string text, int weight)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (text.StartsWith(q, StringComparison.CurrentCultureIgnoreCase)) best = Math.Max(best, 30 * weight);
            else if (text.Contains(" " + q, StringComparison.CurrentCultureIgnoreCase)) best = Math.Max(best, 20 * weight);
            else if (text.Contains(q, StringComparison.CurrentCultureIgnoreCase)) best = Math.Max(best, 10 * weight);
        }
        Consider(item.Title, 3);
        Consider(item.Subtitle, 2);
        Consider(item.Keywords, 1);
        return best;
    }

    private List<PaletteItem> Build()
    {
        var items = new List<PaletteItem>();
        var acc = _main.Accounts;

        void Action(string titleKey, string icon, Action run, string shortcut = "", string keywords = "")
            => items.Add(new PaletteItem
            {
                Title = L.T(titleKey),
                IconKey = icon,
                Execute = run,
                Shortcut = shortcut,
                Keywords = keywords,
                Subtitle = L.T("Palette.Action"),
            });

        Action("Palette.AddAccount", "Icon.UserPlus", () => acc.AddCommand.Execute(null), "Ctrl+N");
        Action("Palette.Import", "Icon.Import", () => acc.ImportCommand.Execute(null));
        Action("Palette.LaunchSelected", "Icon.Play", () => { _main.SelectedIndex = Pages.Accounts; acc.LaunchCommand.Execute(null); });
        Action("Palette.RefreshAccounts", "Icon.Refresh", () => acc.RefreshAllCommand.Execute(null), "F5");
        Action("Palette.CheckSessions", "Icon.ShieldCheck", () => _main.Dashboard.ValidateAllCommand.Execute(null));
        Action("Palette.CloseClients", "Icon.Stop", _main.CloseAllClients);
        Action("Palette.ArrangeWindows", "Icon.Grid", () => _main.Dashboard.ArrangeCommand.Execute(null));
        Action("Palette.ToggleTheme", "Icon.Moon", () => _main.ToggleThemeCommand.Execute(null));
        if (LockService.CanLock) Action("Palette.Lock", "Icon.Lock", () => LockService.Lock(), "Ctrl+L");
        Action("Palette.CheckUpdates", "Icon.Download", () => _ = _main.CheckForUpdateNowAsync());
        Action("Palette.WhatsNew", "Icon.Sparkles", () => _ = _main.ShowWhatsNewAsync());

        foreach (var nav in _main.NavItems.Append(_main.SettingsNav))
        {
            var n = nav;
            items.Add(new PaletteItem
            {
                Title = L.T("Palette.GoTo", n.Title),
                Subtitle = L.T("Palette.Page"),
                IconKey = n.IconKey,
                Shortcut = n.Shortcut,
                Execute = () => _main.SelectedIndex = n.Index,
            });
        }

        foreach (var cat in _main.Settings.Categories)
        {
            var c = cat;
            items.Add(new PaletteItem
            {
                Title = L.T("Palette.SettingsCategory", c.Title),
                Subtitle = L.T("Palette.Settings"),
                IconKey = c.IconKey,
                Keywords = c.Keywords,
                Execute = () => { _main.Settings.SelectedCategory = c; _main.SelectedIndex = Pages.Settings; },
            });
        }

        foreach (var a in _main.Store.Accounts.OrderBy(a => a.DisplayNameOrUser, StringComparer.CurrentCultureIgnoreCase))
        {
            var account = a;
            bool mask = SettingsService.Current.HideUsernames;
            items.Add(new PaletteItem
            {
                Title = mask ? new string('•', 8) : account.DisplayNameOrUser,
                Subtitle = mask ? account.Group : $"{account.SecondaryName}   {account.Group}".Trim(),
                ImageUrl = account.ThumbnailUrl,
                Presence = account.Presence,
                IsAccount = true,
                Keywords = mask ? "" : $"{account.Username} {account.DisplayName} {account.Alias} {account.Group} {account.UserId}",
                Execute = () => _main.ShowAccount(account),
            });
        }

        return items;
    }
}
