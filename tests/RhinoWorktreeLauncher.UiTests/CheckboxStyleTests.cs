using System.Diagnostics;
using System.IO;
using System.Xml.Linq;

namespace RhinoWorktreeLauncher.UiTests;

public sealed class CheckboxStyleTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Application_opens_when_only_SystemRoot_is_inherited()
    {
        string executable = Path.Combine(
            RepositoryRoot(),
            "src",
            "RhinoWorktreeLauncher",
            "bin",
            "Debug",
            "net8.0-windows",
            "win-x64",
            "RhinoWorktreeLauncher.exe");
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false
        };
        _ = startInfo.Environment.Remove("windir");
        startInfo.Environment["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start the WPF application.");
        try
        {
            Assert.True(SpinWait.SpinUntil(() =>
            {
                process.Refresh();
                return process.HasExited || process.MainWindowHandle != IntPtr.Zero;
            }, TimeSpan.FromSeconds(5)));
            Assert.False(process.HasExited);
            Assert.NotEqual(IntPtr.Zero, process.MainWindowHandle);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
    }

    [Fact]
    public void Desktop_deployment_includes_solution_parser_runtime_dependency()
    {
        string assembly = Path.Combine(
            RepositoryRoot(),
            "src",
            "RhinoWorktreeLauncher",
            "bin",
            "Debug",
            "net8.0-windows",
            "win-x64",
            "Microsoft.VisualStudio.SolutionPersistence.dll");

        Assert.True(File.Exists(assembly), $"Missing desktop runtime dependency: {assembly}");
    }

    [Fact]
    public void Main_window_exposes_global_settings_and_project_config_without_selected_indicator()
    {
        XDocument document = LoadXaml("MainWindow.xaml");

        Assert.Equal("Settings", Named(document, "GlobalSettingsButton").Attribute("Content")?.Value);
        Assert.Equal("Config", Named(document, "ProjectConfigButton").Attribute("Content")?.Value);
        Assert.Null(FindNamed(document, "SelectedWorktreeText"));
        Assert.NotNull(FindNamed(document, "GlobalSettingsOverlay"));
    }

    [Fact]
    public void Project_config_exposes_canonical_build_selection_and_launch_mode_only()
    {
        XDocument document = LoadXaml("ProjectConfigDialog.xaml");

        Assert.NotNull(FindNamed(document, "PluginProjectComboBox"));
        Assert.NotNull(FindNamed(document, "SolutionComboBox"));
        Assert.NotNull(FindNamed(document, "BuildConfigurationComboBox"));
        Assert.NotNull(FindNamed(document, "BuildBeforeLaunchToggle"));
        Assert.Equal("0,15", FindNamed(document, "BuildBeforeLaunchToggle")?.Attribute("Padding")?.Value);
        Assert.NotNull(FindNamed(document, "ClearRemoteCacheToggle"));
        Assert.Null(FindNamed(document, "CustomDriverChoice"));
        Assert.Contains(document.Descendants(Presentation + "Button"), element =>
            string.Equals(element.Attribute("Content")?.Value, "Save config", StringComparison.Ordinal));
    }

    [Fact]
    public void Project_config_keeps_panels_above_the_fixed_footer()
    {
        XDocument document = LoadXaml("ProjectConfigDialog.xaml");
        XElement window = document.Root ?? throw new InvalidOperationException("Project config root is missing.");
        XElement contentScroller = Named(document, "ConfigContentScrollViewer");
        XElement footer = Named(document, "ConfigFooter");

        Assert.Equal("820", window.Attribute("Height")?.Value);
        Assert.Equal("0", contentScroller.Attribute("Grid.Row")?.Value);
        Assert.Equal("Auto", contentScroller.Attribute("VerticalScrollBarVisibility")?.Value);
        Assert.Equal("1", footer.Attribute("Grid.Row")?.Value);
    }

    [Fact]
    public void Dropdowns_share_the_handoff_component_standard()
    {
        XDocument application = LoadXaml("App.xaml");
        XDocument dropdowns = LoadXaml(Path.Combine("Themes", "DropdownStyles.xaml"));

        Assert.Contains(application.Descendants(Presentation + "ResourceDictionary"), element =>
            string.Equals(
                element.Attribute("Source")?.Value,
                "Themes/DropdownStyles.xaml",
                StringComparison.Ordinal));

        XElement comboStyle = Keyed(dropdowns, "Style", "BuildCombo");
        Assert.Contains(comboStyle.Elements(Presentation + "Setter"), setter =>
            string.Equals(setter.Attribute("Property")?.Value, "Height", StringComparison.Ordinal) &&
            string.Equals(setter.Attribute("Value")?.Value, "38", StringComparison.Ordinal));

        XElement itemStyle = Keyed(dropdowns, "Style", "DropdownItem");
        Assert.Contains(itemStyle.Elements(Presentation + "Setter"), setter =>
            string.Equals(setter.Attribute("Property")?.Value, "Height", StringComparison.Ordinal) &&
            string.Equals(setter.Attribute("Value")?.Value, "32", StringComparison.Ordinal));

        XElement popup = dropdowns.Descendants(Presentation + "Popup").Single();
        Assert.Equal("6", popup.Attribute("VerticalOffset")?.Value);
        Assert.Equal("False", popup.Attribute("StaysOpen")?.Value);
        Assert.NotNull(FindNamed(dropdowns, "DropdownCaret"));
        Assert.NotNull(FindNamed(dropdowns, "SelectedTexture"));
        Assert.NotNull(FindNamed(dropdowns, "SelectedCheck"));
        Assert.Equal(
            "{Binding PluginProjectPath}",
            Keyed(dropdowns, "DataTemplate", "PluginProjectDropdownValue")
                .Descendants(Presentation + "TextBlock")
                .Single()
                .Attribute("Text")?.Value);
        Assert.Equal(
            "{Binding SolutionPath}",
            Keyed(dropdowns, "DataTemplate", "SolutionDropdownValue")
                .Descendants(Presentation + "TextBlock")
                .Single()
                .Attribute("Text")?.Value);
        Assert.Equal(
            "{Binding DisplayName}",
            Keyed(dropdowns, "DataTemplate", "BuildConfigurationDropdownValue")
                .Descendants(Presentation + "TextBlock")
                .Single()
                .Attribute("Text")?.Value);
    }

    [Fact]
    public void Mcp_and_project_config_checkboxes_use_the_same_standard_style()
    {
        XDocument main = LoadXaml("MainWindow.xaml");
        XDocument config = LoadXaml("ProjectConfigDialog.xaml");

        string? mcpStyle = Named(main, "ClaudeSessionContextCheckBox").Attribute("Style")?.Value;
        string? configStyle = Named(config, "ClearRemoteCacheToggle").Attribute("Style")?.Value;

        Assert.Equal("{StaticResource StandardCheckBox}", mcpStyle);
        Assert.Equal(mcpStyle, configStyle);
    }

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

    private static XDocument LoadXaml(string fileName)
    {
        return XDocument.Load(Path.Combine(
            RepositoryRoot(),
            "src",
            "RhinoWorktreeLauncher",
            fileName));
    }

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
