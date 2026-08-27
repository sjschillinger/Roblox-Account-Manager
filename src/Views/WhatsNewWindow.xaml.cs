using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.Views;

/// <summary>
/// The changelog window: shown once on the first run of a new version, and on demand from
/// Settings → About. The notes come from the copy of CHANGELOG.md compiled into this build, with
/// the matching GitHub release as a fallback. <b>Full changelog</b> switches to every past version
/// from the same embedded copy, so the history is readable offline too.
/// </summary>
public partial class WhatsNewWindow : Window
{
    private readonly string _releasePageUrl;
    private readonly string? _versionNotes;
    private readonly string? _fullChangelog;
    private bool _showingHistory;

    /// <param name="versionText">Display version, e.g. "v1.6.0".</param>
    /// <param name="notes">Release notes for that version, or null when none could be found.</param>
    /// <param name="releasePageUrl">GitHub release page; a tag URL is derived when null.</param>
    /// <param name="postUpdate">
    /// True right after an update installed. The subtitle otherwise claims an update just
    /// happened even when the window was opened by hand from the settings page.
    /// </param>
    public WhatsNewWindow(string versionText, string? notes, string? releasePageUrl, bool postUpdate = false)
    {
        InitializeComponent();

        VersionChip.Text = versionText;
        _releasePageUrl = string.IsNullOrEmpty(releasePageUrl)
            ? $"https://github.com/Vaelixx/Roblox-Account-Manager/releases/tag/{versionText}"
            : releasePageUrl;

        SubtitleText.Text = postUpdate
            ? "The update was installed successfully. Here's what changed:"
            : $"What changed in {versionText}:";

        _versionNotes = notes;
        _fullChangelog = UpdateService.EmbeddedChangelog;

        // Only worth offering when there is more history than the section already on screen.
        HistoryBtn.Visibility = string.IsNullOrWhiteSpace(_fullChangelog) || _fullChangelog == notes
            ? Visibility.Collapsed
            : Visibility.Visible;

        ReleaseNotesRenderer.Render(_versionNotes, NotesPanel);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (Owner == null)
        {
            // Tray-only start: no visible owner to center on.
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        // Rounded corners on Windows 11 (same treatment as UpdaterWindow).
        var hwnd = new WindowInteropHelper(this).Handle;
        int pref = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, sizeof(int));
    }

    private void ToggleHistory_Click(object sender, RoutedEventArgs e)
    {
        _showingHistory = !_showingHistory;
        HistoryBtn.Content = _showingHistory ? "This version" : "Full changelog";
        ReleaseNotesRenderer.Render(_showingHistory ? _fullChangelog : _versionNotes, NotesPanel);
        NotesScroll.ScrollToTop();
    }

    private void ViewOnGitHub_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_releasePageUrl) { UseShellExecute = true }); }
        catch { /* browser launch is best-effort */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
