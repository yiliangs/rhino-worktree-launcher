using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace RhinoWorktreeLauncher.UiTests;

public sealed class ProjectSelectorTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Project_selection_is_a_drop_down_built_from_the_shared_component()
    {
        XDocument document = LoadXaml("MainWindow.xaml");
        XElement selector = Named(document, "ProjectSelector");

        Assert.Equal(Presentation + "ComboBox", selector.Name);
        Assert.Equal("{StaticResource BuildCombo}", selector.Attribute("Style")?.Value);

        // Listing every registered project at once was the reported defect, so no
        // flat project list survives; the only list left is the worktree rail.
        Assert.Null(FindNamed(document, "ProjectList"));
        Assert.Equal(
            "WorktreeList",
            document.Descendants(Presentation + "ListBox").Single().Attribute(Xaml + "Name")?.Value);
    }

    [Fact]
    public void The_drop_down_names_projects_for_the_eye_and_for_assistive_technology()
    {
        XDocument main = LoadXaml("MainWindow.xaml");
        XDocument dropdowns = LoadXaml(Path.Combine("Themes", "DropdownStyles.xaml"));
        XElement selector = Named(main, "ProjectSelector");

        Assert.Equal("{StaticResource ProjectDropdownValue}", selector.Attribute("ItemTemplate")?.Value);
        Assert.Equal("{StaticResource ProjectDropdownItem}", selector.Attribute("ItemContainerStyle")?.Value);
        Assert.Equal(
            "{Binding DisplayName}",
            Keyed(dropdowns, "DataTemplate", "ProjectDropdownValue")
                .Descendants(Presentation + "TextBlock")
                .Single()
                .Attribute("Text")?.Value);

        // Without an explicit container name, UI Automation reads the whole
        // ProjectSnapshot record out through its diagnostic ToString().
        XElement itemStyle = Keyed(dropdowns, "Style", "ProjectDropdownItem");
        Assert.Equal("{StaticResource DropdownItem}", itemStyle.Attribute("BasedOn")?.Value);
        Assert.Contains(itemStyle.Elements(Presentation + "Setter"), setter =>
            string.Equals(setter.Attribute("Property")?.Value, "AutomationProperties.Name", StringComparison.Ordinal) &&
            string.Equals(setter.Attribute("Value")?.Value, "{Binding DisplayName}", StringComparison.Ordinal));
    }

    [Fact]
    public void The_repository_path_sits_below_the_selector_behind_a_quote_rule()
    {
        XDocument document = LoadXaml("MainWindow.xaml");
        XElement selector = Named(document, "ProjectSelector");
        XElement pathRow = Parent(Named(document, "RepositoryPathText"));
        XElement identityBlock = Parent(pathRow);

        // The path owns its own row, so the full window width is available to it
        // instead of whatever the selector leaves over.
        Assert.Equal("0", selector.Attribute("Grid.Row")?.Value);
        Assert.Equal("1", pathRow.Attribute("Grid.Row")?.Value);

        XElement quoteRule = pathRow.Elements(Presentation + "Border").Single();
        Assert.Equal("1", quoteRule.Attribute("Width")?.Value);
        Assert.Equal("{DynamicResource DividerBrush}", quoteRule.Attribute("Background")?.Value);

        // The path is centred in the band between the selector and the worktree
        // panel: the gap it opens above itself matches the gap the block leaves below.
        double above = MarginEdge(pathRow, 1);
        double below = MarginEdge(identityBlock, 3);
        Assert.True(above > 0, "The path must stand clear of the selector.");
        Assert.Equal(above, below);
    }

    private static double MarginEdge(XElement element, int edge) => double.Parse(
        (element.Attribute("Margin")?.Value ?? "0,0,0,0").Split(',')[edge].Trim(),
        CultureInfo.InvariantCulture);

    private static XElement Parent(XElement element) => element.Parent ??
        throw new InvalidOperationException($"XAML element '{element.Name}' has no parent.");

    private static XElement Named(XDocument document, string name) =>
        FindNamed(document, name) ?? throw new InvalidOperationException($"XAML element '{name}' was not found.");

    private static XElement Keyed(XDocument document, string elementName, string key) => document
        .Descendants(Presentation + elementName)
        .FirstOrDefault(element => string.Equals(element.Attribute(Xaml + "Key")?.Value, key, StringComparison.Ordinal)) ??
        throw new InvalidOperationException($"XAML resource '{key}' was not found.");

    private static XElement? FindNamed(XDocument document, string name) => document
        .Descendants()
        .FirstOrDefault(element => string.Equals(
            element.Attribute(Xaml + "Name")?.Value,
            name,
            StringComparison.Ordinal));

    private static XDocument LoadXaml(string fileName) => XDocument.Load(Path.Combine(
        RepositoryRoot(),
        "src",
        "RhinoWorktreeLauncher",
        fileName));

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
