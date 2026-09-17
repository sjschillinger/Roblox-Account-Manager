using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RobloxAccountManager.Models;
using RobloxAccountManager.Services;

namespace RobloxAccountManager.Mvvm;

public class BoolToVis : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => value is Visibility vis && vis == Visibility.Visible ? !Invert : Invert;
}

public class NullToVis : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool has = value is string s ? !string.IsNullOrEmpty(s) : value != null;
        if (Invert) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class InverseBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b && !b;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is bool b && !b;
}

public class HexToBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        try
        {
            if (value is string s && s.Length > 0)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s));
                brush.Freeze();
                return brush;
            }
        }
        catch { }
        return Brushes.Transparent;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class CountToVis : IValueConverter
{
    /// <summary>Visible when count == 0 (empty states) — or when count &gt; 0 with WhenZero=false.</summary>
    public bool WhenZero { get; set; } = true;
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        long n = value switch { int i => i, long l => l, _ => 0 };
        bool show = WhenZero ? n == 0 : n > 0;
        return show ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class UrlToImage : IValueConverter
{
    // Decode small (avatars and icons render tiny) and cache per URL, so scrolling and re-templating
    // never re-download or re-decode.
    private static readonly Dictionary<string, BitmapImage> Cache = new();
    private const int DecodeWidth = 160;

    public object? Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is not string url || string.IsNullOrEmpty(url)) return null;
        // Only ever load images over HTTPS from Roblox's CDNs — a malformed or hostile value in a
        // settings file must not turn into an arbitrary file:// or UNC read.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        if (Cache.TryGetValue(url, out var cached)) return cached;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = DecodeWidth;
            bmp.UriSource = uri;
            bmp.EndInit();
            if (bmp.CanFreeze) bmp.Freeze();
            if (Cache.Count > 512) Cache.Clear();
            Cache[url] = bmp;
            return bmp;
        }
        catch { return null; }
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Resolves a resource key string (e.g. "Icon.Accounts") to its Geometry resource.</summary>
public class IconKeyConverter : IValueConverter
{
    public object? Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is string key && Application.Current.TryFindResource(key) is Geometry g) return g;
        return null;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Visible when the bound value's text equals the ConverterParameter (Invert flips it).</summary>
public class EqualsToVis : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool eq = string.Equals(value?.ToString(), p?.ToString(), StringComparison.OrdinalIgnoreCase);
        if (Invert) eq = !eq;
        return eq ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Two-way bridge for radio-style choices: IsChecked is true when value == parameter,
/// and checking it writes the parameter back.</summary>
public class EqualsToBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => string.Equals(value?.ToString(), p?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
    {
        if (value is not true || p == null) return Binding.DoNothing;
        if (t == typeof(int) && int.TryParse(p.ToString(), out int i)) return i;
        return p.ToString()!;
    }
}

/// <summary>MultiBinding [string text, bool mask] -> bullets when mask is true.</summary>
public class MaskConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object p, CultureInfo c)
    {
        string text = values.Length > 0 && values[0] is string s ? s : "";
        bool mask = values.Length > 1 && values[1] is bool b && b;
        if (mask && !string.IsNullOrEmpty(text)) return new string('•', Math.Min(10, Math.Max(6, text.Length)));
        return text;
    }
    public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => Array.Empty<object>();
}

/// <summary>Counts accounts in a group that are online in any form; parameter "vis" drives a badge's Visibility.</summary>
public class GroupOnlineCount : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        int n = 0;
        if (value is System.Collections.IEnumerable items)
            foreach (var o in items)
                if (o is Account a && PresenceStatus.IsOnline(a.Presence)) n++;
        if (p is string s && s == "vis")
            return n > 0 ? Visibility.Visible : Visibility.Collapsed;
        return n;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Multiplies a 0..1 fill value by the bar width passed as parameter.</summary>
public class FillToWidth : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        double fill = value is double d ? d : 0;
        double max = p != null && double.TryParse(p.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var m) ? m : 60;
        return Math.Clamp(fill, 0, 1) * max;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Canonical presence → theme brush (the brush recolours itself on a theme switch).</summary>
public class PresenceToBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => ThemeService.BrushFor(PresenceStatus.PaletteKey(value as string));
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Canonical presence → localized label.</summary>
public class PresenceToText : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => PresenceStatus.Label(value as string);
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Localizes a value by prefixing it: ConverterParameter "Mode." + "Dark" → L.T("Mode.Dark").</summary>
public class LocalizeKey : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        string key = (p as string ?? "") + (value?.ToString() ?? "");
        return L.T(key);
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Palette key name → live brush ("Success" → SuccessBrush).</summary>
public class KeyToBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => ThemeService.BrushFor(value as string ?? "TextMuted");
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Greater-than-zero test for numbers (badges, counters).</summary>
public class PositiveToVis : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        double n = value switch { int i => i, long l => l, double d => d, _ => 0 };
        return n > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
