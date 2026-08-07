using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RhinoWorktreeLauncher;

public sealed class TrackingTextBlock : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(TrackingTextBlock),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.AffectsMeasure |
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackingProperty = DependencyProperty.Register(
        nameof(Tracking),
        typeof(double),
        typeof(TrackingTextBlock),
        new FrameworkPropertyMetadata(
            0d,
            FrameworkPropertyMetadataOptions.AffectsMeasure |
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
        nameof(TextAlignment),
        typeof(TextAlignment),
        typeof(TrackingTextBlock),
        new FrameworkPropertyMetadata(
            TextAlignment.Left,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineHeightProperty = DependencyProperty.Register(
        nameof(LineHeight),
        typeof(double),
        typeof(TrackingTextBlock),
        new FrameworkPropertyMetadata(
            double.NaN,
            FrameworkPropertyMetadataOptions.AffectsMeasure |
            FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double Tracking
    {
        get => (double)GetValue(TrackingProperty);
        set => SetValue(TrackingProperty, value);
    }

    public TextAlignment TextAlignment
    {
        get => (TextAlignment)GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public double LineHeight
    {
        get => (double)GetValue(LineHeightProperty);
        set => SetValue(LineHeightProperty, value);
    }

    public TrackingTextBlock()
    {
        Focusable = false;
        IsHitTestVisible = false;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        (double width, double naturalHeight) = MeasureText();
        double height = double.IsNaN(LineHeight) ? naturalHeight : LineHeight;
        return new Size(
            Math.Min(width, constraint.Width),
            Math.Min(height, constraint.Height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (string.IsNullOrEmpty(Text))
            return;

        (double width, double naturalHeight) = MeasureText();
        double x = TextAlignment switch
        {
            TextAlignment.Center => Math.Max(0, (ActualWidth - width) / 2),
            TextAlignment.Right => Math.Max(0, ActualWidth - width),
            _ => 0
        };
        double y = (ActualHeight - naturalHeight) / 2;
        double spacing = Tracking * FontSize;
        foreach (char character in Text)
        {
            FormattedText glyph = CreateText(character.ToString());
            drawingContext.DrawText(glyph, new Point(x, y));
            x += glyph.WidthIncludingTrailingWhitespace + spacing;
        }
    }

    private (double Width, double Height) MeasureText()
    {
        if (string.IsNullOrEmpty(Text))
            return (0, 0);

        double width = 0;
        double height = 0;
        foreach (char character in Text)
        {
            FormattedText glyph = CreateText(character.ToString());
            width += glyph.WidthIncludingTrailingWhitespace;
            height = Math.Max(height, glyph.Height);
        }
        width += Math.Max(0, Text.Length - 1) * Tracking * FontSize;
        return (Math.Max(0, width), height);
    }

    private FormattedText CreateText(string text) => new FormattedText(
        text,
        CultureInfo.CurrentUICulture,
        FlowDirection,
        new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
        FontSize,
        Foreground,
        null,
        TextOptions.GetTextFormattingMode(this),
        VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
