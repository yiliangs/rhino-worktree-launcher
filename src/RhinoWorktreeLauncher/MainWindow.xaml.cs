using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace RhinoWorktreeLauncher;

public partial class MainWindow : Window
{
    private const double DesignWindowWidth = 720;
    private const double DesignWindowHeight = 1000;
    private const int DwmExtendedFrameBounds = 9;

    // Where the launch bar stands when each stage begins working, plus how long that
    // stage is expected to take. The fill eases toward the next boundary over that
    // span, so a slow stage keeps moving without ever claiming the stage is finished.
    private static readonly IReadOnlyDictionary<LaunchStage, LaunchStageStep> LaunchSteps =
        new Dictionary<LaunchStage, LaunchStageStep>
        {
            [LaunchStage.Resolve] = new LaunchStageStep("RESOLVING", 0.06, 0.4),
            [LaunchStage.Prepare] = new LaunchStageStep("PREPARING", 0.14, 0.6),
            [LaunchStage.Build] = new LaunchStageStep("BUILDING", 0.62, 25),
            [LaunchStage.Artifact] = new LaunchStageStep("EXISTING BUILD", 0.62, 0.4),
            [LaunchStage.Registration] = new LaunchStageStep("REGISTERING", 0.70, 0.4),
            [LaunchStage.Rhino] = new LaunchStageStep("STARTING RHINO", 0.80, 1.5),
            [LaunchStage.Verify] = new LaunchStageStep("VERIFYING", 0.97, 30),
            [LaunchStage.Complete] = new LaunchStageStep("VERIFIED", 1, 0.3)
        };

    private static readonly IReadOnlyDictionary<string, string> DarkTheme =
        new Dictionary<string, string>
        {
            ["WindowBrush"] = "#16181B",
            ["PanelBrush"] = "#1A1C20",
            ["FooterBrush"] = "#121417",
            ["ControlBrush"] = "#1B1D21",
            ["ControlHoverBrush"] = "#22252A",
            ["ProgressBrush"] = "#247FAE7A",
            ["TagBrush"] = "#0D9AA8BB",
            ["BadgeBrush"] = "#1F2329",
            ["DiffBoxBrush"] = "#1E2125",
            ["RowHoverBrush"] = "#1F2226",
            ["RowActiveBrush"] = "#262B31",
            ["TrackBrush"] = "#1C1F23",
            ["TrackCenterBrush"] = "#3A4048",
            ["PanelBorderBrush"] = "#26292E",
            ["DividerBrush"] = "#232629",
            ["ControlBorderBrush"] = "#2E3238",
            ["ControlHoverBorderBrush"] = "#464C54",
            ["DropdownMenuBrush"] = "#202329",
            ["DropdownMenuBorderBrush"] = "#33383F",
            ["DropdownOpenBorderBrush"] = "#5A616B",
            ["DropdownDisabledBrush"] = "#191B1E",
            ["DropdownDisabledBorderBrush"] = "#25282C",
            ["DropdownFocusRingBrush"] = "#2E8FAE8B",
            ["DropdownSelectedBorderBrush"] = "#2B3037",
            ["DropdownAccentBrush"] = "#8FAE8B",
            ["TagBorderBrush"] = "#232629",
            ["BadgeBorderBrush"] = "#333941",
            ["DiffBoxBorderBrush"] = "#282C31",
            ["TextStrongBrush"] = "#F0F2F5",
            ["TextBodyBrush"] = "#E6E8EB",
            ["TextSecondaryBrush"] = "#878D95",
            ["TextMutedBrush"] = "#6C727A",
            ["TextFaintBrush"] = "#5D646C",
            ["TextBadgeBrush"] = "#A5AEBA",
            ["ControlTextBrush"] = "#B2B8C0",
            ["AccentBrush"] = "#7FAE7A",
            ["AheadTextBrush"] = "#7FAE7A",
            ["BehindTextBrush"] = "#C07D76",
            ["ZeroTextBrush"] = "#484E56",
            ["AheadFillBrush"] = "#5F8A5C",
            ["BehindFillBrush"] = "#9E5F59",
            ["FreshTextBrush"] = "#8FAE8B",
            ["FreshBackgroundBrush"] = "#127FAE7A",
            ["FreshBorderBrush"] = "#2C3A2F",
            ["StaleTextBrush"] = "#8A9099",
            ["StaleBackgroundBrush"] = "#0D9AA8BB",
            ["StaleBorderBrush"] = "#282C31",
            ["PatternBrush"] = "#1AB2C0D3",
            ["PrimaryBrush"] = "#F0F2F5",
            ["PrimaryHoverBrush"] = "#FFFFFF",
            ["PrimaryTextBrush"] = "#16181B",
            ["ScrollThumbBrush"] = "#343840",
            ["ControlHighlightBrush"] = "#08FFFFFF",
            ["ChipHighlightBrush"] = "#05FFFFFF"
        };

