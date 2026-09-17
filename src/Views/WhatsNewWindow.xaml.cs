using System.Windows;
using System.Windows.Input;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.Views;

/// <summary>
/// The changelog window: shown once on the first run of a new version, and on demand from
/// Settings → Updates. The notes come from the copy of CHANGELOG.md compiled into this build, with
/// the matching GitHub release as a fallback. <b>All versions</b> switches to every past version
/// from the same embedded copy, so the history is readable offline too.
/// </summary>
public partial class WhatsNewWindow : Window
{
    private readonly string _releasePageUrl;
    private readonly string? _versionNotes;
    private readonly string? _fullChangelog;

    /// <param name="versionText">Display version, e.g. "v2.0.0".</param>
    /// <param name="notes">Release notes for that version, or null when none could be found.</param>
    /// <param name="releasePageUrl">GitHub release page; a tag URL is derived when null.</param>
    /// <param name="postUpdate">True right after an update installed, so the subtitle can say so.</param>
    public WhatsNewWindow(string versionText, string? notes, string? releasePageUrl, bool postUpdate = false)
    {
        InitializeComponent();

        VersionChip.Text = versionText;
        _releasePageUrl = string.IsNullOrEmpty(releasePageUrl)
            ? $"https://github.com/Vaelixx/Roblox-Account-Manager/releases/tag/{versionText}"
            : releasePageUrl;

        SubtitleText.Text = postUpdate ? L.T("Updates.Installed") : L.T("Updates.ChangesIn", versionText);

        _versionNotes = notes;
        _fullChangelog = UpdateService.EmbeddedChangelog;

        // Only worth offering when there is more history than the section already on screen.
        HistorySwitch.Visibility = string.IsNullOrWhiteSpace(_fullChangelog) || _fullChangelog == notes
            ? Visibility.Collapsed
            : Visibility.Visible;

        ReleaseNotesRenderer.Render(_versionNotes, NotesPanel);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (Owner == null) WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private void Notes_Checked(object sender, RoutedEventArgs e)
    {
        if (NotesPanel == null) return;
        bool history = HistoryTab.IsChecked == true;
        ReleaseNotesRenderer.Render(history ? _fullChangelog : _versionNotes, NotesPanel);
        NotesScroll.ScrollToTop();
    }

    private void ViewOnGitHub_Click(object sender, RoutedEventArgs e) => BrowserService.OpenUrl(_releasePageUrl);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Drag_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }
}
