#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RobloxAccountManager.Models;
using RobloxAccountManager.ViewModels;
using RobloxAccountManager.Views;

namespace RobloxAccountManager.Services;

/// <summary>
/// Debug builds only. <c>--demo</c> starts the app on a throw-away data folder filled with made-up
/// accounts, with every network poller, registry write and update check switched off, so the UI can
/// be worked on and screenshotted without touching real accounts.
///
/// Options: <c>--demo-theme Light|Dark</c>, <c>--demo-accent Iris</c>, <c>--demo-lang de</c>, and
/// <c>--demo-shots &lt;folder&gt;</c> to render every page to PNG files and exit.
/// </summary>
internal static class DemoMode
{
    private static string? _shotsDir;
    private static string _theme = "", _accent = "", _lang = "";
    private static System.Diagnostics.TraceListener? _bindingLog;

    public static void Configure(string[] args)
    {
        if (!args.Contains("--demo")) return;
        AppInfo.IsDemo = true;

        string dir = Path.Combine(Path.GetTempPath(), "RobloxAccountManagerDemo", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Paths.OverrideDataDir(dir);
        WriteSamplePlaytime(Path.Combine(dir, "playtime.json"));

        _shotsDir = Value(args, "--demo-shots");
        _theme = Value(args, "--demo-theme") ?? "";
        _accent = Value(args, "--demo-accent") ?? "";
        _lang = Value(args, "--demo-lang") ?? "";

        if (_shotsDir != null)
        {
            // Broken bindings fail silently in WPF; collect them next to the screenshots.
            Directory.CreateDirectory(_shotsDir);
            var log = _bindingLog = new System.Diagnostics.TextWriterTraceListener(Path.Combine(_shotsDir, "bindings.log"));
            System.Diagnostics.PresentationTraceSources.Refresh();
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(log);
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Warning;
            System.Diagnostics.PresentationTraceSources.ResourceDictionarySource.Listeners.Add(log);
            System.Diagnostics.PresentationTraceSources.DataBindingSource.TraceEvent(System.Diagnostics.TraceEventType.Warning, 0, "binding trace active");
        }
    }

    /// <summary>A few days of made-up play sessions, so playtime columns and "Recent sessions" have content.</summary>
    private static void WriteSamplePlaytime(string path)
    {
        var rng = new Random(7);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sessions = new List<PlaySession>();
        long[] users = { 1480001, 1480001, 1480004, 1480005, 1480002, 1480003, 1480004, 1480006, 1480001, 1480005 };
        long[] places = { 2753915549, 8737899170, 8737899170, 606849621, 4924922222 };
        long end = now - 1800;
        foreach (long user in users)
        {
            long length = 900 + rng.Next(0, 3 * 3600);
            sessions.Add(new PlaySession { UserId = user, StartUnix = end - length, EndUnix = end, PlaceId = places[rng.Next(places.Length)] });
            end -= length + rng.Next(1800, 9 * 3600);
        }
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(sessions));
    }

