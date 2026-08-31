using System.IO;
using System.Windows;
using System.Xml.Linq;

namespace RhinoWorktreeLauncher.UiTests;

/// <summary>
/// Changing which build Rhino loads outside RWL is an action on one row, so it lives on that
/// row rather than in the footer beside the launch action. It is a button, carrying the
/// footer buttons' control in a gutter the row carves for it: shaped as a chip it read as
/// one more label stating a fact, beside two chips that do exactly that. It is offered only
/// where it has something to do: the row the user is acting on, not already registered, with
/// a build configuration to register.
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
        Assert.Equal("Set default", button.Attribute("Content")?.Value);
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

        Assert.Contains(conditions, binding => binding.Contains("IsRegistered", StringComparison.Ordinal));
        Assert.Contains(conditions, binding => binding.Contains("IsSelected", StringComparison.Ordinal));
        Assert.Contains(
            conditions,
            binding => binding.Contains("HasBuildConfiguration", StringComparison.Ordinal));
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