    private static readonly IReadOnlyDictionary<string, string> LightTheme =
        new Dictionary<string, string>
        {
            ["WindowBrush"] = "#F4F5F7",
            ["PanelBrush"] = "#FFFFFF",
            ["FooterBrush"] = "#EEF0F3",
            ["ControlBrush"] = "#FFFFFF",
            ["ControlHoverBrush"] = "#F7F8FA",
            ["ProgressBrush"] = "#296BA36F",
            ["TagBrush"] = "#0F5A697D",
            ["BadgeBrush"] = "#EEF0F3",
            ["DiffBoxBrush"] = "#F6F7F9",
            ["RowHoverBrush"] = "#F4F6F8",
            ["RowActiveBrush"] = "#E7EBF1",
            ["TrackBrush"] = "#EDEFF2",
            ["TrackCenterBrush"] = "#C9CED6",
            ["PanelBorderBrush"] = "#DFE2E7",
            ["DividerBrush"] = "#E0E3E7",
            ["ControlBorderBrush"] = "#D5D9DF",
            ["ControlHoverBorderBrush"] = "#B9BFC8",
            ["DropdownMenuBrush"] = "#FFFFFF",
            ["DropdownMenuBorderBrush"] = "#D5D9DF",
            ["DropdownOpenBorderBrush"] = "#9AA3AE",
            ["DropdownDisabledBrush"] = "#F1F2F4",
            ["DropdownDisabledBorderBrush"] = "#E2E5EA",
            ["DropdownFocusRingBrush"] = "#293F7A44",
            ["DropdownSelectedBorderBrush"] = "#C2CAD6",
            ["DropdownAccentBrush"] = "#3F7A44",
            ["TagBorderBrush"] = "#DDE0E5",
            ["BadgeBorderBrush"] = "#D7DBE1",
            ["DiffBoxBorderBrush"] = "#E2E5EA",
            ["TextStrongBrush"] = "#171B21",
            ["TextBodyBrush"] = "#2A2F36",
            ["TextSecondaryBrush"] = "#606770",
            ["TextMutedBrush"] = "#787F89",
            ["TextFaintBrush"] = "#8D949E",
            ["TextBadgeBrush"] = "#5B636D",
            ["ControlTextBrush"] = "#3C424A",
            ["AccentBrush"] = "#3F7A44",
            ["AheadTextBrush"] = "#3F7A44",
            ["BehindTextBrush"] = "#A5443C",
            ["ZeroTextBrush"] = "#B4BAC2",
            ["AheadFillBrush"] = "#6BA36F",
            ["BehindFillBrush"] = "#CD7D75",
            ["FreshTextBrush"] = "#3F7A44",
            ["FreshBackgroundBrush"] = "#1A6BA36F",
            ["FreshBorderBrush"] = "#C3DDC4",
            ["StaleTextBrush"] = "#6D747E",
            ["StaleBackgroundBrush"] = "#0F5A697D",
            ["StaleBorderBrush"] = "#DDE0E5",
            ["PatternBrush"] = "#24465A78",
            ["PrimaryBrush"] = "#1B1E23",
            ["PrimaryHoverBrush"] = "#000000",
            ["PrimaryTextBrush"] = "#F4F6F8",
            ["ScrollThumbBrush"] = "#C3C8D0",
            ["ControlHighlightBrush"] = "#99FFFFFF",
            ["ChipHighlightBrush"] = "#B3FFFFFF"
        };

