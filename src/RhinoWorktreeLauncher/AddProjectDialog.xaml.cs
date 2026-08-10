using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace RhinoWorktreeLauncher;

public partial class AddProjectDialog : Window
{
    public AddProjectDialog(string projectPath, ProjectBuildOptions buildOptions)
    {
        InitializeComponent();
        ProjectPath = Path.GetFullPath(projectPath);
        ProjectPathText.Text = ProjectPath;
        PluginProjectComboBox.ItemsSource = buildOptions.Plugins;
        PluginProjectComboBox.SelectedIndex = buildOptions.Plugins.Count == 1 ? 0 : -1;
        Loaded += (_, _) => ApplyOwnerTheme();
    }

    public string ProjectPath { get; }
    public bool ReadRemote => RemoteReadToggle.IsChecked == true;
    public string PluginProjectPath => ((PluginBuildOptions)PluginProjectComboBox.SelectedItem).PluginProjectPath;
    public string SolutionPath => ((SolutionBuildOptions)SolutionComboBox.SelectedItem).SolutionPath;
    public BuildConfiguration BuildConfiguration =>
        (BuildConfiguration)BuildConfigurationComboBox.SelectedItem;

    private void PluginProject_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        BuildConfigurationComboBox.ItemsSource = null;
        if (PluginProjectComboBox.SelectedItem is not PluginBuildOptions plugin)
        {
            SolutionComboBox.ItemsSource = null;
            SolutionComboBox.IsEnabled = false;
            SolutionComboBox.Tag = "Select a plug-in project first";
            BuildConfigurationComboBox.IsEnabled = false;
            BuildConfigurationComboBox.Tag = "Select a solution first";
            return;
        }

        SolutionComboBox.ItemsSource = plugin.Solutions;
        SolutionComboBox.IsEnabled = plugin.Solutions.Count > 0;
        SolutionComboBox.Tag = plugin.Solutions.Count > 0
            ? "Select a solution"
            : "No solutions found";
        SolutionComboBox.SelectedIndex = plugin.Solutions.Count == 1 ? 0 : -1;
        ValidationText.Visibility = Visibility.Collapsed;
    }

    private void Solution_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SolutionComboBox.SelectedItem is not SolutionBuildOptions solution)
        {
            BuildConfigurationComboBox.ItemsSource = null;
            BuildConfigurationComboBox.IsEnabled = false;
            BuildConfigurationComboBox.Tag = "Select a solution first";
            return;
        }

        BuildConfigurationComboBox.ItemsSource = solution.Configurations;
        BuildConfigurationComboBox.IsEnabled = solution.Configurations.Count > 0;
        BuildConfigurationComboBox.Tag = solution.Configurations.Count > 0
            ? "Select a configuration"
            : "No configurations found";
        BuildConfigurationComboBox.SelectedItem = solution.Configurations.FirstOrDefault(configuration =>
            string.Equals(configuration.Configuration, "Debug", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(configuration.Platform, "x64", StringComparison.OrdinalIgnoreCase)) ??
            solution.Configurations.FirstOrDefault(configuration =>
                string.Equals(configuration.Configuration, "Debug", StringComparison.OrdinalIgnoreCase)) ??
            solution.Configurations.FirstOrDefault();
        ValidationText.Visibility = Visibility.Collapsed;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (PluginProjectComboBox.SelectedItem is null)
        {
            ShowValidation("Choose the Rhino plug-in project to launch.");
            return;
        }
        if (SolutionComboBox.SelectedItem is null)
        {
            ShowValidation("Choose the solution RWL should build.");
            return;
        }
        if (BuildConfigurationComboBox.SelectedItem is null)
        {
            ShowValidation("Choose a solution build configuration.");
            return;
        }

        DialogResult = true;
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ApplyOwnerTheme()
    {
        if (Owner is null)
            return;
        foreach (string key in new[]
        {
            "WindowBrush",
            "PanelBrush",
            "ControlBrush",
            "ControlHoverBrush",
            "PanelBorderBrush",
            "DividerBrush",
            "ControlBorderBrush",
            "ControlHoverBorderBrush",
            "TextStrongBrush",
            "TextBodyBrush",
            "TextSecondaryBrush",
            "TextMutedBrush",
            "TextFaintBrush",
            "AccentBrush",
            "RowHoverBrush",
            "RowActiveBrush",
            "PatternBrush",
            "DropdownMenuBrush",
            "DropdownMenuBorderBrush",
            "DropdownOpenBorderBrush",
            "DropdownDisabledBrush",
            "DropdownDisabledBorderBrush",
            "DropdownFocusRingBrush",
            "DropdownSelectedBorderBrush",
            "DropdownAccentBrush",
            "DropdownControlShadowEffect",
            "DropdownMenuShadowEffect",
            "PrimaryBrush",
            "PrimaryTextBrush"
        })
        {
            object? resource = Owner.TryFindResource(key);
            if (resource is not null)
                Resources[key] = resource;
        }
    }
}
