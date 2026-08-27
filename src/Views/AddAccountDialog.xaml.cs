using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RobloxAccountManager.Models;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.Views;

/// <summary>
/// Adds an account either by signing in with the Roblox username and password — the session
/// cookie is then fetched automatically — or by pasting a <c>.ROBLOSECURITY</c> cookie directly.
///
/// Sign-in is the default because finding a cookie by hand means opening browser developer tools,
/// which is the single step most people get stuck on. The password is handed to
/// <see cref="RobloxAuthService"/>, which posts it to Roblox and nowhere else; it is held only for
/// the duration of the dialog and never stored. When Roblox insists on a captcha or device
/// confirmation, the browser window from <see cref="BrowserService.CaptureLoginCookieAsync"/>
/// finishes the job instead.
/// </summary>
public partial class AddAccountDialog : Window
{
    private enum Mode { SignIn, Cookie }

    private readonly AccountStore _store;
    private bool _working;
    private Mode _mode = Mode.SignIn;

    /// <summary>Set once Roblox asks for a two-step code; the next submit answers it.</summary>
    private RobloxAuthService.TwoStepChallenge? _challenge;

    /// <summary>Non-null while a browser sign-in is running, so Cancel aborts it.</summary>
    private CancellationTokenSource? _browserCts;

    public Account? Added { get; private set; }