    private readonly ObservableCollection<ProjectSnapshot> _projects =
        new ObservableCollection<ProjectSnapshot>();
    private readonly ObservableCollection<WorktreeSnapshot> _worktrees =
        new ObservableCollection<WorktreeSnapshot>();
    private readonly LauncherBackend _backend;
    private readonly McpClientIntegrationManager _integrationManager =
        new McpClientIntegrationManager();
    private readonly DispatcherTimer _themeTimer;
    private ProjectSnapshot? _currentProject;
    private CancellationTokenSource? _refreshCancellation;
    private int _refreshGeneration;
    private bool _isUpdatingProjects;
    private bool _isUpdatingWorktrees;
    private bool? _isLightTheme;
    private bool _isLaunching;
    private LaunchStage? _launchStage;
    private string _hint = string.Empty;
    private string _repositoryPath = string.Empty;

    public MainWindow(LauncherBackend backend)
    {
        _backend = backend;
        InitializeComponent();
        ProjectSelector.ItemsSource = _projects;
        WorktreeList.ItemsSource = _worktrees;
        SourceInitialized += OnSourceInitialized;
        _themeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _themeTimer.Tick += (_, _) => ApplySystemTheme();
        ApplySystemTheme();
    }

    protected override void OnClosed(EventArgs e)
    {
        _themeTimer.Stop();
        _refreshGeneration++;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = null;
        base.OnClosed(e);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _themeTimer.Start();
        await ReloadProjectsAsync(null);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync(fetchRemote: true);

    private async void GlobalSettings_Click(object sender, RoutedEventArgs e)
    {
        GlobalSettingsOverlay.Visibility = Visibility.Visible;
        await RefreshIntegrationStatusAsync();
    }

    private void GlobalSettingsClose_Click(object sender, RoutedEventArgs e) =>
        GlobalSettingsOverlay.Visibility = Visibility.Collapsed;

    private async void ConfigureClaude_Click(object sender, RoutedEventArgs e) =>
        await RunIntegrationActionAsync(async () =>
        {
            await _integrationManager.InstallAsync(
                McpClientKind.ClaudeCode,
                GetBootstrapPath(),
                installSessionContext: ClaudeSessionContextCheckBox.IsChecked == true,
                CancellationToken.None);
            McpSetupHintText.Text = "Claude Code configured. Restart Claude Code to load the MCP server.";
        });

    private async void RemoveClaude_Click(object sender, RoutedEventArgs e) =>
        await RunIntegrationActionAsync(async () =>
        {
            await _integrationManager.RemoveAsync(McpClientKind.ClaudeCode, CancellationToken.None);
            McpSetupHintText.Text = "Claude Code integration removed. RWL projects and application data were preserved.";
        });

    private async void ConfigureCodex_Click(object sender, RoutedEventArgs e) =>
        await RunIntegrationActionAsync(async () =>
        {
            await _integrationManager.InstallAsync(
                McpClientKind.Codex,
                GetBootstrapPath(),
                installSessionContext: false,
                CancellationToken.None);
            McpSetupHintText.Text = "Codex configured with a 300-second launch timeout. Restart Codex to load the MCP server.";
        });

    private async void RemoveCodex_Click(object sender, RoutedEventArgs e) =>
        await RunIntegrationActionAsync(async () =>
        {
            await _integrationManager.RemoveAsync(McpClientKind.Codex, CancellationToken.None);
            McpSetupHintText.Text = "Codex integration removed. RWL projects and application data were preserved.";
        });

    private async Task RunIntegrationActionAsync(Func<Task> action)
    {
        GlobalSettingsOverlay.IsHitTestVisible = false;
        try
        {
            await action();
            await RefreshIntegrationStatusAsync();
        }
        catch (Exception exception)
        {
            McpSetupHintText.Text = exception.Message;
            MessageBox.Show(
                this,
                exception.Message,
                "MCP setup could not be changed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            GlobalSettingsOverlay.IsHitTestVisible = true;
        }
    }

    private async Task RefreshIntegrationStatusAsync()
    {
        try
        {
            McpClientIntegrationStatus claude = await _integrationManager.GetStatusAsync(
                McpClientKind.ClaudeCode,
                CancellationToken.None);
            McpClientIntegrationStatus codex = await _integrationManager.GetStatusAsync(
                McpClientKind.Codex,
                CancellationToken.None);
            ClaudeIntegrationStatusText.Text = FormatIntegrationStatus(claude);
            CodexIntegrationStatusText.Text = FormatIntegrationStatus(codex);
            ClaudeSessionContextCheckBox.IsChecked = claude.McpConfigured
                ? claude.SessionContextConfigured
                : true;
        }
        catch (Exception exception)
        {
            McpSetupHintText.Text = exception.Message;
        }
    }

    private static string FormatIntegrationStatus(McpClientIntegrationStatus status)
    {
        if (!status.McpConfigured)
            return "Not configured";
        if (!status.BootstrapAvailable)
            return "Configured, but the RWL bootstrap is missing";
        if (status.SessionContextSupported)
        {
            return status.SessionContextConfigured
                ? "Ready; exact-worktree session context enabled"
                : "Ready; session context disabled";
        }
        return "Ready; launch timeout set to 300 seconds";
    }

    private static string GetBootstrapPath() =>
        Environment.GetEnvironmentVariable("RWL_BOOTSTRAP_PATH") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RhinoWorktreeLauncher",
            "bootstrap",
            "rwl.exe");

    private async void ProjectSelector_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isUpdatingProjects)
            return;

