using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.UiTests;

/// <summary>
/// Add project and Project Settings are the same surface asked at two moments, so they draw
/// from one palette rather than two copies of it. A copy is not visibly wrong on the day it
/// is made, which is why the drift it produced was only ever readable by placing the two
/// documents side by side. These tests read them the same way: a key the shared dictionary
/// declares may not be restated by either window.
/// </summary>
public sealed class DialogStyleTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private const string SharedDictionarySource = "Themes/DialogStyles.xaml";

    [Fact]
    public void Add_project_takes_its_palette_from_the_shared_dialog_dictionary()
    {
        AssertApplicationMergesTheSharedDictionary();

        IReadOnlyCollection<string> restated = KeysRestatedBy("AddProjectDialog.xaml");

        Assert.True(
            restated.Count == 0,
            "Add project restates shared dialog keys instead of reading them: " +
            string.Join(", ", restated));
    }

    [Fact]
    public void Project_settings_takes_its_palette_from_the_shared_dialog_dictionary()
    {
        AssertApplicationMergesTheSharedDictionary();

        IReadOnlyCollection<string> restated = KeysRestatedBy("ProjectConfigDialog.xaml");

        Assert.True(
            restated.Count == 0,
            "Project Settings restates shared dialog keys instead of reading them: " +
            string.Join(", ", restated));
    }

    // Project Settings is the reference surface named by the request, so where the two
    // windows had drifted its value is the one the shared dictionary carries. Each colour
    // here is one the Add project copy stated differently.
    [Theory]
    [InlineData("PanelBorderBrush", "#26292E")]
    [InlineData("DividerBrush", "#232629")]
    [InlineData("TextSecondaryBrush", "#878D95")]
    [InlineData("TextMutedBrush", "#6C727A")]
    [InlineData("AccentBrush", "#8FAE8B")]
    public void The_shared_palette_carries_the_project_settings_value(string key, string colour)
    {
        XDocument shared = LoadXaml(SharedDictionarySource);

        XElement brush = shared
            .Root!
            .Elements(Presentation + "SolidColorBrush")
            .FirstOrDefault(element => string.Equals(
                element.Attribute(Xaml + "Key")?.Value,
                key,
                StringComparison.Ordinal)) ??
            throw new InvalidOperationException($"The shared dialog dictionary declares no '{key}'.");

        Assert.Equal(colour, brush.Attribute("Color")?.Value);
    }

    // The controls moved with the palette, so the window that defined them still reaches
    // them and the window that had its own smaller pair is free to keep it.
    [Fact]
    public void The_shared_dictionary_owns_the_controls_project_settings_presses()
    {
        IReadOnlyCollection<string> shared = SharedKeys();
        XDocument config = LoadXaml("ProjectConfigDialog.xaml");

        Assert.Contains("PrimaryButton", shared);
        Assert.Contains("SecondaryButton", shared);
        Assert.Contains("SettingsToggleRow", shared);
        Assert.Equal(
            "{StaticResource PrimaryButton}",
            Named(config, "SaveConfigButton").Attribute("Style")?.Value);
        Assert.Equal(
            "{StaticResource SettingsToggleRow}",
            Named(config, "BuildBeforeLaunchToggle").Attribute("Style")?.Value);
    }

    // What the shared dictionary declares is only the fallback: a dialog copies its owner's
    // live theme over those keys, so the palette a user sees is the main window's. Both
    // dialogs read one list to do it, and the validation colour is part of that list, so
    // Add project follows the owner on it as Project Settings already did.
    //
    // The owner is passed rather than assigned, because WPF refuses Window.Owner until the
    // owner window has been shown and showing the main window here would run its Loaded
    // handler and the catalog read behind it. That leaves the call sites, one line in each
    // dialog, as the part no fixture can assert.
    [Fact]
    public void Add_project_follows_its_owner_on_the_shared_key_list() =>
        AssertFollowsItsOwner(() => new AddProjectDialog(@"C:\repos\ExampleTool", BuildOptions()));

    [Fact]
    public void Project_settings_follows_its_owner_on_the_shared_key_list() =>
        AssertFollowsItsOwner(() => new ProjectConfigDialog(
            Registration(),
            _ => Task.FromResult(CommandResult<ProjectBuildOptions>.Success(BuildOptions()))));

    private static void AssertFollowsItsOwner(Func<Window> create)
    {
        (object? divider, object? validation) = SurfaceLayout.Run(() =>
        {
            Window owner = new Window();
            owner.Resources["DividerBrush"] = Brushes.Magenta;
            owner.Resources["BehindTextBrush"] = Brushes.Orange;

            Window dialog = create();
            OwnerTheme.Apply(dialog, owner);
            return (dialog.Resources["DividerBrush"], dialog.Resources["ValidationBrush"]);
        });

        Assert.Same(Brushes.Magenta, divider);
        Assert.Same(Brushes.Orange, validation);
    }

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

    private static ProjectRegistration Registration() => new ProjectRegistration(
        "example",
        "ExampleTool",
        @"C:\repos\ExampleTool\.git",
        @"C:\repos\ExampleTool",
        8,
        ProjectAccessGrant.Full,
        BuildProfile.Unconfigured);

    private static void AssertApplicationMergesTheSharedDictionary()
    {
        XDocument application = LoadXaml("App.xaml");

        Assert.Contains(application.Descendants(Presentation + "ResourceDictionary"), element =>
            string.Equals(
                element.Attribute("Source")?.Value,
                SharedDictionarySource,
                StringComparison.Ordinal));
    }

    /// <summary>
    /// The shared keys the window declares again in its own <c>Window.Resources</c>, which
    /// a local declaration would shadow for every lookup the window makes.
    /// </summary>
    private static IReadOnlyCollection<string> KeysRestatedBy(string fileName)
    {
        IReadOnlyCollection<string> shared = SharedKeys();
        XDocument window = LoadXaml(fileName);
        XElement? resources = window.Root?.Element(Presentation + "Window.Resources");
        if (resources is null)
            return Array.Empty<string>();

        return resources
            .Descendants()
            .Select(element => element.Attribute(Xaml + "Key")?.Value)
            .Where(key => key is not null && shared.Contains(key))
            .Select(key => key!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyCollection<string> SharedKeys() => LoadXaml(SharedDictionarySource)
        .Root!
        .Elements()
        .Select(element => element.Attribute(Xaml + "Key")?.Value)
        .Where(key => key is not null)
        .Select(key => key!)
        .ToHashSet(StringComparer.Ordinal);

    private static XElement Named(XDocument document, string name) => document
        .Descendants()
        .FirstOrDefault(element => string.Equals(
            element.Attribute(Xaml + "Name")?.Value,
            name,
            StringComparison.Ordinal)) ??
        throw new InvalidOperationException($"XAML element '{name}' was not found.");

    private static XDocument LoadXaml(string fileName) => XDocument.Load(Path.Combine(
        RepositoryRoot(),
        "src",
        "RhinoWorktreeLauncher",
        fileName.Replace('/', Path.DirectorySeparatorChar)));

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
