using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.Views;

/// <summary>
/// Update prompt: current → new version, publish date, download size and the release notes.
/// DialogResult == true means "install now"; <see cref="Skipped"/> tells "Later" apart from
/// "never offer this version again".
/// </summary>
public partial class UpdatePromptWindow : Window
{
    private readonly string _releasePageUrl;

    /// <summary>True when the user pressed "Skip this version" rather than "Later".</summary>
    public bool Skipped { get; private set; }

    public UpdatePromptWindow(UpdateInfo info)
    {
        InitializeComponent();

        CurrentVersionText.Text = UpdateService.CurrentVersionText;
        NewVersionText.Text = info.VersionText;

        if (info.PublishedAt is { } dt) AddMeta("Icon.Calendar", L.T("Updates.Published", dt.ToLocalTime().ToString("d")));
        if (info.SizeText.Length > 0) AddMeta("Icon.Download", info.SizeText);
        if (!string.IsNullOrEmpty(info.Sha256)) AddMeta("Icon.ShieldCheck", L.T("Updates.Checksum"));
        if (info.IsPrerelease) AddMeta("Icon.Alert", L.T("Updates.Prerelease"));

        _releasePageUrl = string.IsNullOrEmpty(info.ReleasePageUrl)
            ? "https://github.com/Vaelixx/Roblox-Account-Manager/releases/latest"
            : info.ReleasePageUrl;

        ReleaseNotesRenderer.Render(info.Notes, NotesPanel);

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; DialogResult = false; } };
    }

    private void AddMeta(string iconKey, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
        var icon = new System.Windows.Shapes.Path
        {
            Data = (System.Windows.Media.Geometry)FindResource(iconKey),
            Style = (Style)FindResource("IconPath"),
            Width = 13, Height = 13,
            Margin = new Thickness(0, 0, 6, 0),
        };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextMutedBrush");
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("Text.Caption"), VerticalAlignment = VerticalAlignment.Center });
        MetaPanel.Children.Add(row);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (Owner == null) WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private void ViewOnGitHub_Click(object sender, RoutedEventArgs e) => BrowserService.OpenUrl(_releasePageUrl);

    private void UpdateNow_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Later_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        Skipped = true;
        DialogResult = false;
    }

    private void Drag_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }
}
