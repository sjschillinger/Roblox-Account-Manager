using System.Windows;
using System.Windows.Input;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.Views;

public partial class ChromiumDownloadDialog : Window
{
    private readonly CancellationTokenSource _cts = new();

    /// <summary>True once the download ended (successfully or not) and the dialog may close itself.</summary>
    private bool _finished;

    public bool Installed { get; private set; }

    public ChromiumDownloadDialog()
    {
        InitializeComponent();
        PhaseText.Text = L.T("Chromium.Phase.Starting");
        Loaded += OnLoaded;
        Closing += (_, _) => { if (!_finished) _cts.Cancel(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Cancel_Click(this, new RoutedEventArgs()); } };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { } };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var progress = new Progress<ChromiumService.Progress>(Report);
        try
        {
            await ChromiumService.DownloadAsync(progress, _cts.Token);
            Installed = ChromiumService.IsInstalled;
            _finished = true;
            CloseWith(Installed);
        }
        catch (OperationCanceledException)
        {
            _finished = true;
            CloseWith(false);
        }
        catch (Exception ex)
        {
            // Stay open so the reason can be read; the button now just closes.
            _finished = true;
            DiagnosticsService.Warn("chromium", "CloakBrowser download failed", ex);
            PhaseText.Text = L.T("Chromium.Failed", ex.Message);
            PhaseText.SetResourceReference(ForegroundProperty, "DangerBrush");
            PhaseText.TextWrapping = TextWrapping.Wrap;
            SizeText.Text = "";
            CancelBtn.Content = L.T("Common.Close");
        }
    }

    /// <summary>Sets the result exactly once, and only while the dialog is still showing.</summary>
    private void CloseWith(bool result)
    {
        if (!IsLoaded) return;
        try { DialogResult = result; }
        catch (InvalidOperationException) { Close(); }
    }

    private void Report(ChromiumService.Progress p)
    {
        PhaseText.Text = p.Phase;
        if (p.Total > 1)
        {
            Bar.Value = p.Fraction;
            SizeText.Text = $"{p.Done / 1024 / 1024:N0} / {p.Total / 1024 / 1024:N0} MB";
        }
        else
        {
            if (p.Total == 1 && p.Done == 1) Bar.Value = 1;
            SizeText.Text = "";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // During the download, cancel the token and let OnLoaded close the dialog once the task has
        // unwound; setting DialogResult here as well used to throw when OnLoaded set it a second time.
        if (!_finished) { CancelBtn.IsEnabled = false; _cts.Cancel(); return; }
        CloseWith(Installed);
    }
}
