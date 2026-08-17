using System.Collections;
using System.IO;
using System.Reflection;
using System.Xml.Linq;

namespace RhinoWorktreeLauncher.UiTests;

public sealed class LaunchProgressSurfaceTests
{
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
        Assert.Equal("True", run.Attribute("ClipToBounds")?.Value);
    }

    [Fact]
    public void Launch_progress_fill_starts_empty_on_the_inverted_primary_surface()
    {
        XDocument document = LoadMainWindow();
        XElement fill = Named(document, "LaunchProgressFill");

        Assert.Equal("0", fill.Attribute("Width")?.Value);
        Assert.Equal("Left", fill.Attribute("HorizontalAlignment")?.Value);
        Assert.Equal("{DynamicResource PrimaryProgressBrush}", fill.Attribute("Background")?.Value);
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

        Assert.Contains("PrimaryProgressBrush", dark.Keys);
        Assert.Contains("PrimaryProgressBrush", light.Keys);
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
