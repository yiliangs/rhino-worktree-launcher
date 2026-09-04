using System.IO;
using System.Windows;
using System.Xml.Linq;

namespace RhinoWorktreeLauncher.UiTests;

/// <summary>
/// The row carries two different facts about a worktree. PRIMARY is the Git fact, the
/// repository's main working tree, and it sits with the name it qualifies. DEFAULT is the
/// registry fact, the worktree whose build Rhino loads when it is started outside RWL, and
/// it sits in the row's action gutter, where the action that changes it appears. One chip
/// each, so neither reads as the other, and one gutter that either states the fact or
/// offers the action, never both.
/// </summary>
public sealed class WorktreeRowChipTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void The_primary_checkout_chip_reads_PRIMARY()
    {
        XElement chip = ChipBoundTo(LoadMainWindow(), "IsPrimary");

        Assert.Equal("PRIMARY", ChipText(chip));
    }

    [Fact]
    public void The_registered_worktree_chip_reads_DEFAULT()
    {
        XElement chip = Named(LoadMainWindow(), "DefaultChip");

        Assert.Equal("DEFAULT", ChipText(chip));
    }

    [Fact]
    public void The_registered_worktree_chip_says_what_DEFAULT_means()
    {
        XElement chip = Named(LoadMainWindow(), "DefaultChip");

        Assert.Equal(
            "Rhino loads this worktree's build when started outside RWL",
            chip.Attribute("ToolTip")?.Value);
    }

    [Fact]
    public void Both_chips_sit_in_the_worktree_row_template()
    {
        XDocument document = LoadMainWindow();
        XElement list = Named(document, "WorktreeList");

        Assert.Contains(ChipBoundTo(document, "IsPrimary").Ancestors(), element => element == list);
        Assert.Contains(Named(document, "DefaultChip").Ancestors(), element => element == list);
    }

    // PRIMARY qualifies the name, so it stays beside it. DEFAULT names the thing the gutter
    // acts on, so it moves out of the identity run and into the gutter.
    [Fact]
    public void The_default_chip_sits_in_the_rows_action_gutter_and_not_beside_the_name()
    {
        XDocument document = LoadMainWindow();
        XElement row = Named(document, "WorktreeRow");
        XElement chip = Named(document, "DefaultChip");

        Assert.Same(row, chip.Parent);
        Assert.Equal("2", chip.Attribute("Grid.Column")?.Value);
        Assert.Equal("Right", chip.Attribute("HorizontalAlignment")?.Value);
        Assert.DoesNotContain(chip.Ancestors(), IsIdentityPanel);
        Assert.Contains(ChipBoundTo(document, "IsPrimary").Ancestors(), IsIdentityPanel);
    }

    // The gutter states the fact when the row is idle and offers the action when the row is
    // selected. Both rules live in the style, so no local visibility value can outrank them.
    [Fact]
    public void The_default_chip_gives_the_gutter_up_to_the_action()
    {
        XDocument document = LoadMainWindow();
        XElement chip = Named(document, "DefaultChip");
        XElement style = Keyed(document, "RowDefaultChip");

        Assert.Null(chip.Attribute("Visibility"));
        Assert.Equal("{StaticResource RowDefaultChip}", chip.Attribute("Style")?.Value);
        Assert.Contains(
            style.Elements().Where(element => element.Name.LocalName == "Setter"),
            setter => setter.Attribute("Property")?.Value == "Visibility" &&
                setter.Attribute("Value")?.Value == "Collapsed");

        XElement shown = style
            .Descendants()
            .Single(element => element.Name.LocalName == "DataTrigger");
        Assert.Equal("{Binding IsRegistered}", shown.Attribute("Binding")?.Value);
        Assert.Equal("True", shown.Attribute("Value")?.Value);
        Assert.Contains(
            shown.Elements(),
            setter => setter.Attribute("Property")?.Value == "Visibility" &&
                setter.Attribute("Value")?.Value == "Visible");

        XElement hidden = style
            .Descendants()
            .Single(element => element.Name.LocalName == "MultiDataTrigger");
        string[] conditions = hidden
            .Descendants()
            .Where(element => element.Name.LocalName == "Condition")
            .Select(element => element.Attribute("Binding")?.Value ?? string.Empty)
            .ToArray();
        Assert.Equal(2, conditions.Length);
        Assert.Contains(conditions, binding => binding.Contains("IsSelected", StringComparison.Ordinal));
        Assert.Contains(
            conditions,
            binding => binding.Contains("HasBuildConfiguration", StringComparison.Ordinal));
        Assert.Contains(
            hidden.Elements(),
            setter => setter.Attribute("Property")?.Value == "Visibility" &&
                setter.Attribute("Value")?.Value == "Collapsed");
    }

    // The arranged proof of the rule the markup states: an idle registered row shows the
    // chip where the action would have been, at the same right edge, and offers no button.
    [Fact]
    public void An_idle_registered_row_shows_the_chip_in_the_gutter()
    {
        IReadOnlyDictionary<string, Rect> idle = WorktreeSurface.Arrange(
            WorktreeSurface.Row(isRegistered: true),
            selected: false);
        Rect chip = idle.Part("DefaultChip");

        Assert.False(idle.ContainsKey("SetDefaultButton"), "An unselected row offered the action.");
        Assert.True(chip.Width > 0 && chip.Height > 0);
        Assert.Equal(
            WorktreeSurface.Arrange().Part("SetDefaultButton").Right,
            chip.Right,
            precision: 3);
    }

    // A row with nothing to build is not launchable, so the gutter never offers the action
    // there. The fact it already states has to survive selecting it anyway.
    [Fact]
    public void A_selected_row_that_cannot_be_launched_keeps_showing_the_chip()
    {
        IReadOnlyDictionary<string, Rect> layout = WorktreeSurface.Arrange(
            WorktreeSurface.Row(isRegistered: true, hasBuildConfiguration: false),
            selected: true);

        Assert.True(layout.Part("DefaultChip").Width > 0);
        Assert.False(layout.ContainsKey("SetDefaultButton"), "An unlaunchable row offered the action.");
    }

    private static bool IsIdentityPanel(XElement element) =>
        element.Name.LocalName == "InlineIdentityPanel";

    private static XElement ChipBoundTo(XDocument document, string property) => document
        .Descendants()
        .FirstOrDefault(element => element.Attribute("Visibility")?.Value
            .Contains($"Binding {property},", StringComparison.Ordinal) == true) ??
        throw new InvalidOperationException($"No chip binds its visibility to '{property}'.");

    private static string? ChipText(XElement chip) => chip
        .Descendants()
        .Select(element => element.Attribute("Text")?.Value)
        .FirstOrDefault(text => text is not null);

    private static XElement Named(XDocument document, string name) => document
        .Descendants()
        .FirstOrDefault(element => string.Equals(
            element.Attribute(Xaml + "Name")?.Value,
            name,
            StringComparison.Ordinal)) ??
        throw new InvalidOperationException($"XAML element '{name}' was not found.");

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
