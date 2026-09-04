using System.IO;
using System.Windows;
using System.Xml.Linq;

namespace RhinoWorktreeLauncher.UiTests;

/// <summary>
/// Changing which build Rhino loads outside RWL is an action on one row, so it lives on that
/// row rather than in the footer beside the launch action. It is a button, carrying the
/// footer buttons' control in the gutter the row carves for it: shaped as a chip it read as
/// one more label stating a fact. It is offered wherever it has something to do: the row the
/// user is acting on, with a build configuration to register. Already being the default is
/// not a reason to withhold it, because a registration another tool dropped or rewrote is
/// re-applied by writing the same worktree again.
/// </summary>
public sealed class WorktreeRowActionTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void The_row_carries_a_set_default_button_that_calls_its_own_handler()
    {
        XElement button = Named(LoadMainWindow(), "SetDefaultButton");

        Assert.Equal("Button", button.Name.LocalName);
        Assert.Equal("SetDefault_Click", button.Attribute("Click")?.Value);
        // The label is one of two, so it is decided where the trigger that decides it lives.
        Assert.Null(button.Attribute("Content"));
    }

    // Same handler, same write, two labels: setting a default the row does not hold, and
    // re-applying the one it does.
    [Fact]
    public void The_button_says_whether_it_sets_the_default_or_re_applies_it()
    {
        XElement style = Keyed(LoadMainWindow(), "RowActionButton");

        Assert.Contains(
            style.Elements().Where(element => element.Name.LocalName == "Setter"),
            setter => setter.Attribute("Property")?.Value == "Content" &&
                setter.Attribute("Value")?.Value == "Set default");

        XElement registered = style
            .Descendants()
            .Single(element => element.Name.LocalName == "DataTrigger");
        Assert.Equal("{Binding IsRegistered}", registered.Attribute("Binding")?.Value);
        Assert.Equal("True", registered.Attribute("Value")?.Value);
        Assert.Contains(
            registered.Elements(),
            setter => setter.Attribute("Property")?.Value == "Content" &&
                setter.Attribute("Value")?.Value == "Reset default");
        // Two different actions in the user's terms, so two different explanations.
        Assert.Contains(
            registered.Elements(),
            setter => setter.Attribute("Property")?.Value == "ToolTip");
        Assert.Contains(
            style.Elements().Where(element => element.Name.LocalName == "Setter"),
            setter => setter.Attribute("Property")?.Value == "ToolTip");
        Assert.Null(Named(LoadMainWindow(), "SetDefaultButton").Attribute("ToolTip"));
    }

    [Fact]
    public void The_set_default_button_is_the_footer_buttons_control()
    {
        XDocument document = LoadMainWindow();
        XElement style = Keyed(document, "RowActionButton");

        Assert.Equal("{StaticResource ControlButton}", style.Attribute("BasedOn")?.Value);
        Assert.Equal(
            "{StaticResource RowActionButton}",
            Named(document, "SetDefaultButton").Attribute("Style")?.Value);
        // No hand-rolled chrome of its own: the row action is the control the footer uses,
        // at a row's scale.
        Assert.Empty(Named(document, "SetDefaultButton").Elements());
    }

    [Fact]
    public void The_set_default_button_sits_in_the_row_templates_own_gutter()
    {
        XDocument document = LoadMainWindow();
        XElement row = Named(document, "WorktreeRow");
        XElement gutter = Named(document, "RowActionGutter");
        XElement button = Named(document, "SetDefaultButton");

        Assert.Contains(row.Ancestors(), element => element == Named(document, "WorktreeList"));
        // A fixed trailing column, so the row's content ends where the gutter begins rather
        // than the action floating over content that shrank to make room for it.
        Assert.Same(row, gutter.Parent!.Parent);
        Assert.Equal("96", gutter.Attribute("Width")?.Value);
        Assert.Single(row.Elements(), element => element.Name.LocalName == "Grid.ColumnDefinitions");
        Assert.Equal("2", button.Attribute("Grid.Column")?.Value);
        Assert.Same(row, button.Parent);
    }

    // The header reserves the same width the rows give away, so the BEHIND and AHEAD
    // captions keep standing over the bars they name. The arranged proof of that is in
    // ListScrollRailTests; this is the reservation itself.
    [Fact]
    public void The_panel_header_reserves_the_gutter_the_rows_give_away()
    {
        XDocument document = LoadMainWindow();

        Assert.Equal("108", Named(document, "HeaderActionSpacer").Attribute("Width")?.Value);
    }

    [Fact]
    public void The_set_default_button_is_offered_only_where_it_has_something_to_do()
    {
        string[] conditions = Keyed(LoadMainWindow(), "RowActionButton")
            .Descendants()
            .Where(element => element.Name.LocalName == "Condition")
            .Select(element => element.Attribute("Binding")?.Value ?? string.Empty)
            .ToArray();

        Assert.Equal(2, conditions.Length);
        Assert.Contains(conditions, binding => binding.Contains("IsSelected", StringComparison.Ordinal));
        Assert.Contains(
            conditions,
            binding => binding.Contains("HasBuildConfiguration", StringComparison.Ordinal));
        // Whether the row is already the default decides the label, never whether the
        // action is there at all.
        Assert.DoesNotContain(
            conditions,
            binding => binding.Contains("IsRegistered", StringComparison.Ordinal));
    }

    // Hidden by default from the style, so a row that does not qualify shows nothing and a
    // local visibility value can never outrank the trigger that decides.
    [Fact]
    public void The_set_default_button_is_hidden_until_the_row_qualifies()
    {
        XDocument document = LoadMainWindow();

        Assert.Null(Named(document, "SetDefaultButton").Attribute("Visibility"));
        Assert.Contains(
            Keyed(document, "RowActionButton").Elements()
                .Where(element => element.Name.LocalName == "Setter"),
            setter => setter.Attribute("Property")?.Value == "Visibility" &&
                setter.Attribute("Value")?.Value == "Collapsed");
    }

    // A qualifying row shows a control at a pressable size, not a caption.
    [Fact]
    public void The_offered_button_is_a_pressable_size()
    {
        Rect action = WorktreeSurface.Arrange().Part("SetDefaultButton");

        Assert.Equal(30, action.Height, precision: 3);
        Assert.Equal(96, action.Width, precision: 3);
    }

    // The gutter of the row that is already the default carries the action, not the chip:
    // a registration Rhino or another tool rewrote is re-applied from where it is read.
    [Fact]
    public void The_row_that_is_already_the_default_is_offered_the_action_too()
    {
        IReadOnlyDictionary<string, Rect> layout = WorktreeSurface.Arrange(
            WorktreeSurface.Row(isRegistered: true),
            selected: true);
        Rect action = layout.Part("SetDefaultButton");

        Assert.Equal(30, action.Height, precision: 3);
        Assert.Equal(96, action.Width, precision: 3);
        Assert.False(layout.ContainsKey("DefaultChip"), "The gutter showed the chip and the action at once.");
    }

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
