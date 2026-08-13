using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace RhinoWorktreeLauncher;

public partial class ProjectConfigDialog : Window
{
    private readonly CancellationTokenSource _loadCancellation = new CancellationTokenSource();
    private readonly Func<CancellationToken, Task<CommandResult<ProjectBuildOptions>>> _loadBuildOptions;
    private readonly BuildProfile _savedProfile;
    private readonly string _projectPath;
    private BuildSelectionState? _buildSelection;
    private bool _renderingBuildSelection;

    public ProjectConfigDialog(
        ProjectRegistration registration,
        Func<CancellationToken, Task<CommandResult<ProjectBuildOptions>>> loadBuildOptions)
    {
        InitializeComponent();
        _loadBuildOptions = loadBuildOptions;
        _savedProfile = registration.BuildProfile;
        _projectPath = Path.GetFullPath(registration.PrimaryCheckout);
        BuildConfigurationComboBox.SelectionChanged += BuildConfiguration_SelectionChanged;

        ProjectNameText.Text = registration.DisplayName;
        ProjectInitialText.Text = string.IsNullOrWhiteSpace(registration.DisplayName)
            ? "?"
            : registration.DisplayName.Substring(0, 1).ToUpper(CultureInfo.CurrentCulture);
        ProjectPathText.ToolTip = _projectPath;
        RemoteReadToggle.IsChecked = registration.Access.ReadRemote;
        BuildBeforeLaunchToggle.IsChecked = registration.BuildProfile.LaunchMode == LaunchMode.BuildAndLaunch;

        Loaded += (_, _) =>
        {
            ApplyOwnerTheme();
            UpdateProjectPathText();
        };
        ContentRendered += LoadBuildOptions;
        Closed += (_, _) =>
        {
            _loadCancellation.Cancel();
            _loadCancellation.Dispose();
        };
    }

    public bool ReadRemote => RemoteReadToggle.IsChecked == true;
    public string PluginProjectPath => _buildSelection!.SelectedPlugin!.PluginProjectPath;
    public string SolutionPath => _buildSelection!.SelectedSolution!.SolutionPath;
    public BuildConfiguration BuildConfiguration => _buildSelection!.SelectedConfiguration!;
    public LaunchMode LaunchMode => BuildBeforeLaunchToggle.IsChecked == true
        ? LaunchMode.BuildAndLaunch
        : LaunchMode.DirectLaunch;
    public bool ClearRemoteCache => ClearRemoteCacheToggle.IsChecked == true;

    private async void LoadBuildOptions(object? sender, EventArgs e)
    {
        ContentRendered -= LoadBuildOptions;
        CommandResult<ProjectBuildOptions> result = await _loadBuildOptions(_loadCancellation.Token);
        if (!IsVisible)
            return;
        if (!result.Succeeded)
        {
            MessageBox.Show(
                this,
                result.Diagnostics[0].Message,
                "Project configuration unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            DialogResult = false;
            return;
        }

        ApplyBuildOptions(result.Value!);
    }

    private void ApplyBuildOptions(ProjectBuildOptions buildOptions)
    {
        _buildSelection = BuildSelectionState.ForConfig(buildOptions, _savedProfile);
        RenderBuildSelection();
        SaveConfigButton.IsEnabled = buildOptions.Plugins.Count > 0;
    }

    private void PluginProject_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_renderingBuildSelection || _buildSelection is null)
            return;

