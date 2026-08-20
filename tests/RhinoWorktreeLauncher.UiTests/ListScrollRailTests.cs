using System.IO;
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
        XElement viewer = Keyed(document, "GutteredScrollViewer");

        Assert.Contains(
            viewer.Descendants(Presentation + "ScrollBar"),
            bar => string.Equals(bar.Attribute(Xaml + "Name")?.Value, "PART_VerticalScrollBar", StringComparison.Ordinal));
    }

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
