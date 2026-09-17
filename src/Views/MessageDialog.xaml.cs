using System.Windows;
using System.Windows.Input;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.Views;

public partial class MessageDialog : Window
{
    public enum Kind { Confirm, Text, Multiline, Password, NewPassword }

    private readonly Kind _kind;

    public string ResultText { get; private set; } = "";

    /// <summary>Minimum length for <see cref="Kind.NewPassword"/>; OK stays disabled until it is met.</summary>
    public int MinLength { get; init; } = 1;

    public MessageDialog(Kind kind, string title, string message, string initial = "",
        string okText = "OK", bool showCancel = true, string cancelText = "Cancel", bool danger = false)
    {
        InitializeComponent();
        _kind = kind;
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        OkBtn.Content = okText;
        CancelBtn.Content = cancelText;
        CancelBtn.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        if (danger) OkBtn.Style = (Style)FindResource("Btn.DangerSolid");

        switch (kind)
        {
            case Kind.Confirm:
                InputHost.Visibility = Visibility.Collapsed;
                // A destructive confirmation must not fire on a reflexive Enter.
                if (danger) OkBtn.IsDefault = false;
                Loaded += (_, _) => (danger ? CancelBtn : OkBtn).Focus();
                break;
            case Kind.Text:
                Input.Text = initial;
                Loaded += (_, _) => { Input.Focus(); Input.SelectAll(); };
                break;
            case Kind.Multiline:
                Input.Style = (Style)FindResource("Input.Code");
                Input.Text = initial;
                Input.Height = 170;       // fixed height with internal scroll, so a huge paste never grows the window
                Input.TextWrapping = TextWrapping.NoWrap;
                OkBtn.IsDefault = false;  // Enter inserts a line break here
                Loaded += (_, _) => Input.Focus();
                break;
            case Kind.Password:
                Input.Visibility = Visibility.Collapsed;
                PasswordHost.Visibility = Visibility.Visible;
                SetPlaceholder(Password, L.T("Dialog.Password"));
                Loaded += (_, _) => Password.Focus();
                break;
            case Kind.NewPassword:
                Input.Visibility = Visibility.Collapsed;
                PasswordHost.Visibility = Visibility.Visible;
                PasswordRepeat.Visibility = Visibility.Visible;
                PasswordHint.Visibility = Visibility.Visible;
                SetPlaceholder(Password, L.T("Dialog.NewPassword"));
                SetPlaceholder(PasswordRepeat, L.T("Dialog.RepeatPassword"));
                Loaded += (_, _) => { UpdatePasswordState(); Password.Focus(); };
                break;
        }

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        };
    }

    private static void SetPlaceholder(DependencyObject box, string text) => Controls.Ui.SetPlaceholder(box, text);

    private void Password_Changed(object sender, RoutedEventArgs e)
    {
        if (_kind == Kind.NewPassword) UpdatePasswordState();
    }

    private void UpdatePasswordState()
    {
        string a = Password.Password, b = PasswordRepeat.Password;
        bool longEnough = a.Length >= MinLength;
        bool match = a == b;

        OkBtn.IsEnabled = longEnough && match;
        if (!longEnough)
        {
            PasswordHint.Text = L.T("Dialog.PasswordMin", MinLength);
            PasswordHint.SetResourceReference(ForegroundProperty, "TextMutedBrush");
        }
        else if (b.Length > 0 && !match)
        {
            PasswordHint.Text = L.T("Dialog.PasswordMismatch");
            PasswordHint.SetResourceReference(ForegroundProperty, "DangerBrush");
        }
        else if (!match)
        {
            PasswordHint.Text = L.T("Dialog.PasswordRepeat");
            PasswordHint.SetResourceReference(ForegroundProperty, "TextMutedBrush");
        }
        else
        {
            PasswordHint.Text = L.T("Dialog.PasswordReady");
            PasswordHint.SetResourceReference(ForegroundProperty, "SuccessBrush");
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!OkBtn.IsEnabled) return;
        ResultText = _kind is Kind.Password or Kind.NewPassword ? Password.Password : Input.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && fe.TemplatedParent == null && e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }
}
