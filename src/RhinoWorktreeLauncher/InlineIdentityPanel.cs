using System.Windows;
using System.Windows.Controls;

namespace RhinoWorktreeLauncher;

/// <summary>
/// Keeps worktree badges immediately after the name while reserving their full width.
/// The first child is the only element allowed to contract and trim.
/// </summary>
public sealed class InlineIdentityPanel : Panel
{
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap),
        typeof(double),
        typeof(InlineIdentityPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count == 0)
            return new Size();

        double badgesWidth = 0;
        double height = 0;
        int visibleBadgeCount = 0;

        for (int index = 1; index < InternalChildren.Count; index++)
        {
            UIElement badge = InternalChildren[index];
            badge.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            if (badge.Visibility == Visibility.Collapsed)
                continue;

            badgesWidth += badge.DesiredSize.Width;
            height = Math.Max(height, badge.DesiredSize.Height);
            visibleBadgeCount++;
        }

        double gapsWidth = visibleBadgeCount * Gap;
        double nameWidth = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0, availableSize.Width - badgesWidth - gapsWidth);

        UIElement name = InternalChildren[0];
        name.Measure(new Size(nameWidth, availableSize.Height));
        height = Math.Max(height, name.DesiredSize.Height);

        double desiredWidth = name.DesiredSize.Width + badgesWidth + gapsWidth;
        return new Size(
            double.IsInfinity(availableSize.Width) ? desiredWidth : Math.Min(desiredWidth, availableSize.Width),
            height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double badgesWidth = 0;
        int visibleBadgeCount = 0;

        for (int index = 1; index < InternalChildren.Count; index++)
        {
            UIElement badge = InternalChildren[index];
            if (badge.Visibility == Visibility.Collapsed)
                continue;

            badgesWidth += badge.DesiredSize.Width;
            visibleBadgeCount++;
        }

        double nameWidth = Math.Min(
            InternalChildren[0].DesiredSize.Width,
            Math.Max(0, finalSize.Width - badgesWidth - visibleBadgeCount * Gap));
        InternalChildren[0].Arrange(new Rect(0, 0, nameWidth, finalSize.Height));

        double x = nameWidth;
        for (int index = 1; index < InternalChildren.Count; index++)
        {
            UIElement badge = InternalChildren[index];
            if (badge.Visibility == Visibility.Collapsed)
                continue;

            x += Gap;
            badge.Arrange(new Rect(x, 0, badge.DesiredSize.Width, finalSize.Height));
            x += badge.DesiredSize.Width;
        }

        return finalSize;
    }
}
