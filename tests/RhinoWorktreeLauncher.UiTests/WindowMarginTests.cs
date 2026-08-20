using System.IO;
using System.Xml.Linq;

namespace RhinoWorktreeLauncher.UiTests;

/// <summary>
/// The surface is a fixed 720 x 1000 window, so the distance from its content to its
/// frame is a constant of the design rather than something each region chooses.
/// </summary>
public sealed class WindowMarginTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void The_surface_keeps_one_margin_to_every_window_edge()
    {
        XDocument document = LoadMainWindow();
        string[] content = Thicknesses(Named(document, "ContentArea"), "Margin");
        string[] footer = Thicknesses(Named(document, "FooterBar"), "Padding");
        string edge = content[0];

        // Left, top and right come from the content area; the footer is the last row, so
        // the bottom of the window is its own. Its top is an internal seam, not an edge.
        Assert.Equal(edge, content[1]);
        Assert.Equal(edge, content[2]);
        Assert.Equal(edge, footer[0]);
        Assert.Equal(edge, footer[2]);
        Assert.Equal(edge, footer[3]);
    }

    [Fact]
    public void The_window_edge_inset_is_stated_once_per_region()
    {
        XDocument document = LoadMainWindow();

        // The identity block used to carry the top inset itself, which is how the bottom
        // came to differ from it without anything reading as wrong.
        Assert.DoesNotContain(
            Named(document, "ContentArea").Elements(),
            element => (element.Attribute("Margin")?.Value ?? string.Empty).Split(',') is { Length: 4 } parts &&
                parts[1] != "0");
    }

    private static string[] Thicknesses(XElement element, string property)
    {
        string[] parts = (element.Attribute(property)?.Value ?? string.Empty).Split(',');
        return parts.Length == 4
            ? parts
            : throw new InvalidOperationException(
                $"'{element.Attribute(Xaml + "Name")?.Value}' does not state {property} as four sides.");
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
