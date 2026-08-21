using System.Windows;
using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.UiTests;

/// <summary>
/// The Add project window is the first surface a new user meets, and it is the one that
/// asks for access. Its decorations only read as deliberate if they share their axes, so
/// the axes are asserted against a real arrange pass rather than against the markup that
/// produced them.
/// </summary>
public sealed class AddProjectLayoutTests
{
    // Nothing here is asserted against an absolute coordinate: every claim relates one
    // arranged part to another, so the box the dialog is measured in is not load-bearing.
    private static readonly Size Client = new Size(574, 671);

    [Fact]
    public void The_marker_column_puts_every_decoration_on_one_axis()
    {
        IReadOnlyDictionary<string, Rect> layout = Arrange();
        double axis = layout.Part("RequiredMarker").CentreX();

        Assert.Equal(axis, layout.Part("RequiredRail").CentreX(), precision: 3);
        Assert.Equal(axis, layout.Part("OptionalMarker").CentreX(), precision: 3);
    }

    // The required item carries a REQUIRED badge and the optional one does not, so the two
    // title line boxes are different heights. A marker positioned by a fixed offset from
    // the top of its row therefore lands differently in each, which is what a centre on the
    // title itself is immune to.
    [Fact]
    public void Each_marker_centres_on_the_title_it_marks()
    {
        IReadOnlyDictionary<string, Rect> layout = Arrange();

        Assert.Equal(
            layout.Part("RequiredTitle").CentreY(),
            layout.Part("RequiredMarker").CentreY(),
            precision: 3);
        Assert.Equal(
            layout.Part("OptionalTitle").CentreY(),
            layout.Part("OptionalMarker").CentreY(),
            precision: 3);
    }

    [Fact]
    public void The_remote_toggle_centres_on_the_row_it_switches()
    {
        IReadOnlyDictionary<string, Rect> layout = Arrange();

        Assert.Equal(
            layout.Part("OptionalTitle").CentreY(),
            layout.Part("RemoteReadToggle").CentreY(),
            precision: 3);
    }

    // A divider that starts inside the marker gutter leaves the rail sitting outside the
    // block it is meant to separate, so it has to reach past everything the panel draws.
    [Fact]
    public void The_divider_spans_everything_the_panel_lays_out()
    {
        IReadOnlyDictionary<string, Rect> layout = Arrange();
        Rect divider = layout.Part("AccessDivider");

        string[] parts =
        [
            "RequiredMarker", "RequiredRail", "RequiredTitle", "RequiredDescription",
            "OptionalMarker", "OptionalTitle", "OptionalDescription", "RemoteReadToggle"
        ];
        foreach (string part in parts)
        {
            Rect bounds = layout.Part(part);
            Assert.True(
                divider.Left <= bounds.Left && divider.Right >= bounds.Right,
                $"The divider spans {divider.Left}..{divider.Right}, which does not cover " +
                $"'{part}' at {bounds.Left}..{bounds.Right}.");
        }
    }

    [Fact]
    public void Each_description_wraps_to_the_same_column_as_its_own_title()
    {
        IReadOnlyDictionary<string, Rect> layout = Arrange();

        Assert.Equal(
            layout.Part("RequiredTitle").Right,
            layout.Part("RequiredDescription").Right,
            precision: 3);
        Assert.Equal(
            layout.Part("OptionalTitle").Right,
            layout.Part("OptionalDescription").Right,
            precision: 3);
    }

    [Fact]
    public void Every_caption_starts_on_the_window_text_edge()
    {
        IReadOnlyDictionary<string, Rect> layout = Arrange();
        double edge = layout.Part("EyebrowText").Left;

        Assert.Equal(edge, layout.Part("HeadingText").Left, precision: 3);
        Assert.Equal(edge, layout.Part("BuildSectionCaption").Left, precision: 3);
        Assert.Equal(edge, layout.Part("PluginProjectCaption").Left, precision: 3);
    }

    private static IReadOnlyDictionary<string, Rect> Arrange() => SurfaceLayout.Arrange(
        () => new AddProjectDialog(@"C:\repos\ExampleTool", BuildOptions()),
        Client);

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
}
