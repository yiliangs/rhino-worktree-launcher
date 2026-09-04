using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Xml.Linq;

namespace RhinoWorktreeLauncher.UiTests;

/// <summary>
/// The footer publishes both launch modes rather than one button whose label follows a
/// saved setting. Build &amp; Launch and Launch are different requests, and reaching the
/// other one through Config is a detour through a settings surface to press a button that
/// is already on screen. Each button names the mode it passes, so nothing in the footer
/// reads the saved default; that default keeps the row's mode chip, Enter on the list, and
/// the CLI.
/// </summary>
public sealed class FooterLaunchModeTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    // The window is fixed at this width, so what the footer holds has to fit it.
    private const double WindowWidth = 720;

    [Fact]
    public void The_footer_runs_settings_open_folder_then_both_launch_modes()
    {
        XDocument document = LoadMainWindow();
        XElement footer = Named(document, "FooterBar");
        string[] order =
        {
            "GlobalSettingsButton",
            "OpenFolderButton",
            "LaunchButton",
            "BuildAndLaunchButton"
        };

        int[] columns = order
            .Select(name => Named(document, name))
            .Select(button =>
            {
                Assert.Contains(button.Ancestors(), element => element == footer);
                return int.Parse(
                    button.Attribute("Grid.Column")?.Value ?? "0",
                    CultureInfo.InvariantCulture);
            })
            .ToArray();

        Assert.Equal(columns.OrderBy(column => column).Distinct().ToArray(), columns);
    }

    [Fact]
    public void Each_launch_button_names_the_mode_it_passes()
    {
        XDocument document = LoadMainWindow();
        XElement direct = Named(document, "LaunchButton");
        XElement build = Named(document, "BuildAndLaunchButton");

        Assert.Equal("Launch", direct.Attribute("Content")?.Value);
        Assert.Equal("Launch_Click", direct.Attribute("Click")?.Value);
        Assert.Equal("{StaticResource ControlButton}", direct.Attribute("Style")?.Value);

        Assert.Equal("Build & Launch", build.Attribute("Content")?.Value);
        Assert.Equal("BuildAndLaunch_Click", build.Attribute("Click")?.Value);
        // The build is the mode the surface leads with, so it keeps the primary control at
        // the trailing edge.
        Assert.Equal("{StaticResource PrimaryButton}", build.Attribute("Style")?.Value);

        // A label that followed a setting needed a named TextBlock to swap. Two buttons
        // that each state one mode need only their own content.
        Assert.Empty(direct.Elements());
        Assert.Empty(build.Elements());
        Assert.Null(FindNamed(document, "LaunchButtonText"));
    }

    [Fact]
    public void The_footer_row_fits_between_the_window_edges()
    {
        XDocument document = LoadMainWindow();
        XElement footer = Named(document, "FooterBar");
        string[] padding = (footer.Attribute("Padding")?.Value ?? string.Empty).Split(',');
        double fixedColumns = footer
            .Descendants()
            .Where(element => element.Name.LocalName == "ColumnDefinition")
            .Select(element => element.Attribute("Width")?.Value ?? "*")
            .Where(width => width != "*")
            .Sum(width => double.Parse(width, CultureInfo.InvariantCulture));
        // The star column carries the left-aligned Settings button, so its own width is
        // what that column has to hold.
        double settings = double.Parse(
            Named(document, "GlobalSettingsButton").Attribute("Width")?.Value ?? "0",
            CultureInfo.InvariantCulture);

        double occupied = fixedColumns +
            settings +
            double.Parse(padding[0], CultureInfo.InvariantCulture) +
            double.Parse(padding[2], CultureInfo.InvariantCulture);

        Assert.True(
            occupied <= WindowWidth,
            $"The footer asks for {occupied}px inside a {WindowWidth}px window.");
    }

    // The markup states widths; this reads where the four buttons actually landed, which is
    // what a fourth button in a fixed-width footer can break.
    [Fact]
    public void The_four_footer_buttons_land_in_one_row_inside_the_window()
    {
        IReadOnlyDictionary<string, Rect> layout = WorktreeSurface.Arrange();
        Rect[] buttons =
        {
            layout.Part("GlobalSettingsButton"),
            layout.Part("OpenFolderButton"),
            layout.Part("LaunchButton"),
            layout.Part("BuildAndLaunchButton")
        };

        for (int index = 1; index < buttons.Length; index++)
        {
            Assert.True(
                buttons[index].Left - buttons[index - 1].Right >= 12,
                $"Footer buttons {index - 1} and {index} sit " +
                $"{buttons[index].Left - buttons[index - 1].Right:0.###}px apart.");
        }

        Assert.True(buttons[0].Left >= 0);
        Assert.True(
            buttons[^1].Right <= WindowWidth,
            $"The last footer button reaches {buttons[^1].Right:0.###}px in a {WindowWidth}px window.");
        Assert.All(buttons, button => Assert.Equal(48, button.Height, precision: 3));
    }

    // Nothing can launch without naming a mode, because there is no overload that would
    // let a call site fall back to whatever the project saved.
    [Fact]
    public void Every_launch_the_window_starts_names_its_own_mode()
    {
        MethodInfo[] launches = typeof(MainWindow)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method => method.Name == "Launch")
            .ToArray();

        MethodInfo launch = Assert.Single(launches);
        Assert.Equal(
            new[] { typeof(WorktreeSnapshot), typeof(LaunchMode) },
            launch.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.NotNull(typeof(MainWindow).GetMethod(
            "BuildAndLaunch_Click",
            BindingFlags.NonPublic | BindingFlags.Instance));
    }

    private static XElement Named(XDocument document, string name) =>
        FindNamed(document, name) ?? throw new InvalidOperationException($"XAML element '{name}' was not found.");

    private static XElement? FindNamed(XDocument document, string name) => document
        .Descendants()
        .FirstOrDefault(element => string.Equals(
            element.Attribute(Xaml + "Name")?.Value,
            name,
            StringComparison.Ordinal));

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
