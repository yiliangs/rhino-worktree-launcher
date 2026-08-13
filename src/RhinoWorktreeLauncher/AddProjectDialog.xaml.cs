using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace RhinoWorktreeLauncher;

public partial class AddProjectDialog : Window
{
    private BuildSelectionState _buildSelection;
    private bool _renderingBuildSelection;

    public AddProjectDialog(string projectPath, ProjectBuildOptions buildOptions)
    {
        _buildSelection = BuildSelectionState.ForAdd(buildOptions);
        InitializeComponent();
        ProjectPath = Path.GetFullPath(projectPath);
        ProjectPathText.Text = ProjectPath;
        BuildConfigurationComboBox.SelectionChanged += BuildConfiguration_SelectionChanged;
        RenderBuildSelection();
        Loaded += (_, _) => ApplyOwnerTheme();
    }

    public string ProjectPath { get; }
    public bool ReadRemote => RemoteReadToggle.IsChecked == true;
    public string PluginProjectPath => _buildSelection.SelectedPlugin!.PluginProjectPath;
    public string SolutionPath => _buildSelection.SelectedSolution!.SolutionPath;
    public BuildConfiguration BuildConfiguration => _buildSelection.SelectedConfiguration!;

    private void PluginProject_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_renderingBuildSelection)
            return;

        PluginBuildOptions? plugin = PluginProjectComboBox.SelectedItem as PluginBuildOptions;
        _buildSelection = _buildSelection.SelectPlugin(plugin);
        RenderBuildSelection();
        if (plugin is not null)
            ValidationText.Visibility = Visibility.Collapsed;
    }

    private void Solution_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_renderingBuildSelection)
            return;

        SolutionBuildOptions? solution = SolutionComboBox.SelectedItem as SolutionBuildOptions;
        _buildSelection = _buildSelection.SelectSolution(solution);
        RenderBuildSelection();
        if (solution is not null)
            ValidationText.Visibility = Visibility.Collapsed;
    }

    private void BuildConfiguration_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_renderingBuildSelection)
            return;

        _buildSelection = _buildSelection.SelectConfiguration(
            BuildConfigurationComboBox.SelectedItem as BuildConfiguration);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_buildSelection.SelectedPlugin is null)
        {
            ShowValidation("Choose the Rhino plug-in project to launch.");
            return;
        }
        if (_buildSelection.SelectedSolution is null)
        {
            ShowValidation("Choose the solution RWL should build.");
            return;
        }
        if (_buildSelection.SelectedConfiguration is null)
        {
            ShowValidation("Choose a solution build configuration.");
            return;
        }

        DialogResult = true;
    }

    private void RenderBuildSelection()
    {
        _renderingBuildSelection = true;
        try
        {
            PluginProjectComboBox.ItemsSource = _buildSelection.Plugins;
            PluginProjectComboBox.IsEnabled = _buildSelection.PluginEnabled;
            PluginProjectComboBox.Tag = _buildSelection.PluginPlaceholder;
            PluginProjectComboBox.SelectedItem = _buildSelection.SelectedPlugin;

            SolutionComboBox.ItemsSource = _buildSelection.Solutions;
            SolutionComboBox.IsEnabled = _buildSelection.SolutionEnabled;
            SolutionComboBox.Tag = _buildSelection.SolutionPlaceholder;
            SolutionComboBox.SelectedItem = _buildSelection.SelectedSolution;

            BuildConfigurationComboBox.ItemsSource = _buildSelection.Configurations;
            BuildConfigurationComboBox.IsEnabled = _buildSelection.ConfigurationEnabled;
            BuildConfigurationComboBox.Tag = _buildSelection.ConfigurationPlaceholder;
            BuildConfigurationComboBox.SelectedItem = _buildSelection.SelectedConfiguration;
        }
        finally
        {
            _renderingBuildSelection = false;
        }
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