        PluginBuildOptions? plugin = PluginProjectComboBox.SelectedItem as PluginBuildOptions;
        _buildSelection = _buildSelection.SelectPlugin(plugin);
        RenderBuildSelection();
        if (plugin is not null)
            ValidationText.Visibility = Visibility.Collapsed;
    }

    private void Solution_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_renderingBuildSelection || _buildSelection is null)
            return;

        SolutionBuildOptions? solution = SolutionComboBox.SelectedItem as SolutionBuildOptions;
        _buildSelection = _buildSelection.SelectSolution(solution);
        RenderBuildSelection();
        if (solution is not null)
            ValidationText.Visibility = Visibility.Collapsed;
    }

    private void BuildConfiguration_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_renderingBuildSelection || _buildSelection is null)
            return;

        _buildSelection = _buildSelection.SelectConfiguration(
            BuildConfigurationComboBox.SelectedItem as BuildConfiguration);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_buildSelection?.IsComplete != true)
        {
            ValidationText.Text = "Choose a plug-in project, solution, and build configuration.";
            ValidationText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }

    private void RenderBuildSelection()
    {
        if (_buildSelection is null)
            return;

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

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ProjectPathText_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateProjectPathText();

    private void UpdateProjectPathText()
    {
        ProjectPathText.Text = TruncatePathFromStart(_projectPath, Math.Max(0, ProjectPathText.ActualWidth));
    }

    private string TruncatePathFromStart(string path, double availableWidth)
    {
        if (string.IsNullOrWhiteSpace(path) || availableWidth <= 0)
            return path;

        Typeface typeface = new Typeface(
            (FontFamily)Application.Current.FindResource("MonoFont"),
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double Measure(string value) => new FormattedText(
            value,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            11,
            Brushes.Black,
            pixelsPerDip).WidthIncludingTrailingWhitespace;

        if (Measure(path) <= availableWidth)
            return path;

        const string prefix = "…";
        int low = 0;
        int high = path.Length;
        while (low < high)
        {
            int length = (low + high + 1) / 2;
            string candidate = prefix + path.Substring(path.Length - length);
            if (Measure(candidate) <= availableWidth)
                low = length;
            else
                high = length - 1;
        }
        return prefix + path.Substring(path.Length - low);
    }

    private void ApplyOwnerTheme()
    {
        if (Owner is null)
            return;

        foreach (string key in new[]
        {
            "WindowBrush", "PanelBrush", "FooterBrush", "ControlBrush", "ControlHoverBrush", "TrackBrush", "PanelBorderBrush",
            "DividerBrush", "ControlBorderBrush", "ControlHoverBorderBrush", "BadgeBrush", "BadgeBorderBrush",
            "TextStrongBrush", "TextBodyBrush", "TextSecondaryBrush", "TextMutedBrush", "TextBadgeBrush",
            "ControlTextBrush", "AccentBrush", "PrimaryBrush", "PrimaryHoverBrush", "RowHoverBrush",
            "RowActiveBrush", "PatternBrush", "TextFaintBrush", "DropdownMenuBrush", "DropdownMenuBorderBrush",
            "DropdownOpenBorderBrush", "DropdownDisabledBrush", "DropdownDisabledBorderBrush",
            "DropdownFocusRingBrush", "DropdownSelectedBorderBrush", "DropdownAccentBrush",
            "PrimaryTextBrush", "ControlShadowEffect", "PrimaryShadowEffect",
            "DropdownControlShadowEffect", "DropdownMenuShadowEffect"
        })
        {
            object? resource = Owner.TryFindResource(key);
            if (resource is not null)
                Resources[key] = resource;
        }

        CopyOwnerResource("BehindTextBrush", "ValidationBrush");
        ApplyToggleTheme();
        Resources["LogoShadowEffect"] = CreateLogoShadow();
    }

    private void CopyOwnerResource(string ownerKey, string localKey)
    {
        object? resource = Owner?.TryFindResource(ownerKey);
        if (resource is not null)
            Resources[localKey] = resource;
    }

    private void ApplyToggleTheme()
    {
        bool isLight = ((SolidColorBrush)FindResource("WindowBrush")).Color.R > 128;
        Resources["ToggleOnBrush"] = CreateBrush(isLight ? "#6BA36F" : "#5F8A5C");
        Resources["ToggleOnBorderBrush"] = CreateBrush(isLight ? "#5D9161" : "#537A51");
        Resources["ToggleOffBrush"] = CreateBrush(isLight ? "#E2E5EA" : "#24272C");
        Resources["ToggleOffBorderBrush"] = CreateBrush(isLight ? "#D0D4DA" : "#31353B");
        Resources["ToggleKnobBrush"] = CreateBrush(isLight ? "#FFFFFF" : "#F0F2F5");
        Resources["ToggleKnobOffBrush"] = CreateBrush(isLight ? "#FFFFFF" : "#7D848D");
    }

    private static SolidColorBrush CreateBrush(string value) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;

    private DropShadowEffect CreateLogoShadow()
    {
        bool isLight = ((SolidColorBrush)FindResource("WindowBrush")).Color.R > 128;
        return new DropShadowEffect
        {
            BlurRadius = isLight ? 10 : 12,
            Direction = 270,
            ShadowDepth = isLight ? 3 : 4,
            Opacity = isLight ? 0.3 : 0.5,
            Color = isLight ? Color.FromRgb(24, 30, 40) : Colors.Black
        };
    }
}
