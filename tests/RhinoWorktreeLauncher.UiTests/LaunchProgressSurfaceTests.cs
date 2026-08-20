using System.Collections;
using System.Windows;
using System.Windows.Media;
using System.IO;
using System.Reflection;
using System.Xml.Linq;

namespace RhinoWorktreeLauncher.UiTests;

/// <summary>
/// Launch progress reads in the status banner, which is where the surface says what is
/// happening. The button stays a button.
/// </summary>
public sealed class LaunchProgressSurfaceTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void The_launch_button_carries_only_its_label()
    {
        XDocument document = LoadMainWindow();
        XElement button = Named(document, "LaunchButton");

        Assert.Null(button.Attribute("Content"));
        Assert.NotNull(FindNamed(document, "LaunchButtonText"));
        // The idle and progress layers existed to swap one for the other inside the
        // button. Progress moved out, so there is nothing left to swap.
        Assert.Null(FindNamed(document, "LaunchRun"));
        Assert.Null(FindNamed(document, "LaunchIdleText"));
        Assert.DoesNotContain(
            button.Descendants(),
            element => string.Equals(element.Attribute(Xaml + "Name")?.Value, "LaunchProgressFill", StringComparison.Ordinal));
    }

    [Fact]
    public void Launch_progress_sweeps_across_the_status_banner()
    {
        XDocument document = LoadMainWindow();
        XElement fill = Named(document, "LaunchProgressFill");

        Assert.Contains(
            fill.Ancestors(),
            element => string.Equals(element.Attribute(Xaml + "Name")?.Value, "PanelHintBanner", StringComparison.Ordinal));
        Assert.Equal("0", fill.Attribute("Width")?.Value);
        Assert.Equal("Left", fill.Attribute("HorizontalAlignment")?.Value);
        // The tint the Refresh control already sweeps in, not a second progress colour.
        Assert.Equal("{DynamicResource ProgressBrush}", fill.Attribute("Background")?.Value);
    }

    [Fact]
    public void The_sweep_is_a_rectangle()
    {
        XDocument document = LoadMainWindow();

        // Its leading edge is the reading: a straight line at how far the launch has
        // got, not a pill end that rounds away from the number it represents.
        Assert.Null(Named(document, "LaunchProgressFill").Attribute("CornerRadius"));
    }

    [Fact]
    public void The_banner_clips_the_rectangle_to_its_own_corners()
    {
        XDocument document = LoadMainWindow();

        // The fill squares off at both ends, so the banner has to do the rounding, and
        // it does it at a size that follows the panel rather than a fixed geometry.
        Assert.NotNull(Named(document, "PanelHintBannerClip").Attribute("SizeChanged"));
    }

    [Theory]
    [InlineData(400, 38)]
    [InlineData(0, 0)]
    public void The_clip_matches_the_banner_inner_corner(double width, double height)
    {
        MethodInfo clip = typeof(MainWindow).GetMethod(
            "BannerClip",
            BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("MainWindow method 'BannerClip' was not found.");

        RectangleGeometry geometry = (RectangleGeometry)clip.Invoke(null, new object?[] { width, height })!;

        Assert.Equal(new Rect(0, 0, width, height), geometry.Rect);
        // The banner is an 8 radius drawn behind a 1px border.
        Assert.Equal(7, geometry.RadiusX);
        Assert.Equal(7, geometry.RadiusY);
    }

    [Fact]
    public void One_caption_carries_both_halves_of_the_sweep()
    {
        XDocument document = LoadMainWindow();

        Assert.Null(FindNamed(document, "LaunchStageFilledText"));
        // The banner inherits no text colour, and the sweep is a translucent tint over
        // it, so one explicitly coloured caption clears AA on both halves.
        Assert.NotNull(Named(document, "LaunchStageText").Attribute("Foreground"));
    }

    [Fact]
    public void The_stage_caption_is_absent_until_a_launch_runs()
    {
        XDocument document = LoadMainWindow();

        Assert.Equal("Collapsed", Named(document, "LaunchStageText").Attribute("Visibility")?.Value);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    // The stage caption used to live in the button, which had to stay lit to stay
    // legible. It reports in the banner now, so the button can say it is unavailable.
    [InlineData(true, true, false)]
    public void The_button_is_unavailable_while_a_launch_runs(
        bool launching,
        bool configured,
        bool enabled)
    {
        MethodInfo canLaunch = typeof(MainWindow).GetMethod(
            "CanLaunch",
            BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("MainWindow method 'CanLaunch' was not found.");

        Assert.Equal(enabled, canLaunch.Invoke(null, new object?[] { launching, configured }));
    }

    [Fact]
    public void Every_launch_stage_has_a_caption_the_banner_can_show()
    {
        IDictionary steps = PrivateStatic<IDictionary>("LaunchSteps");

        IEnumerable<LaunchStage> covered = steps.Keys.Cast<LaunchStage>();

        Assert.Equal(Enum.GetValues<LaunchStage>().OrderBy(stage => stage), covered.OrderBy(stage => stage));
    }

    [Fact]
    public void Both_themes_define_the_same_brush_keys()
    {
        IReadOnlyDictionary<string, string> dark = PrivateStatic<IReadOnlyDictionary<string, string>>("DarkTheme");
        IReadOnlyDictionary<string, string> light = PrivateStatic<IReadOnlyDictionary<string, string>>("LightTheme");

        Assert.Equal(dark.Keys.OrderBy(key => key, StringComparer.Ordinal), light.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    private static T PrivateStatic<T>(string name)
    {
        FieldInfo field = typeof(MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException($"MainWindow field '{name}' was not found.");
        return (T)(field.GetValue(null) ??
            throw new InvalidOperationException($"MainWindow field '{name}' was null."));
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
