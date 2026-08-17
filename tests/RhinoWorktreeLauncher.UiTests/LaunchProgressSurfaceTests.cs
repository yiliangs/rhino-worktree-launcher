using System.Collections;
using System.IO;
using System.Reflection;
using System.Xml.Linq;

namespace RhinoWorktreeLauncher.UiTests;

public sealed class LaunchProgressSurfaceTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Launch_button_carries_an_idle_layer_and_a_collapsed_progress_layer()
    {
        XDocument document = LoadMainWindow();
        XElement button = Named(document, "LaunchButton");
        XElement run = Named(document, "LaunchRun");

        // The idle caption moved into a named layer so the progress layer can replace it.
        Assert.Null(button.Attribute("Content"));
        Assert.NotNull(FindNamed(document, "LaunchIdleText"));
        Assert.NotNull(FindNamed(document, "LaunchStageText"));
        Assert.Equal("Collapsed", run.Attribute("Visibility")?.Value);
    }

    [Fact]
    public void Launch_progress_starts_empty_and_fills_with_the_button_text_colour()
    {
        XDocument document = LoadMainWindow();
        XElement clip = Named(document, "LaunchFillClip");
        XElement fill = Named(document, "LaunchProgressFill");

        Assert.Equal("0", clip.Attribute("Width")?.Value);
        Assert.Equal("Left", clip.Attribute("HorizontalAlignment")?.Value);
        Assert.Equal("True", clip.Attribute("ClipToBounds")?.Value);
        // Solid, in the button's own two colours: no accent hue on the primary surface.
        Assert.Equal("{DynamicResource PrimaryTextBrush}", fill.Attribute("Background")?.Value);
        Assert.Null(fill.Attribute("Opacity"));
    }

    [Fact]
    public void The_filled_caption_spans_the_whole_track_so_the_sweep_inverts_it_in_place()
    {
        XDocument document = LoadMainWindow();
        XElement baseCaption = Named(document, "LaunchStageText");
        XElement filledCaption = Named(document, "LaunchStageFilledText");

        // The filled layer must sit exactly over the base caption: full track width,
        // left aligned, same centring. Centring it inside the clip would slide the
        // glyphs as the fill grows.
        Assert.Equal("162", filledCaption.Attribute("Width")?.Value);
        Assert.Equal("Left", filledCaption.Attribute("HorizontalAlignment")?.Value);
        Assert.Equal("{DynamicResource PrimaryBrush}", filledCaption.Attribute("Foreground")?.Value);
        foreach (string shared in new[] { "TextAlignment", "FontSize", "FontWeight", "Tracking" })
            Assert.Equal(baseCaption.Attribute(shared)?.Value, filledCaption.Attribute(shared)?.Value);
    }

    [Fact]
    public void The_progress_layer_is_rounded_to_the_button_corner()
    {
        XDocument document = LoadMainWindow();
        XElement geometry = Named(document, "LaunchRun")
            .Descendants(Presentation + "RectangleGeometry")
            .Single();

        Assert.Equal("0,0,162,46", geometry.Attribute("Rect")?.Value);
        Assert.Equal("7", geometry.Attribute("RadiusX")?.Value);
        Assert.Equal("7", geometry.Attribute("RadiusY")?.Value);
    }

    [Fact]
    public void Every_launch_stage_has_a_caption_the_button_can_show()
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
