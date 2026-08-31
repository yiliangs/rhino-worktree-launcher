using System.IO;
using System.Xml.Linq;

namespace RhinoWorktreeLauncher.UiTests;

/// <summary>
/// Changing which build Rhino loads outside RWL is an action on one row, so it lives on that
/// row rather than in the footer beside the launch action. It is offered only where it has
/// something to do: the row the user is acting on, not already registered, with a build
/// configuration to register.
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
    }

    [Fact]
    public void The_set_default_button_sits_in_the_worktree_row_template()
    {
        XDocument document = LoadMainWindow();

        Assert.Contains(
            Named(document, "SetDefaultButton").Ancestors(),
            element => element == Named(document, "WorktreeList"));
    }

    [Fact]
    public void The_set_default_button_is_offered_only_where_it_has_something_to_do()
    {
        XElement button = Named(LoadMainWindow(), "SetDefaultButton");
        string[] conditions = button
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

    // Hidden by default, so a row that is not selected shows nothing until the trigger says
    // otherwise, and a local visibility value can never win over that trigger.
    [Fact]
    public void The_set_default_button_is_hidden_until_the_row_qualifies()
    {
        XElement button = Named(LoadMainWindow(), "SetDefaultButton");

        Assert.Null(button.Attribute("Visibility"));
        Assert.Contains(
            button.Descendants().Where(element => element.Name.LocalName == "Setter"),
            setter => setter.Attribute("Property")?.Value == "Visibility" &&
                setter.Attribute("Value")?.Value == "Collapsed");
    }

    private static XElement Named(XDocument document, string name) => document
        .Descendants()
        .FirstOrDefault(element => string.Equals(
            element.Attribute(Xaml + "Name")?.Value,
            name,
            StringComparison.Ordinal)) ??
        throw new InvalidOperationException($"XAML element '{name}' was not found.");

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
