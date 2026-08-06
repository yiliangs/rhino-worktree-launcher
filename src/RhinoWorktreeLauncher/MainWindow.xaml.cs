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

    private readonly ObservableCollection<ProjectManifest> _projects =
        new ObservableCollection<ProjectManifest>();
    private readonly ObservableCollection<WorktreeEntry> _worktrees =
        new ObservableCollection<WorktreeEntry>();
    private readonly ProjectCatalog _catalog;
    private readonly GitWorktreeScanner _scanner = new GitWorktreeScanner();
    private readonly WorktreeLaunchService _launchService = new WorktreeLaunchService();
    private readonly DispatcherTimer _themeTimer;
    private ProjectManifest? _currentProject;
    private bool _isRefreshing;
    private bool _isClosing;
    private bool _isUpdatingProjects;
    private bool _isUpdatingWorktrees;
    private bool? _isLightTheme;
    private string _hint = "Double-click to launch";
    private string _repositoryPath = string.Empty;

    public MainWindow(ProjectCatalog catalog)
    {
        _catalog = catalog;
        InitializeComponent();
        ProjectList.ItemsSource = _projects;
        WorktreeList.ItemsSource = _worktrees;
        SourceInitialized += OnSourceInitialized;
        _themeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _themeTimer.Tick += (_, _) => ApplySystemTheme();
        ApplySystemTheme();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _isClosing = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _themeTimer.Stop();
        base.OnClosed(e);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _themeTimer.Start();
        await ReloadProjectsAsync(null);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync(fetchRemote: true);

    private async void ProjectList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isUpdatingProjects)
            return;

        ProjectManifest? project = ProjectList.SelectedItem as ProjectManifest;
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

        try
        {
            ProjectManifest project = _catalog.AddProject(dialog.FolderName);
            await ReloadProjectsAsync(project.ManifestPath);
        }
        catch (Exception ex)
        {
            _hint = "Project not supported";
            UpdateState();
            MessageBox.Show(
                this,
                ex.Message,
                "Project not supported",
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

    private void WorktreeList_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        if (WorktreeList.SelectedItem is WorktreeEntry worktree && worktree.CanLaunch)
            Launch(worktree);
    }

    private void WorktreeList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter ||
            WorktreeList.SelectedItem is not WorktreeEntry worktree ||
            !worktree.CanLaunch)
        {
            return;
        }

        Launch(worktree);
        e.Handled = true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (WorktreeList.SelectedItem is not WorktreeEntry worktree)
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            ArgumentList = { worktree.Path },
            UseShellExecute = true
        });
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (WorktreeList.SelectedItem is WorktreeEntry worktree)
            Launch(worktree);
    }

    private async Task ReloadProjectsAsync(string? selectedManifestPath)
    {
        IReadOnlyList<ProjectManifest> projects = _catalog.LoadProjects();
        _isUpdatingProjects = true;
        try
        {
            _projects.Clear();
            foreach (ProjectManifest project in projects)
                _projects.Add(project);

            _currentProject = _projects.FirstOrDefault(project => string.Equals(
                project.ManifestPath,
                selectedManifestPath,
                StringComparison.OrdinalIgnoreCase)) ?? _projects.FirstOrDefault();
            ProjectList.SelectedItem = _currentProject;
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

    private async Task SelectProjectAsync(ProjectManifest project)
    {
        _currentProject = project;
        _repositoryPath = project.RepositoryRoot;
        _worktrees.Clear();
        _hint = "Loading worktrees...";
        UpdateState();
        await RefreshAsync(fetchRemote: false);
    }

    private async Task RefreshAsync(bool fetchRemote)
    {
        if (_isRefreshing || _currentProject is null)
            return;

        _isRefreshing = true;
        string? selectedPath = (WorktreeList.SelectedItem as WorktreeEntry)?.Path;
        _hint = fetchRemote ? "Syncing repository..." : "Loading worktrees...";
        UpdateState();
        UpdateSync(active: true, local: 0, git: 0);

        try
        {
            ProjectManifest project = _currentProject;
            if (!fetchRemote)
            {
                IReadOnlyList<WorktreeEntry> fastEntries =
                    await Task.Run(() => _scanner.ScanFast(project));
                UpdateWorktrees(fastEntries, selectedPath);
                _hint = "Loading local details...";
                UpdateState();

                IReadOnlyList<WorktreeEntry> initialLocalEntries =
                    await Task.Run(() => _scanner.ScanLocal(project));
                UpdateWorktrees(initialLocalEntries, selectedPath);
                _hint = "Double-click to launch";
                UpdateSync(active: true, local: 1, git: 1);
                UpdateState();
                _ = EnrichInitialGitAsync(project, initialLocalEntries);
                return;
            }

            Task<IReadOnlyList<WorktreeEntry>> localTask =
                Task.Run(() => _scanner.ScanLocal(project));
            Task<GitSyncResult> gitTask =
                Task.Run(() => SynchronizeGit(project));

            IReadOnlyList<WorktreeEntry> localEntries = await localTask;
            UpdateWorktrees(localEntries, selectedPath);
            _hint = "Local scan complete; syncing Git...";
            UpdateState();
            UpdateSync(active: true, local: 1, git: 0);

            GitSyncResult gitResult = await gitTask;
            UpdateSync(active: true, local: 1, git: 1);

            IReadOnlyList<WorktreeEntry> enriched = await Task.Run(() =>
                _scanner.EnrichGit(project, localEntries, gitResult.PullRequests));
            UpdateWorktrees(enriched, selectedPath);
            _hint = gitResult.FetchSucceeded
                ? "Double-click to launch"
                : "Local data shown; Git sync unavailable";
            UpdateState();
            await Task.Delay(450);
        }
        catch (Exception ex)
        {
            _worktrees.Clear();
            _hint = ex.Message;
            UpdateState();
        }
        finally
        {
            _isRefreshing = false;
            UpdateSync(active: false, local: 0, git: 0);
            UpdateState();
        }
    }

    private async Task EnrichInitialGitAsync(
        ProjectManifest project,
        IReadOnlyList<WorktreeEntry> localEntries)
    {
        try
        {
            IReadOnlyList<WorktreeEntry> enriched = await Task.Run(() =>
            {
                IReadOnlyDictionary<string, PullRequestInfo> pullRequests =
                    _scanner.GetPullRequests(project);
                return _scanner.EnrichGit(project, localEntries, pullRequests);
            });
            if (_isClosing || _isRefreshing || !ReferenceEquals(_currentProject, project))
                return;

            string? selectedPath = (WorktreeList.SelectedItem as WorktreeEntry)?.Path;
            UpdateWorktrees(enriched, selectedPath);
            UpdateState();
        }
        catch
        {
            // Optional divergence enrichment must not delay or break local startup.
        }
    }

    private GitSyncResult SynchronizeGit(ProjectManifest project)
    {
        bool fetchSucceeded = true;
        try
        {
            _scanner.Fetch(project);
        }
        catch
        {
            fetchSucceeded = false;
        }

        IReadOnlyDictionary<string, PullRequestInfo> pullRequests =
            _scanner.GetPullRequests(project);
        return new GitSyncResult(fetchSucceeded, pullRequests);
    }

    private void UpdateWorktrees(
        IReadOnlyList<WorktreeEntry> entries,
        string? selectedPath)
    {
        _isUpdatingWorktrees = true;
        try
        {
            _worktrees.Clear();
            foreach (WorktreeEntry entry in entries)
                _worktrees.Add(entry);

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

    private void UpdateState()
    {
        WorktreeCountText.Text = _worktrees.Count.ToString(CultureInfo.InvariantCulture);
        PanelHintText.Text = _hint;
        EmptyStateText.Visibility = _worktrees.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateRepositoryPathText();
        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        WorktreeEntry? selected = WorktreeList.SelectedItem as WorktreeEntry;
        SelectedWorktreeText.Text = selected?.DisplayName ?? "No worktree selected";
        OpenFolderButton.IsEnabled = selected is not null;
        LaunchButton.IsEnabled = selected?.CanLaunch == true;
    }

    private void UpdateSync(bool active, double local, double git)
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

    private static void SetProgress(Border fill, double value)
    {
        fill.BeginAnimation(WidthProperty, null);
        if (value >= 1)
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

    private void Launch(WorktreeEntry worktree)
    {
        try
        {
            _launchService.Launch(worktree);
            _hint = worktree.IsPrimary
                ? "Normal Rhino started"
                : "Worktree launch started";
            UpdateState();
        }
        catch (Exception ex)
        {
            _hint = "Launch failed";
            UpdateState();
            MessageBox.Show(
                this,
                ex.Message,
                "Rhino launch failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private sealed record GitSyncResult(
        bool FetchSucceeded,
        IReadOnlyDictionary<string, PullRequestInfo> PullRequests);
}
