using System.Globalization;
using System.IO;
using System.Windows;
using System.Xml.Linq;
using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.UiTests;

/// <summary>
/// Three surfaces show a repository path under a heading, and each drew it differently.
/// The main window's lead-in bar is the one treatment, so its element is read for the
/// measurements rather than the numbers being copied into the dialogs and into this test,
/// where they could agree today and drift apart on the next edit.
/// </summary>
public sealed class PathRailTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly Size Client = new Size(574, 671);

    [Fact]
    public void The_add_project_directory_line_carries_the_main_windows_rail()
    {
        AssertRailMatchesMainWindow(SurfaceLayout.Arrange(
            () => new AddProjectDialog(@"C:\repos\ExampleTool", BuildOptions()),
            Client));
    }

    [Fact]
    public void The_project_config_directory_line_carries_the_main_windows_rail()
    {
        AssertRailMatchesMainWindow(SurfaceLayout.Arrange(
            () => new ProjectConfigDialog(Registration(), _ => throw new NotSupportedException()),
            new Size(704, 781)));
    }

    private static void AssertRailMatchesMainWindow(IReadOnlyDictionary<string, Rect> layout)
    {
        RailSpecification expected = MainWindowRail();
        Rect rail = layout.Part("ProjectPathRail");
        Rect path = layout.Part("ProjectPathText");

        Assert.Equal(expected.Width, rail.Width, precision: 3);
        Assert.Equal(expected.Height, rail.Height, precision: 3);
        Assert.Equal(expected.Gap, path.Left - rail.Right, precision: 3);
        Assert.Equal(path.CentreY(), rail.CentreY(), precision: 3);
    }

    /// <summary>Reads the main window's rail so it stays the single definition.</summary>
    private static RailSpecification MainWindowRail()
    {
        XElement rail = XDocument
            .Load(Path.Combine(SourceDirectory(), "MainWindow.xaml"))
            .Descendants()
            .FirstOrDefault(element => string.Equals(
                element.Attribute(Xaml + "Name")?.Value,
                "RepositoryPathRail",
                StringComparison.Ordinal)) ??
            throw new Xunit.Sdk.XunitException(
                "MainWindow.xaml has no element named 'RepositoryPathRail'.");

        string[] margin = (rail.Attribute("Margin")?.Value ?? string.Empty).Split(',');
        Assert.Equal(4, margin.Length);
        return new RailSpecification(
            Number(rail.Attribute("Width")?.Value),
            Number(rail.Attribute("Height")?.Value),
            Number(margin[2]));
    }

    private static double Number(string? value) => double.Parse(
        value ?? throw new Xunit.Sdk.XunitException("The main window's rail omits a measurement."),
        CultureInfo.InvariantCulture);

    private static ProjectRegistration Registration() => new ProjectRegistration(
        "example",
        "Example Tool",
        @"C:\repos\ExampleTool\.git",
        @"C:\repos\ExampleTool",
        8,
        ProjectAccessGrant.Full,
        BuildProfile.Unconfigured);

    private static ProjectBuildOptions BuildOptions() => new ProjectBuildOptions(new[]
    {
        new PluginBuildOptions(
            "Plugins/Example.csproj",
            new[]
            {
                new SolutionBuildOptions(
                    "Example.sln",
                    new[] { new BuildConfiguration("Debug", "AnyCPU") })
            })
    });

    private static string SourceDirectory()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RhinoWorktreeLauncher.slnx")))
            directory = directory.Parent;
        if (directory is null)
            throw new DirectoryNotFoundException("Repository root was not found from the UI test output directory.");
        return Path.Combine(directory.FullName, "src", "RhinoWorktreeLauncher");
    }

    private sealed record RailSpecification(double Width, double Height, double Gap);
}
