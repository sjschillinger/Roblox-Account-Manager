using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RobloxAccountManager.Controls;

/// <summary>
/// Attached properties the control templates read. Keeps one-off needs — a placeholder, a leading
/// icon, a corner radius — out of a dozen near-identical styles.
/// </summary>
public static class Ui
{
    /// <summary>Hint text shown inside an empty TextBox / PasswordBox / ComboBox.</summary>
    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.RegisterAttached("Placeholder", typeof(string), typeof(Ui),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.Inherits));

    public static string GetPlaceholder(DependencyObject d) => (string)d.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject d, string v) => d.SetValue(PlaceholderProperty, v);

    /// <summary>Leading icon for inputs and buttons that support one.</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached("Icon", typeof(Geometry), typeof(Ui), new PropertyMetadata(null));

    public static Geometry? GetIcon(DependencyObject d) => (Geometry?)d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, Geometry? v) => d.SetValue(IconProperty, v);

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.RegisterAttached("CornerRadius", typeof(CornerRadius), typeof(Ui),
            new PropertyMetadata(new CornerRadius(6)));

    public static CornerRadius GetCornerRadius(DependencyObject d) => (CornerRadius)d.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject d, CornerRadius v) => d.SetValue(CornerRadiusProperty, v);

    /// <summary>Small count / hint shown at the trailing edge of nav items and tabs.</summary>
    public static readonly DependencyProperty BadgeProperty =
        DependencyProperty.RegisterAttached("Badge", typeof(object), typeof(Ui), new PropertyMetadata(null));

    public static object? GetBadge(DependencyObject d) => d.GetValue(BadgeProperty);
    public static void SetBadge(DependencyObject d, object? v) => d.SetValue(BadgeProperty, v);

    /// <summary>
    /// PasswordBox.Password is not a dependency property, so a template cannot tell whether the box
    /// is empty. This mirrors it as a bool the placeholder trigger can use.
    /// </summary>
    public static readonly DependencyProperty HasTextProperty =
        DependencyProperty.RegisterAttached("HasText", typeof(bool), typeof(Ui), new PropertyMetadata(false));

    public static bool GetHasText(DependencyObject d) => (bool)d.GetValue(HasTextProperty);
    public static void SetHasText(DependencyObject d, bool v) => d.SetValue(HasTextProperty, v);

    public static readonly DependencyProperty TrackPasswordProperty =
        DependencyProperty.RegisterAttached("TrackPassword", typeof(bool), typeof(Ui),
            new PropertyMetadata(false, OnTrackPasswordChanged));

    public static bool GetTrackPassword(DependencyObject d) => (bool)d.GetValue(TrackPasswordProperty);
    public static void SetTrackPassword(DependencyObject d, bool v) => d.SetValue(TrackPasswordProperty, v);

    private static void OnTrackPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        box.PasswordChanged -= OnPasswordChanged;
        if ((bool)e.NewValue) box.PasswordChanged += OnPasswordChanged;
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box) SetHasText(box, box.SecurePassword.Length > 0);
    }
}