    public AddAccountDialog(AccountStore store)
    {
        InitializeComponent();
        _store = store;
        Loaded += (_, _) => UserBox.Focus();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Cancel_Click(this, new RoutedEventArgs()); };
    }

    // ---------------------------------------------------------------
    //  Mode switch
    // ---------------------------------------------------------------

    private void SignInTab_Click(object sender, RoutedEventArgs e) => SetMode(Mode.SignIn);

    private void CookieTab_Click(object sender, RoutedEventArgs e) => SetMode(Mode.Cookie);

    private void SetMode(Mode mode)
    {
        if (_working || _mode == mode) return;
        _mode = mode;

        bool signIn = mode == Mode.SignIn;
        SignInTab.Style = (Style)FindResource(signIn ? "PrimaryButton" : "GhostButton");
        CookieTab.Style = (Style)FindResource(signIn ? "GhostButton" : "PrimaryButton");

        SignInPanel.Visibility = signIn ? Visibility.Visible : Visibility.Collapsed;
        CookiePanel.Visibility = signIn ? Visibility.Collapsed : Visibility.Visible;

        ModeHint.Text = signIn
            ? "Sign in with the account's Roblox username and password. The session is fetched "
              + "automatically — you never have to find a cookie yourself."
            : "Paste the account's .ROBLOSECURITY cookie. It is checked against Roblox before it is added.";

        // Leaving sign-in abandons any half-finished two-step attempt.
        if (!signIn) ClearTwoFactor();

        StatusBar.Visibility = Visibility.Collapsed;
        UpdateActionButton();

        if (signIn) UserBox.Focus(); else CookieBox.Focus();
    }

    private void UpdateActionButton()
        => AddBtn.Content = _mode == Mode.Cookie ? "Add account"
                          : _challenge != null ? "Verify"
                          : "Sign in";

    // ---------------------------------------------------------------
    //  Input
    // ---------------------------------------------------------------

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                CookieBox.Text = Clipboard.GetText().Trim();
                CookieBox.CaretIndex = CookieBox.Text.Length;
            }
        }
        catch { }
    }

    // Clear any prior error as soon as the user edits.
    private void Cookie_Changed(object sender, TextChangedEventArgs e) => ClearStatus();

    private void Credentials_Changed(object sender, TextChangedEventArgs e) => ClearStatus();

    private void Password_Changed(object sender, RoutedEventArgs e) => ClearStatus();

    private void ClearStatus()
    {
        if (!_working) StatusBar.Visibility = Visibility.Collapsed;
    }

    // ---------------------------------------------------------------
    //  Submit
    // ---------------------------------------------------------------

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_working) return;

        if (_mode == Mode.Cookie) { await AddByCookieAsync(); return; }
        if (_challenge != null) { await SubmitTwoFactorAsync(); return; }

        await SignInAsync();
    }

    private async Task AddByCookieAsync()
    {
        string cookie = CookieBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(cookie))
        {
            ShowStatus("Paste a cookie first.", isError: true);
            return;
        }

        SetWorking(true);
        ShowStatus("Validating with Roblox…", isError: false, spinner: true);
        await StoreCookieAsync(cookie);
    }

    private async Task SignInAsync()
    {
        string user = UserBox.Text.Trim();
        string pass = PassBox.Password;

        if (user.Length == 0) { ShowStatus("Enter the Roblox username.", isError: true); UserBox.Focus(); return; }
        if (pass.Length == 0) { ShowStatus("Enter the password.", isError: true); PassBox.Focus(); return; }

        SetWorking(true);
        ShowStatus("Signing in to Roblox…", isError: false, spinner: true);

        RobloxAuthService.LoginResult result;
        try { result = await RobloxAuthService.LoginAsync(user, pass); }
        catch (Exception ex) { SetWorking(false); ShowStatus($"Sign-in failed: {ex.Message}", isError: true); return; }

        await HandleLoginResultAsync(result);
    }

    private async Task SubmitTwoFactorAsync()
    {
        var challenge = _challenge;
        if (challenge == null) return;

        string code = CodeBox.Text.Trim();
        if (code.Length == 0) { ShowStatus("Enter the verification code.", isError: true); CodeBox.Focus(); return; }

        SetWorking(true);
        ShowStatus("Checking the code…", isError: false, spinner: true);

        RobloxAuthService.LoginResult result;
        try { result = await RobloxAuthService.CompleteTwoStepAsync(UserBox.Text.Trim(), PassBox.Password, challenge, code); }
        catch (Exception ex) { SetWorking(false); ShowStatus($"Verification failed: {ex.Message}", isError: true); return; }

        await HandleLoginResultAsync(result);
    }

    private async Task HandleLoginResultAsync(RobloxAuthService.LoginResult result)
    {
        // A two-step prompt is news the first time and a rejected code the second time; the
        // status line should not colour both the same.
        bool codeWasRejected = _challenge != null;

        switch (result.Outcome)
        {
            case RobloxAuthService.LoginOutcome.Success when result.Cookie != null:
                ShowStatus("Signed in — adding the account…", isError: false, spinner: true);
                await StoreCookieAsync(result.Cookie);
                return;

            case RobloxAuthService.LoginOutcome.TwoStepRequired when result.TwoStep != null:
                _challenge = result.TwoStep;
                TwoFactorPanel.Visibility = Visibility.Visible;
                TwoFactorLabel.Text = $"Verification code from {result.TwoStep.SourceText}";
                SetWorking(false);
                ShowStatus(result.Message, isError: codeWasRejected);
                CodeBox.Focus();
                CodeBox.SelectAll();
                return;

            case RobloxAuthService.LoginOutcome.ChallengeRequired:
                ClearTwoFactor();
                SetWorking(false);
                ShowStatus(result.Message, isError: true);
                return;

            default:
                // A rejected code leaves the challenge in place so the next code can be tried.
                if (result.Outcome != RobloxAuthService.LoginOutcome.TwoStepRequired) ClearTwoFactor();
                SetWorking(false);
                UpdateActionButton();
                ShowStatus(result.Message, isError: true);
                return;
        }
    }

    /// <summary>Validates the cookie, stores the account and closes on success.</summary>
    private async Task StoreCookieAsync(string cookie)
    {
        var result = await _store.AddByCookieAsync(cookie);

        if (result.Account != null)
        {
            Added = result.Account;
            ShowStatus(result.Message, isError: false, success: true);
            await Task.Delay(650);
            DialogResult = true;
            Close();
            return;
        }

        SetWorking(false);
        UpdateActionButton();
        ShowStatus(result.Message, isError: true);
    }

    // ---------------------------------------------------------------
    //  Browser sign-in
    // ---------------------------------------------------------------

    private async void BrowserLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_working) return;

        if (!ChromiumService.IsInstalled)
        {
            // The private browser build is a one-time download; without it there is no window
            // to sign in through.
            if (!DialogService.ShowChromiumDownload(this) || !ChromiumService.IsInstalled)
            {
                ShowStatus("The sign-in browser is not installed, so the browser sign-in cannot start.", isError: true);
                return;
            }
        }

        ClearTwoFactor();
        SetWorking(true);
        CancelBtn.IsEnabled = true;      // Cancel aborts the capture instead of closing the dialog
        CancelBtn.Content = "Stop";
        ShowStatus("Opening the Roblox sign-in page…", isError: false, spinner: true);

        _browserCts = new CancellationTokenSource();
        var progress = new Progress<string>(text => ShowStatus(text, isError: false, spinner: true));

        BrowserService.LoginCapture capture;
        try { capture = await BrowserService.CaptureLoginCookieAsync(progress, _browserCts.Token); }
        catch (Exception ex) { capture = new BrowserService.LoginCapture(false, $"Browser sign-in failed: {ex.Message}", null); }
        finally
        {
            _browserCts?.Dispose();
            _browserCts = null;
            CancelBtn.Content = "Cancel";
        }

        if (capture.Success && capture.Cookie != null)
        {
            ShowStatus("Signed in — adding the account…", isError: false, spinner: true);
            await StoreCookieAsync(capture.Cookie);
            return;
        }

        SetWorking(false);
        UpdateActionButton();
        ShowStatus(capture.Message == "no-chromium"
            ? "The sign-in browser is not installed, so the browser sign-in cannot start."
            : capture.Message, isError: true);
    }

    private void ClearTwoFactor()
    {
        _challenge = null;
        CodeBox.Text = "";
        TwoFactorPanel.Visibility = Visibility.Collapsed;
    }

    // ---------------------------------------------------------------
    //  Chrome
    // ---------------------------------------------------------------

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // While a browser sign-in is open, Cancel means "stop waiting", not "close the dialog".
        if (_browserCts != null)
        {
            try { _browserCts.Cancel(); } catch { }
            return;
        }

        if (_working) return;
        DialogResult = false;
        Close();
    }

    private void SetWorking(bool working)
    {
        _working = working;
        AddBtn.IsEnabled = !working;
        CancelBtn.IsEnabled = !working;
        SignInTab.IsEnabled = !working;
        CookieTab.IsEnabled = !working;
        UserBox.IsEnabled = !working;
        PassBox.IsEnabled = !working;
        CodeBox.IsEnabled = !working;
        CookieBox.IsEnabled = !working;
        PasteBtn.IsEnabled = !working;
        BrowserBtn.IsEnabled = !working;

        if (working)
            AddBtn.Content = _mode == Mode.Cookie ? "Adding…" : _challenge != null ? "Verifying…" : "Signing in…";
        else
            UpdateActionButton();
    }

    private void ShowStatus(string text, bool isError, bool success = false, bool spinner = false)
    {
        StatusBar.Visibility = Visibility.Visible;
        StatusText.Text = text;

        Color accent;
        if (success) accent = (Color)ColorConverter.ConvertFromString("#3FB950");
        else if (isError) accent = (Color)ColorConverter.ConvertFromString("#F85149");
        else accent = (Color)ColorConverter.ConvertFromString("#7B61FF");

        StatusText.Foreground = new SolidColorBrush(accent);
        StatusIcon.Stroke = new SolidColorBrush(accent);
        StatusBar.Background = new SolidColorBrush(Color.FromArgb(28, accent.R, accent.G, accent.B));

        string key = success ? "Icon.Check" : isError ? "Icon.Close" : "Icon.Refresh";
        StatusIcon.Data = (Geometry)FindResource(key);
    }
}
