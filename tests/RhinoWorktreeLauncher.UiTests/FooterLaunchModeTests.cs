using System.IO;
using System.Reflection;
using System.Windows;
using System.Xml.Linq;

namespace RhinoWorktreeLauncher.UiTests;

/// <summary>
/// The footer publishes both launch modes rather than one button whose label follows a
/// saved setting, and it says which is which by arrangement rather than by four identical
/// controls in a row. Settings acts on the application and stands alone on the left; Open
/// folder, Launch and Build &amp; Launch all act on the selected worktree and group on the
/// right, where the two launch modes sit closer to each other than to Open folder because
/// they are one choice. The three tiers the window already uses carry the weight: ghost for
/// the incidental, control for the secondary, primary for the call to action. Nothing in
/// the footer reads the saved launch-mode default; that default keeps the row's mode chip,
/// Enter on the list, and the CLI.
/// </summary>
public sealed class FooterLaunchModeTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    // The window is fixed at this size and the footer insets its content by this much, so
    // what the footer holds has to fit between them.
    private const double WindowWidth = 720;
    private const double FooterInset = 26;

    // Layout is decided by the arranged tree, so the reading order is read from where the
    // buttons landed rather than from column numbers a later layout would renumber.
    [Fact]
    public void The_footer_runs_settings_open_folder_then_both_launch_modes()
    {
        Rect[] buttons = FooterButtons();

        for (int index = 1; index < buttons.Length; index++)
        {
            Assert.True(
                buttons[index].Left >= buttons[index - 1].Right,
                $"Footer button {index} starts at {buttons[index].Left:0.###}px, " +
                $"before button {index - 1} ends at {buttons[index - 1].Right:0.###}px.");
        }
    }

    // The two launch modes are one choice, so they sit tighter to each other than to the
    // action that merely shares their group.
    [Fact]
    public void The_two_launch_modes_read_as_one_pair()
    {
        Rect[] buttons = FooterButtons();

        Assert.Equal(12, buttons[2].Left - buttons[1].Right, precision: 0);
        Assert.Equal(8, buttons[3].Left - buttons[2].Right, precision: 0);
    }

    // The pair offers one choice between two modes, so neither is the smaller option.
    // Which one a user reaches for is said by the tier the button carries, never by making
    // the other one harder to hit.
    [Fact]
    public void Neither_launch_mode_is_the_smaller_button()
    {
        Rect[] buttons = FooterButtons();

        Assert.Equal(buttons[3].Width, buttons[2].Width, precision: 3);
        Assert.Equal(buttons[3].Height, buttons[2].Height, precision: 3);
    }

    // Three tiers of control at three different heights, so one baseline is what keeps the
    // footer a row rather than a staircase.
    [Fact]
    public void Every_footer_button_stands_on_one_centre_line()
    {
        Rect[] buttons = FooterButtons();
        double centre = buttons[0].CentreY();

        Assert.All(buttons, button => Assert.InRange(Math.Abs(button.CentreY() - centre), 0, 1));
    }

    [Fact]
    public void Each_footer_button_carries_the_tier_its_job_deserves()
    {
        XDocument document = LoadMainWindow();

        // Incidental, and one of them is not even about the selected worktree.
        Assert.Equal(
            "{StaticResource GhostButton}",
            Named(document, "GlobalSettingsButton").Attribute("Style")?.Value);
        Assert.Equal(
            "{StaticResource GhostButton}",
            Named(document, "OpenFolderButton").Attribute("Style")?.Value);
        // The launch a user asks for less often, offered rather than urged.
        Assert.Equal(
            "{StaticResource ControlButton}",
            Named(document, "LaunchButton").Attribute("Style")?.Value);
        // The one thing the window exists to do.
        Assert.Equal(
            "{StaticResource PrimaryButton}",
            Named(document, "BuildAndLaunchButton").Attribute("Style")?.Value);
    }

    [Fact]
    public void Each_launch_button_names_the_mode_it_passes()
    {
        XDocument document = LoadMainWindow();
        XElement direct = Named(document, "LaunchButton");
        XElement build = Named(document, "BuildAndLaunchButton");

        Assert.Equal("Launch", direct.Attribute("Content")?.Value);
        Assert.Equal("Launch_Click", direct.Attribute("Click")?.Value);
        Assert.Equal("Build & Launch", build.Attribute("Content")?.Value);
        Assert.Equal("BuildAndLaunch_Click", build.Attribute("Click")?.Value);

        // Two words each, so the difference between the modes is spelled out where a user
        // deciding between them is already looking.
        Assert.Equal(
            "Load the existing build without building",
            direct.Attribute("ToolTip")?.Value);
        Assert.Equal(
            "Build the selected solution, then load the build",
            build.Attribute("ToolTip")?.Value);

        // A label that followed a setting needed a named TextBlock to swap. Two buttons
        // that each state one mode need only their own content.
        Assert.Empty(direct.Elements());
        Assert.Empty(build.Elements());
        Assert.Null(FindNamed(document, "LaunchButtonText"));
    }

    [Fact]
    public void The_footer_row_fits_between_the_window_edges()
    {
        Rect[] buttons = FooterButtons();

        Assert.True(
            buttons[0].Left >= FooterInset - 1,
            $"Settings starts at {buttons[0].Left:0.###}px, inside the {FooterInset}px inset.");
        Assert.True(
            buttons[^1].Right <= WindowWidth - FooterInset + 1,
            $"The last footer button reaches {buttons[^1].Right:0.###}px in a {WindowWidth}px window.");
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

    /// <summary>
    /// The four footer buttons as the window actually arranged them, in reading order.
    /// </summary>
    private static Rect[] FooterButtons()
    {
        IReadOnlyDictionary<string, Rect> layout = WorktreeSurface.Arrange();
        return new[]
        {
            layout.Part("GlobalSettingsButton"),
            layout.Part("OpenFolderButton"),
            layout.Part("LaunchButton"),
            layout.Part("BuildAndLaunchButton")
        };
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