    private static string? Value(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static void AdjustSettings(AppSettings s)
    {
        if (!AppInfo.IsDemo) return;
        s.CheckUpdatesOnStartup = false;
        s.AutoCheckUpdates = false;
        s.ValidateCookiesOnStartup = false;
        s.MinimizeToTray = false;
        s.WindowWidth = 1280;
        s.WindowHeight = 800;
        s.WindowMaximized = Environment.GetCommandLineArgs().Contains("--demo-maximized");
        if (_theme.Length > 0) s.ThemeMode = _theme;
        if (_accent.Length > 0) s.AccentName = _accent;
        if (_lang.Length > 0) s.Language = _lang;

        s.LaunchPresets = new()
        {
            new LaunchPreset { Name = "Evening farm", Aliases = new() { "quietfern_07", "tidepool_42", "ember.alt" }, PlaceId = 8737899170, JoinDelaySeconds = 10 },
            new LaunchPreset { Name = "Trading squad", Aliases = new() { "LunaVoyage", "orbit_moss" }, PlaceId = 2753915549 },
        };
        s.ScheduledTasks = new()
        {
            new ScheduledTask { Name = "Start farm", PresetName = "Evening farm", TimeOfDay = "18:30",
                Days = new() { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday }, AutoCloseAfterMinutes = 180 },
            new ScheduledTask { Name = "Close everything", Action = ScheduleAction.Close, Alias = "NovaRunner", TimeOfDay = "23:00", Enabled = false },
        };
    }

    public static void Populate(MainViewModel vm)
    {
        var store = vm.Store;
        store.IsReadOnly = true;
        var now = DateTime.UtcNow;

        Account Make(long id, string user, string display, string group, string presence, long robux,
                     double addedDays, double validatedHours, string location = "", bool premium = false,
                     bool favorite = false, bool rejected = false, bool neverValidated = false, string color = "")
        {
            var a = new Account
            {
                Cookie = "_|WARNING:-DO-NOT-SHARE-THIS.--demo-" + id,
                UserId = id,
                Username = user,
                DisplayName = display,
                Group = group,
                Color = color,
                IsFavorite = favorite,
                AddedUtc = now.AddDays(-addedDays),
                CookieUpdatedUtc = now.AddDays(-addedDays),
                LastValidatedUtc = neverValidated ? null : now.AddHours(-validatedHours),
                CookieRejectedUtc = rejected ? now.AddHours(-3) : null,
            };
            a.Presence = presence;
            a.LastLocation = location;
            a.Robux = robux;
            a.IsPremium = premium;
            return a;
        }

        var accounts = new[]
        {
            Make(1480001, "NovaRunner", "Nova", "Main", PresenceStatus.InGame, 12450, 214, 2, "Blox Fruits", premium: true, favorite: true, color: "#6E8BFF"),
            Make(1480002, "PixelMarlow", "Marlow", "Main", PresenceStatus.Online, 830, 96, 5),
            Make(1480003, "Sable_Knight", "Sable", "Main", PresenceStatus.Online, 2210, 40, 30, favorite: true),
            Make(1480004, "quietfern_07", "Fern", "Farm", PresenceStatus.InGame, 45, 60, 8, "Pet Simulator 99"),
            Make(1480005, "tidepool_42", "tidepool_42", "Farm", PresenceStatus.InGame, 0, 400, 12, "Pet Simulator 99"),
            Make(1480006, "KaiTheBuilder", "Kai", "Farm", PresenceStatus.InStudio, 150, 33, 20),
            Make(1480007, "ember.alt", "Ember", "Farm", PresenceStatus.Offline, 12, 120, 24 * 21),
            Make(1480008, "LunaVoyage", "Luna", "Trading", PresenceStatus.Offline, 5600, 180, 50, rejected: true, color: "#E5484D"),
            Make(1480009, "orbit_moss", "orbit_moss", "Trading", PresenceStatus.Offline, 310, 3, 0, neverValidated: true),
        };
        foreach (var a in accounts) store.Accounts.Add(a);
        vm.Accounts.Selected = accounts[0];
        vm.SetStatus(L.N("Status.Loaded", accounts.Length));
    }

    public static void RunScript(Window window, MainViewModel vm)
    {
        if (_shotsDir == null) return;
        Directory.CreateDirectory(_shotsDir);
        _ = RunScriptAsync(window, vm);
    }

    private static async Task RunScriptAsync(Window window, MainViewModel vm)
    {
        string tag = $"{(ThemeService.IsLight ? "light" : "dark")}-{LocalizationService.Current}";
        try
        {
            // Off screen, so wherever the real mouse cursor is cannot leave hover highlights in the shots.
            window.Left = -6000;
            await Settle(1200);
            string[] pages = { "overview", "accounts", "friends", "servers", "automation", "settings" };
            for (int i = 0; i < pages.Length; i++)
            {
                vm.SelectedIndex = i;
                await Settle();
                Shot((FrameworkElement)window.Content, $"{tag}-{i}-{pages[i]}.png");
            }

            foreach (var cat in new[] { "Appearance", "Launching", "Graphics", "Security", "Integrations", "Updates" })
            {
                vm.Settings.SelectedCategory = vm.Settings.Categories.First(c => c.Key == cat);
                await Settle();
                Shot((FrameworkElement)window.Content, $"{tag}-settings-{cat.ToLowerInvariant()}.png");
            }

            vm.SelectedIndex = Pages.Accounts;
            vm.Accounts.Selected = vm.Store.Accounts[7];
            vm.Accounts.InspectorTab = "Security";
            await Settle();
            Shot((FrameworkElement)window.Content, $"{tag}-accounts-security.png");
            vm.Accounts.InspectorTab = "Overview";
            vm.Accounts.IsCompact = true;
            await Settle();
            Shot((FrameworkElement)window.Content, $"{tag}-accounts-compact.png");
            vm.Accounts.IsCompact = false;

            vm.Palette.Open();
            await Settle();
            Shot((FrameworkElement)window.Content, $"{tag}-palette.png");
            vm.Palette.IsOpen = false;

            await ShotDialog(new AddAccountDialog(vm.Store), $"{tag}-dialog-add.png");
            await ShotDialog(new MessageDialog(MessageDialog.Kind.Confirm, L.T("Remove.Title"), L.T("Remove.Body", "NovaRunner"),
                "", L.T("Common.Remove"), true, L.T("Common.Cancel"), danger: true), $"{tag}-dialog-confirm.png");
            await ShotDialog(new MessageDialog(MessageDialog.Kind.NewPassword, L.T("Security.Password.SetTitle"), L.T("Security.Password.SetBody"),
                "", L.T("Common.Save"), true, L.T("Common.Cancel")) { MinLength = 8 }, $"{tag}-dialog-password.png");
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(_shotsDir!, $"{tag}-error.txt"), ex.ToString());
        }
        finally
        {
            _bindingLog?.Flush();
            Application.Current.Shutdown();
        }

        async Task ShotDialog(Window dlg, string file)
        {
            dlg.Owner = window;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dlg.Show();
            await Settle(500);
            Shot(dlg, file);
            dlg.Close();
        }
    }

    private static async Task Settle(int ms = 450)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(ms);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static void Shot(FrameworkElement element, string file)
    {
        element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(_shotsDir!, file));
        encoder.Save(stream);
    }
}
#endif
