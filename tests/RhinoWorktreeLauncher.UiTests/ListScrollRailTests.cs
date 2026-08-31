using System.IO;
using System.Windows;
using System.Xml.Linq;

namespace RhinoWorktreeLauncher.UiTests;

/// <summary>
/// The gutter beside the worktree list carries the scrollbar and nothing else. A rail
/// drawn precisely when there is nothing to scroll offers a control that cannot be used.
/// </summary>
public sealed class ListScrollRailTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void The_gutter_draws_nothing_when_there_is_nothing_to_scroll()
    {
        XDocument document = LoadMainWindow();

        // Named in an element, and again in the trigger that showed it, so the search
        // covers both rather than leaving a setter pointing at a target that is gone.
        Assert.DoesNotContain(
            document.Descendants().SelectMany(element => element.Attributes()),
            attribute => attribute.Value.Contains("InactiveScrollIndicator", StringComparison.Ordinal));
    }

    [Fact]
    public void The_scrollbar_a_reader_can_actually_drag_stays()
    {
        XDocument document = LoadMainWindow();
        XElement viewer = Keyed(document, "OverlayScrollViewer");

        Assert.Contains(
            viewer.Descendants(Presentation + "ScrollBar"),
            bar => string.Equals(bar.Attribute(Xaml + "Name")?.Value, "PART_VerticalScrollBar", StringComparison.Ordinal));
    }

    [Fact]
    public void The_rows_run_the_full_width_of_the_list()
    {
        XDocument document = LoadMainWindow();
        XElement viewer = Keyed(document, "OverlayScrollViewer");

        // No reserved column at all. A gutter that appears with the scrollbar would move
        // every row 12px sideways on the one occasion the list is long enough to scroll,
        // and the column captions above the rows cannot follow it.
        Assert.Empty(viewer.Descendants(Presentation + "ColumnDefinition"));
        Assert.Null(Named(document, "PART_ScrollContentPresenter").Attribute("Grid.Column"));
    }

    [Fact]
    public void A_scrollbar_that_appears_floats_over_the_rows_own_padding()
    {
        XDocument document = LoadMainWindow();
        XElement bar = Named(document, "PART_VerticalScrollBar");

        Assert.Null(bar.Attribute("Grid.Column"));
        Assert.Equal("Right", bar.Attribute("HorizontalAlignment")?.Value);
        // Four pixels over the row padding, where no row content reaches.
        Assert.Equal("4", bar.Attribute("MaxWidth")?.Value);
    }

    // Symmetric panel insets used to be enough, because the row ran to the same edge the
    // header did. The row now reserves a trailing gutter for its own action, so equal insets
    // would put the captions a gutter's width to the right of the bars they name. What has
    // to hold is the alignment itself, so it is read from the arranged surface rather than
    // inferred from two margins.
    [Fact]
    public void The_column_captions_sit_over_the_divergence_bars_they_name()
    {
        IReadOnlyDictionary<string, Rect> layout = WorktreeSurface.Arrange();
        Rect captions = layout.Part("DivergenceCaptions");
        Rect bars = layout.Part("DivergenceBars");

        // Within a pixel: the panel header's inset and the row's padding differ by the
        // panel border, which predates the gutter. A gutter the header did not match would
        // be out by the width of the gutter, not by a pixel.
        Assert.InRange(Math.Abs(captions.Left - bars.Left), 0, 1);
        Assert.InRange(Math.Abs(captions.Right - bars.Right), 0, 1);
    }

    // The scrollbar floats over the row's own padding, which lies outside the content the
    // row template arranges. The row action lives inside that content, so it can never end
    // up under the scrollbar, and this reads both bounds rather than trusting the arithmetic.
    [Fact]
    public void The_row_action_stays_clear_of_the_floating_scrollbar()
    {
        IReadOnlyDictionary<string, Rect> layout = WorktreeSurface.Arrange();
        Rect row = layout.Part("RowBackground");
        Rect action = layout.Part("SetDefaultButton");

        Assert.True(
            row.Right - action.Right >= 4,
            $"The row action reaches {row.Right - action.Right:0.###}px from the row edge, " +
            "inside the 4px the scrollbar floats over.");
    }

    private static XElement Named(XDocument document, string name) => document
        .Descendants()
        .FirstOrDefault(element => string.Equals(
            element.Attribute(Xaml + "Name")?.Value,
            name,
            StringComparison.Ordinal)) ??
        throw new InvalidOperationException($"XAML element '{name}' was not found.");

    private static XElement Keyed(XDocument document, string key) => document
        .Descendants()
        .FirstOrDefault(element => string.Equals(
            element.Attribute(Xaml + "Key")?.Value,
            key,
            StringComparison.Ordinal)) ??
        throw new InvalidOperationException($"XAML resource '{key}' was not found.");

    private static XDocument LoadMainWindow() => XDocument.Load(Path.Combine(
        RepositoryRoot(),
        "src",
        "RhinoWorktreeLauncher",
        "MainWindow.xaml"));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RhinoWorktreeLauncher.slnx")))
            directory = directory.Parent;
        if (directory is null)
            throw new DirectoryNotFoundException("Repository root was not found from the UI test output directory.");
        return directory.FullName;
    }
}