        ProjectSnapshot? project = ProjectSelector.SelectedItem as ProjectSnapshot;
        if (project is null || ReferenceEquals(project, _currentProject))
            return;

        await SelectProjectAsync(project);
    }

    private async void AddProject_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new OpenFolderDialog
        {
            Title = "Select a Rhino plug-in repository",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        CommandResult<ProjectBuildOptions> options = await _backend.DiscoverProjectBuildOptionsAsync(
            dialog.FolderName,
            CancellationToken.None);
        if (!options.Succeeded)
        {
            MessageBox.Show(
                this,
                options.Diagnostics[0].Message,
                "Project configuration required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        AddProjectDialog consent = new AddProjectDialog(dialog.FolderName, options.Value!)
        {
            Owner = this
        };
        if (consent.ShowDialog() != true)
            return;

        try
        {
            CommandResult<ProjectRegistration> result = await _backend.RegisterProjectAsync(
                new ProjectRegistrationRequest(
                    consent.ProjectPath,
                    new ProjectAccessGrant(ReadProject: true, ReadRemote: consent.ReadRemote),
                    consent.PluginProjectPath,
                    consent.SolutionPath,
                    consent.BuildConfiguration,
                    LaunchMode.BuildAndLaunch),
                CancellationToken.None);
            if (!result.Succeeded)
                throw new InvalidOperationException(result.Diagnostics[0].Message);

            await ReloadProjectsAsync(result.Value!.ProjectId);
        }
        catch (Exception ex)
        {
            _hint = "Project could not be added";
            UpdateState();
            MessageBox.Show(
                this,
                ex.Message,
                "Project could not be added",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void ProjectConfig_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null)
            return;

        ProjectRegistration registration = _currentProject.Registration;
        ProjectConfigDialog config = new ProjectConfigDialog(
            registration,
            cancellationToken => _backend.DiscoverProjectBuildOptionsAsync(
                registration.PrimaryCheckout,
                cancellationToken))
        {
            Owner = this
        };
        if (config.ShowDialog() != true)
            return;

        try
        {
            CommandResult<ProjectRegistration> result = await _backend.UpdateProjectConfigAsync(
                new ProjectConfigRequest(
                    _currentProject.ProjectId,
                    config.ReadRemote,
                    config.PluginProjectPath,
                    config.SolutionPath,
                    config.BuildConfiguration,
                    config.LaunchMode),
                CancellationToken.None);
            if (!result.Succeeded)
                throw new InvalidOperationException(result.Diagnostics[0].Message);

            if (config.ClearRemoteCache)
            {
                CommandResult<bool> cleared = await _backend.ClearRemoteCacheAsync(
                    _currentProject.ProjectId,
                    CancellationToken.None);
                if (!cleared.Succeeded)
                    throw new InvalidOperationException(cleared.Diagnostics[0].Message);
            }

            await ReloadProjectsAsync(result.Value!.ProjectId);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "Project config could not be saved",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void WorktreeList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_isUpdatingWorktrees)
            UpdateSelectionState();
    }

    private void WorktreeList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter ||
            WorktreeList.SelectedItem is not WorktreeSnapshot worktree ||
            !worktree.HasBuildConfiguration)
        {
            return;
        }

        Launch(worktree);
        e.Handled = true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (WorktreeList.SelectedItem is not WorktreeSnapshot worktree)
            return;

        _ = Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            ArgumentList = { worktree.Path },
            UseShellExecute = true
        });
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (WorktreeList.SelectedItem is WorktreeSnapshot worktree)
            Launch(worktree);
    }

    private async Task ReloadProjectsAsync(string? selectedProjectId)
    {
        CommandResult<IReadOnlyList<ProjectSnapshot>> result = await _backend.GetProjectsAsync(
            CancellationToken.None);
        IReadOnlyList<ProjectSnapshot> projects = result.Value ?? Array.Empty<ProjectSnapshot>();
        _isUpdatingProjects = true;
        try
        {
            _projects.Clear();
            foreach (ProjectSnapshot project in projects)
                _projects.Add(project);

            _currentProject = _projects.FirstOrDefault(project => string.Equals(
                project.ProjectId,
                selectedProjectId,
                StringComparison.OrdinalIgnoreCase)) ?? _projects.FirstOrDefault();
            ProjectSelector.SelectedItem = _currentProject;
        }
        finally
        {
            _isUpdatingProjects = false;
        }

        if (_currentProject is null)
        {
            _repositoryPath = string.Empty;
            _worktrees.Clear();
            _hint = "No projects registered";
            UpdateState();
            return;
        }

        await SelectProjectAsync(_currentProject);
    }

    private async Task SelectProjectAsync(ProjectSnapshot project)
    {
        _currentProject = project;
        _repositoryPath = project.Registration.PrimaryCheckout;
        _worktrees.Clear();
        _hint = "Loading worktrees...";
        UpdateState();
        await RefreshAsync(fetchRemote: false);
    }

    private async Task RefreshAsync(bool fetchRemote)
    {
        if (_currentProject is null)
            return;

        ProjectSnapshot project = _currentProject;
        int generation = ++_refreshGeneration;
        CancellationTokenSource cancellation = new CancellationTokenSource();
        CancellationTokenSource? previousCancellation = _refreshCancellation;
        _refreshCancellation = cancellation;
        previousCancellation?.Cancel();
        previousCancellation?.Dispose();
        string? selectedPath = (WorktreeList.SelectedItem as WorktreeSnapshot)?.Path;
        _hint = fetchRemote ? "Syncing repository..." : "Loading worktrees...";
        UpdateState();
        UpdateSync(active: true, local: 0, git: null);

        try
        {
            DispatcherProgress<WorktreeRefreshProgress> progress = new DispatcherProgress<WorktreeRefreshProgress>(
                Dispatcher,
                update =>
                {
                    if (!IsCurrentRefresh(generation, project.ProjectId))
                        return;

                    string? currentSelection = (WorktreeList.SelectedItem as WorktreeSnapshot)?.Path ??
                        selectedPath;
                    _ = UpdateWorktreesIfChanged(update.Worktrees.Worktrees, currentSelection);
                    if (update.Stage == WorktreeRefreshStage.LocalList)
                    {
                        UpdateSync(active: true, local: 0, git: null);
                        _hint = "Reading local state...";
                    }
                    else if (update.Stage == WorktreeRefreshStage.Local)
                    {
                        UpdateSync(active: true, local: 1, git: fetchRemote ? 0 : null);
                        _hint = fetchRemote ? "Syncing remote metadata..." : string.Empty;
                    }
                    else
                    {
                        UpdateSync(active: true, local: 1, git: 1);
                    }
                    UpdateState();
                });
            CommandResult<ProjectWorktrees> result = await _backend.GetWorktreeSnapshotAsync(
                project.ProjectId,
                fetchRemote,
                progress,
                cancellation.Token);
            if (!IsCurrentRefresh(generation, project.ProjectId))
                return;
            if (!result.Succeeded || result.Value is null)
                throw new InvalidOperationException(result.Diagnostics[0].Message);

            string? currentSelection = (WorktreeList.SelectedItem as WorktreeSnapshot)?.Path ??
                selectedPath;
            _ = UpdateWorktreesIfChanged(result.Value.Worktrees, currentSelection);
            UpdateSync(active: true, local: 1, git: fetchRemote ? 1 : null);
            _hint = result.Diagnostics.Count == 0
                ? string.Empty
                : "Local data shown; remote enrichment unavailable";
            UpdateState();
            if (fetchRemote)
                await Task.Delay(450, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentRefresh(generation, project.ProjectId))
                return;
            _worktrees.Clear();
            _hint = ex.Message;
            UpdateState();
        }
        finally
        {
            if (IsCurrentRefresh(generation, project.ProjectId))
            {
                _refreshCancellation = null;
                UpdateSync(active: false, local: 0, git: 0);
                UpdateState();
            }
            cancellation.Dispose();
        }
    }

    private bool IsCurrentRefresh(int generation, string projectId) =>
        generation == _refreshGeneration &&
        string.Equals(_currentProject?.ProjectId, projectId, StringComparison.OrdinalIgnoreCase);

    private bool UpdateWorktreesIfChanged(
        IReadOnlyList<WorktreeSnapshot> entries,
        string? selectedPath)
    {
        if (_worktrees.SequenceEqual(entries))
            return false;

        UpdateWorktrees(entries, selectedPath);
        return true;
    }

    private void UpdateWorktrees(
        IReadOnlyList<WorktreeSnapshot> entries,
        string? selectedPath)
    {
        _isUpdatingWorktrees = true;
        try
        {
            for (int index = 0; index < entries.Count; index++)
            {
                WorktreeSnapshot entry = entries[index];
                int existingIndex = IndexOfWorktree(entry.Path, index);
                if (existingIndex < 0)
                {
                    _worktrees.Insert(index, entry);
                }
                else
                {
                    if (existingIndex != index)
                        _worktrees.Move(existingIndex, index);
                    if (!_worktrees[index].Equals(entry))
                        _worktrees[index] = entry;
                }
            }
            while (_worktrees.Count > entries.Count)
                _worktrees.RemoveAt(_worktrees.Count - 1);

            WorktreeList.SelectedItem = _worktrees.FirstOrDefault(entry => string.Equals(
                entry.Path,
                selectedPath,
                StringComparison.OrdinalIgnoreCase)) ??
                _worktrees.FirstOrDefault(entry => entry.IsPrimary) ??
                _worktrees.FirstOrDefault();
        }
        finally
        {
            _isUpdatingWorktrees = false;
        }

        UpdateSelectionState();
    }

    private int IndexOfWorktree(string path, int startIndex)
    {
        for (int index = startIndex; index < _worktrees.Count; index++)
        {
            if (string.Equals(_worktrees[index].Path, path, StringComparison.OrdinalIgnoreCase))
                return index;
        }
        return -1;
    }

    private void UpdateState()
    {
        WorktreeCountText.Text = _worktrees.Count.ToString(CultureInfo.InvariantCulture);
        ProjectSelector.IsEnabled = _projects.Count > 0;
        ProjectConfigButton.IsEnabled = _currentProject is not null;
        ShowHint();
        EmptyStateText.Visibility = _worktrees.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateRepositoryPathText();
        UpdateSelectionState();
    }

    private void ShowHint()
    {
        PanelHintText.Text = _hint;
        PanelHintBanner.Visibility = HintVisibility(_hint, _isLaunching);
    }

    // The banner floats over the rows, so a report with nothing to say has to leave
    // the list alone rather than sit there empty. A launch is the exception: the sweep
    // is the progress indicator, and it cannot wait for the first message to arrive.
    private static Visibility HintVisibility(string hint, bool isLaunching) =>
        isLaunching || !string.IsNullOrWhiteSpace(hint)
            ? Visibility.Visible
            : Visibility.Collapsed;

    // The banner is the launch progress track, and its width follows the panel rather
    // than a fixed button, so the sweep measures it instead of trusting a constant.
    private double LaunchTrackWidth => Math.Max(0, PanelHintBanner.ActualWidth - 2);

    private void PanelHintBannerClip_SizeChanged(object sender, SizeChangedEventArgs e) =>
        PanelHintBannerClip.Clip = BannerClip(e.NewSize.Width, e.NewSize.Height);

    // The fill is a rectangle at both ends, so the banner does the rounding. Its radius
    // is the 8 the banner is drawn with, less the 1px border the clip sits inside.
    private static RectangleGeometry BannerClip(double width, double height) =>
        new RectangleGeometry(new Rect(0, 0, width, height), 7, 7);

    private void UpdateSelectionState()
    {
        WorktreeSnapshot? selected = WorktreeList.SelectedItem as WorktreeSnapshot;
        OpenFolderButton.IsEnabled = selected is not null;
        LaunchButton.IsEnabled = CanLaunch(_isLaunching, selected?.HasBuildConfiguration == true);
        LaunchButtonText.Text = selected is null || selected.LaunchMode == LaunchMode.DirectLaunch
            ? "Launch Rhino"
            : "Build & Launch";
    }

    // Progress reports in the banner, so the button can simply say it is unavailable
    // rather than stay lit and refuse the click.
    private static bool CanLaunch(bool isLaunching, bool hasBuildConfiguration) =>
        !isLaunching && hasBuildConfiguration;

    private void UpdateSync(bool active, double local, double? git)
    {
        RefreshButton.IsHitTestVisible = !active;
        RefreshButton.Cursor = active ? Cursors.Arrow : Cursors.Hand;
        RefreshIdle.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        RefreshSync.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

        if (!active)
        {
            StopProgress(LocalProgressFill);
            StopProgress(GitProgressFill);
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            SetProgress(LocalProgressFill, local);
            SetProgress(GitProgressFill, git);
        }, DispatcherPriority.Loaded);
    }

    private static void SetProgress(Border fill, double? value)
    {
        fill.BeginAnimation(WidthProperty, null);
        if (!value.HasValue)
        {
            fill.Width = 0;
            return;
        }
        if (value.Value >= 1)
        {
            fill.Width = (fill.Parent as FrameworkElement)?.ActualWidth ?? 0;
            return;
        }

        double width = (fill.Parent as FrameworkElement)?.ActualWidth ?? 0;
        DoubleAnimation crawl = new DoubleAnimation
        {
            From = 0,
            To = width * 0.72,
            Duration = TimeSpan.FromSeconds(4),
            RepeatBehavior = RepeatBehavior.Forever
        };
        fill.BeginAnimation(WidthProperty, crawl);
    }

    private static void StopProgress(Border fill)
    {
        fill.BeginAnimation(WidthProperty, null);
        fill.Width = 0;
    }

    private async void Launch(WorktreeSnapshot worktree)
    {
        if (_isLaunching)
            return;

        BeginLaunchProgress();
        string? failure = null;
        try
        {
            DispatcherProgress<LaunchProgress> progress = new DispatcherProgress<LaunchProgress>(
                Dispatcher,
                update =>
                {
                    ShowLaunchStage(update.Stage);
                    // The build stage reports one update per MSBuild line, so the detail
                    // text is set directly rather than through a full state refresh.
                    _hint = update.Message;
                    ShowHint();
                });
            CommandResult<LaunchResult> result = await _backend.LaunchAsync(
                worktree.Path,
                worktree.LaunchMode,
                TimeSpan.FromMinutes(3),
                progress,
                CancellationToken.None);
            failure = result.Succeeded ? null : result.Diagnostics[0].Message;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }
        finally
        {
            EndLaunchProgress();
        }

        _hint = failure is null ? "Selected plug-in verified in Rhino" : "Launch failed";
        UpdateState();
        if (failure is not null)
        {
            MessageBox.Show(
                this,
                failure,
                "Rhino launch failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void BeginLaunchProgress()
    {
        _isLaunching = true;
        _launchStage = null;
        LaunchStageText.Text = "STARTING";
        LaunchStageText.Visibility = Visibility.Visible;
        UpdateSelectionState();
        ShowHint();
        // The banner is collapsed between launches, so it has no measured width for the
        // sweep to cross until this layout pass has run.
        PanelHintBanner.UpdateLayout();
        LaunchProgressFill.BeginAnimation(WidthProperty, null);
        LaunchProgressFill.Width = 0;
    }

    private void ShowLaunchStage(LaunchStage stage)
    {
        if (_launchStage == stage || !LaunchSteps.TryGetValue(stage, out LaunchStageStep step))
            return;

        _launchStage = stage;
        LaunchStageText.Text = step.Caption;
        DoubleAnimation advance = new DoubleAnimation
        {
            From = LaunchProgressFill.ActualWidth,
            To = LaunchTrackWidth * step.Target,
            Duration = TimeSpan.FromSeconds(step.Seconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        LaunchProgressFill.BeginAnimation(WidthProperty, advance);
    }

    private void EndLaunchProgress()
    {
        _isLaunching = false;
        _launchStage = null;
        LaunchProgressFill.BeginAnimation(WidthProperty, null);
        LaunchProgressFill.Width = 0;
        LaunchStageText.Text = string.Empty;
        LaunchStageText.Visibility = Visibility.Collapsed;
        UpdateSelectionState();
        ShowHint();
    }

    private void RepositoryPathText_SizeChanged(
        object sender,
        SizeChangedEventArgs e) => UpdateRepositoryPathText();

    private void UpdateRepositoryPathText()
    {
        RepositoryPathText.ToolTip = string.IsNullOrWhiteSpace(_repositoryPath)
            ? null
            : _repositoryPath;
        RepositoryPathText.Text = TruncatePathFromStart(
            _repositoryPath,
            Math.Max(0, RepositoryPathText.ActualWidth - 32));
    }

    private static string TruncatePathFromStart(string path, double availableWidth)
    {
        if (string.IsNullOrWhiteSpace(path) || availableWidth <= 0)
            return path;

        Typeface typeface = new Typeface(
            (FontFamily)Application.Current.FindResource("MonoFont"),
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);
        double pixelsPerDip = VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip;

        double Measure(string value) => new FormattedText(
            value,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            10.5,
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

    private void ApplySystemTheme()
    {
        bool isLight = IsSystemLightTheme();
        if (_isLightTheme == isLight)
            return;

        _isLightTheme = isLight;
        IReadOnlyDictionary<string, string> palette =
            isLight ? LightTheme : DarkTheme;
        foreach ((string key, string value) in palette)
            Resources[key] = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(value));

        Resources["ControlShadowEffect"] = isLight
            ? CreateShadow(3, 1, 0.07, Color.FromRgb(24, 30, 40))
            : CreateShadow(3, 1, 0.30, Colors.Black);
        Resources["DropdownControlShadowEffect"] = isLight
            ? CreateShadow(2, 1, 0.08, Color.FromRgb(24, 30, 40))
            : CreateShadow(2, 1, 0.30, Colors.Black);
        Resources["DropdownMenuShadowEffect"] = isLight
            ? CreateShadow(18, 6, 0.22, Color.FromRgb(24, 30, 40))
            : CreateShadow(20, 8, 0.60, Colors.Black);
        Resources["ChipShadowEffect"] = isLight
            ? CreateShadow(2, 1, 0.05, Color.FromRgb(24, 30, 40))
            : CreateShadow(2, 1, 0.30, Colors.Black);
        Resources["RowActiveShadowEffect"] = isLight
            ? CreateShadow(2, 1, 0.06, Color.FromRgb(24, 30, 40))
            : CreateShadow(8, 2, 0.65, Colors.Black);
        Resources["PrimaryShadowEffect"] = isLight
            ? CreateShadow(10, 2, 0.14, Color.FromRgb(24, 30, 40))
            : CreateShadow(10, 2, 0.18, Color.FromRgb(200, 210, 225));

        ApplyWindowChrome();
    }

    private static DropShadowEffect CreateShadow(
        double blurRadius,
        double shadowDepth,
        double opacity,
        Color color) => new DropShadowEffect
        {
            BlurRadius = blurRadius,
            Direction = 270,
            ShadowDepth = shadowDepth,
            Opacity = opacity,
            Color = color
        };

    private static bool IsSystemLightTheme()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        FitVisibleFrameToDesignSize();
        ApplyWindowChrome();
    }

    private void FitVisibleFrameToDesignSize()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero ||
            DwmGetWindowAttribute(
                handle,
                DwmExtendedFrameBounds,
                out NativeRect frame,
                Marshal.SizeOf<NativeRect>()) != 0)
        {
            return;
        }

        double scale = GetDpiForWindow(handle) / 96d;
        if (scale <= 0)
            return;

        double visibleWidth = (frame.Right - frame.Left) / scale;
        double visibleHeight = (frame.Bottom - frame.Top) / scale;
        Width += DesignWindowWidth - visibleWidth;
        Height += DesignWindowHeight - visibleHeight;
    }

    private void ApplyWindowChrome()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        int dark = _isLightTheme == true ? 0 : 1;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int value,
        int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr window,
        int attribute,
        out NativeRect value,
        int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    private readonly record struct LaunchStageStep(string Caption, double Target, double Seconds);

    private sealed class DispatcherProgress<T> : IProgress<T>
    {
        private readonly Dispatcher _dispatcher;
        private readonly Action<T> _report;

        public DispatcherProgress(Dispatcher dispatcher, Action<T> report)
        {
            _dispatcher = dispatcher;
            _report = report;
        }

        public void Report(T value)
        {
            if (_dispatcher.CheckAccess())
            {
                _report(value);
                return;
            }

            _dispatcher.Invoke(() => _report(value));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

}
