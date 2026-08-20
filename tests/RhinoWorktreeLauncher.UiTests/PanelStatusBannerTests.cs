using System.IO;
using System.Reflection;
using System.Windows;
using System.Xml.Linq;

namespace RhinoWorktreeLauncher.UiTests;

/// <summary>
/// The status report says variable-length things at unpredictable moments, so it cannot
/// share a line with three permanent column captions. It reads at the bottom of the panel,
/// over the list, and it is absent whenever there is nothing to report.
/// </summary>
public sealed class PanelStatusBannerTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void The_status_report_left_the_panel_header()
    {
        XDocument document = LoadMainWindow();
        XElement header = Named(document, "WorktreeCountText").Parent!;

        Assert.DoesNotContain(
            header.Descendants(),
            element => string.Equals(element.Attribute(Xaml + "Name")?.Value, "PanelHintText", StringComparison.Ordinal));
    }

    [Fact]
    public void The_status_report_reads_inside_a_banner()
    {
        XDocument document = LoadMainWindow();

        Assert.Contains(
            Named(document, "PanelHintText").Ancestors(),
            element => string.Equals(element.Attribute(Xaml + "Name")?.Value, "PanelHintBanner", StringComparison.Ordinal));
    }

    [Fact]
    public void The_banner_floats_over_the_bottom_of_the_list_rather_than_displacing_it()
    {
        XDocument document = LoadMainWindow();
        XElement banner = Named(document, "PanelHintBanner");

        // Same parent as the list, so it overlays the rows instead of taking a layout row
        // of its own and shortening the list every time there is something to say.
        Assert.Same(Named(document, "WorktreeList").Parent, banner.Parent);
        Assert.Equal("Bottom", banner.Attribute("VerticalAlignment")?.Value);
    }

    [Fact]
    public void The_banner_is_hidden_until_there_is_something_to_report()
    {
        XDocument document = LoadMainWindow();

        Assert.Equal("Collapsed", Named(document, "PanelHintBanner").Attribute("Visibility")?.Value);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Selected plug-in verified in Rhino", true)]
    [InlineData("Local data shown; remote enrichment unavailable", true)]
    public void A_report_with_nothing_to_say_leaves_no_banner_over_the_list(string hint, bool shown)
    {
        MethodInfo visibility = typeof(MainWindow).GetMethod(
            "HintVisibility",
            BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("MainWindow method 'HintVisibility' was not found.");

        Assert.Equal(
            shown ? Visibility.Visible : Visibility.Collapsed,
            visibility.Invoke(null, new object?[] { hint }));
    }

    private static XElement Named(XDocument document, string name) => document
        .Descendants()
        .FirstOrDefault(element => string.Equals(
            element.Attribute(Xaml + "Name")?.Value,
            name,
            StringComparison.Ordinal)) ??
        throw new InvalidOperationException($"XAML element '{name}' was not found.");

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
