using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RhinoWorktreeLauncher;

/// <summary>
/// Draws the restrained inner top highlight used by raised controls and chips.
/// </summary>
public sealed class InsetHighlightBorder : Border
{
    public static readonly DependencyProperty HighlightBrushProperty =
        DependencyProperty.Register(
            nameof(HighlightBrush),
            typeof(Brush),
            typeof(InsetHighlightBorder),
            new FrameworkPropertyMetadata(
                Brushes.Transparent,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush HighlightBrush
    {
        get => (Brush)GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (HighlightBrush is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        const double thickness = 0.67;
        double y = BorderThickness.Top + thickness / 2;
        double start = Math.Max(
            BorderThickness.Left,
            Math.Max(CornerRadius.TopLeft, CornerRadius.BottomLeft));
        double end = ActualWidth - Math.Max(
            BorderThickness.Right,
            Math.Max(CornerRadius.TopRight, CornerRadius.BottomRight));
        if (end <= start)
            return;

        drawingContext.DrawLine(
            new Pen(HighlightBrush, thickness),
            new Point(start, y),
            new Point(end, y));
    }
}
