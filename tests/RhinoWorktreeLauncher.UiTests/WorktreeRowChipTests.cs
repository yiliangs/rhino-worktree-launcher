using System.IO;
using System.Xml.Linq;

namespace RhinoWorktreeLauncher.UiTests;

/// <summary>
/// The row carries two different facts about a worktree. PRIMARY is the Git fact, the
/// repository's main working tree. DEFAULT is the registry fact, the worktree whose build
/// Rhino loads when it is started outside RWL. One chip each, so neither reads as the other.
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
        XElement chip = ChipBoundTo(LoadMainWindow(), "IsRegistered");

        Assert.Equal("DEFAULT", ChipText(chip));
    }

    [Fact]
    public void The_registered_worktree_chip_says_what_DEFAULT_means()
    {
        XElement chip = ChipBoundTo(LoadMainWindow(), "IsRegistered");

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
        Assert.Contains(ChipBoundTo(document, "IsRegistered").Ancestors(), element => element == list);
    }

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
