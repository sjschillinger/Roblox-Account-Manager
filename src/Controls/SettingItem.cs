using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RobloxAccountManager.Controls;

/// <summary>
/// One row of a settings list: a title and a description on the left, the control that changes the
/// setting on the right (or underneath, for wide editors). Every settings page is built from these,
/// which is what keeps the spacing and hierarchy identical from one category to the next.
/// </summary>
public class SettingItem : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingItem), new PropertyMetadata(""));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingItem), new PropertyMetadata(""));

    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    /// <summary>Puts the editor below the text instead of beside it (text areas, button rows, lists).</summary>
    public static readonly DependencyProperty IsStackedProperty =
        DependencyProperty.Register(nameof(IsStacked), typeof(bool), typeof(SettingItem), new PropertyMetadata(false));

    public bool IsStacked { get => (bool)GetValue(IsStackedProperty); set => SetValue(IsStackedProperty, value); }

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(Geometry), typeof(SettingItem), new PropertyMetadata(null));

    public Geometry? Icon { get => (Geometry?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
}

/// <summary>
/// A vertical stack that draws a hairline between consecutive visible children. Settings groups
/// use it so rows are separated without every row having to know whether it is the first or last.
/// </summary>
public class DividerStack : StackPanel
{
    public static readonly DependencyProperty DividerBrushProperty =
        DependencyProperty.Register(nameof(DividerBrush), typeof(Brush), typeof(DividerStack),
            new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush DividerBrush { get => (Brush)GetValue(DividerBrushProperty); set => SetValue(DividerBrushProperty, value); }

    public static readonly DependencyProperty DividerInsetProperty =
        DependencyProperty.Register(nameof(DividerInset), typeof(double), typeof(DividerStack),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Horizontal inset on both ends of each line.</summary>
    public double DividerInset { get => (double)GetValue(DividerInsetProperty); set => SetValue(DividerInsetProperty, value); }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = base.ArrangeOverride(arrangeSize);
        InvalidateVisual();   // child visibility changes move the lines
        return size;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (DividerBrush == null) return;

        bool first = true;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility != Visibility.Visible) continue;
            if (!first)
            {
                // The layout slot includes the child's margin, so the line sits exactly on the seam.
                double y = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot((FrameworkElement)child).Top;
                dc.DrawRectangle(DividerBrush, null,
                    new Rect(DividerInset, Math.Round(y), Math.Max(0, ActualWidth - DividerInset * 2), 1));
            }
            first = false;
        }
    }
}
