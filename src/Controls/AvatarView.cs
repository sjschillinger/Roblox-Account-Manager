using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RobloxAccountManager.Controls;

/// <summary>
/// Account / friend avatar: the Roblox headshot when there is one, otherwise the initial on a
/// colour derived from the name (stable across runs, so an account is recognisable before its
/// thumbnail loads), with an optional presence pip in the corner.
/// </summary>
public class AvatarView : Control
{
    private static readonly Color[] Hues =
    {
        Color.FromRgb(0x5B, 0x8D, 0xEF), Color.FromRgb(0x2F, 0xB3, 0x80), Color.FromRgb(0xE0, 0x8A, 0x3C),
        Color.FromRgb(0xC0, 0x62, 0xD9), Color.FromRgb(0xE0, 0x5C, 0x6E), Color.FromRgb(0x3F, 0xB0, 0xC4),
        Color.FromRgb(0x94, 0x9A, 0x3A), Color.FromRgb(0x7C, 0x7F, 0xE8),
    };

    public static readonly DependencyProperty ImageUrlProperty =
        DependencyProperty.Register(nameof(ImageUrl), typeof(string), typeof(AvatarView), new PropertyMetadata(null));

    public string? ImageUrl { get => (string?)GetValue(ImageUrlProperty); set => SetValue(ImageUrlProperty, value); }

    public static readonly DependencyProperty NameTextProperty =
        DependencyProperty.Register(nameof(NameText), typeof(string), typeof(AvatarView),
            new PropertyMetadata("", OnNameChanged));

    /// <summary>Name the initial and the fallback colour are derived from.</summary>
    public string NameText { get => (string)GetValue(NameTextProperty); set => SetValue(NameTextProperty, value); }

    private static readonly DependencyPropertyKey InitialPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(Initial), typeof(string), typeof(AvatarView), new PropertyMetadata("?"));

    public static readonly DependencyProperty InitialProperty = InitialPropertyKey.DependencyProperty;
    public string Initial => (string)GetValue(InitialProperty);

    private static readonly DependencyPropertyKey TintPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(Tint), typeof(Brush), typeof(AvatarView), new PropertyMetadata(Brushes.Gray));

    public static readonly DependencyProperty TintProperty = TintPropertyKey.DependencyProperty;
    public Brush Tint => (Brush)GetValue(TintProperty);

    private static readonly DependencyPropertyKey TintSoftPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(TintSoft), typeof(Brush), typeof(AvatarView), new PropertyMetadata(Brushes.Transparent));

    public static readonly DependencyProperty TintSoftProperty = TintSoftPropertyKey.DependencyProperty;
    public Brush TintSoft => (Brush)GetValue(TintSoftProperty);

    public static readonly DependencyProperty PresenceProperty =
        DependencyProperty.Register(nameof(Presence), typeof(string), typeof(AvatarView), new PropertyMetadata(null));

    /// <summary>Canonical presence ("Online", "In Game", "In Studio", "Offline"); null hides the pip.</summary>
    public string? Presence { get => (string?)GetValue(PresenceProperty); set => SetValue(PresenceProperty, value); }

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(AvatarView), new PropertyMetadata(new CornerRadius(10)));

    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    public static readonly DependencyProperty PipSizeProperty =
        DependencyProperty.Register(nameof(PipSize), typeof(double), typeof(AvatarView), new PropertyMetadata(10d));

    public double PipSize { get => (double)GetValue(PipSizeProperty); set => SetValue(PipSizeProperty, value); }

    public static readonly DependencyProperty RingBrushProperty =
        DependencyProperty.Register(nameof(RingBrush), typeof(Brush), typeof(AvatarView), new PropertyMetadata(Brushes.Black));

    /// <summary>Colour of the cut-out ring around the pip — should match whatever the avatar sits on.</summary>
    public Brush RingBrush { get => (Brush)GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }

    /// <summary>Scales the initial with the avatar size.</summary>
    public static readonly System.Windows.Data.IValueConverter FontSizeConverter = new InitialSizeConverter();

    private sealed class InitialSizeConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c)
            => value is double h && h > 0 ? Math.Max(9, h * 0.42) : 13d;
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c)
            => System.Windows.Data.Binding.DoNothing;
    }

    private static void OnNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (AvatarView)d;
        string name = (e.NewValue as string ?? "").Trim().TrimStart('@');
        view.SetValue(InitialPropertyKey, name.Length == 0 ? "?" : char.ToUpperInvariant(name[0]).ToString());

        // Stable hash (string.GetHashCode is randomised per process).
        uint h = 2166136261;
        foreach (char c in name.ToLowerInvariant()) { h ^= c; h *= 16777619; }
        var hue = Hues[h % Hues.Length];

        var tint = new SolidColorBrush(hue);
        tint.Freeze();
        var soft = new SolidColorBrush(Color.FromArgb(0x38, hue.R, hue.G, hue.B));
        soft.Freeze();
        view.SetValue(TintPropertyKey, tint);
        view.SetValue(TintSoftPropertyKey, soft);
    }
}
