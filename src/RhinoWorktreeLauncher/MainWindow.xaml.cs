using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DrawingColor = System.Drawing.Color;

namespace RhinoWorktreeLauncher;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly ProjectCatalog _catalog;
    private readonly GitWorktreeScanner _scanner = new GitWorktreeScanner();
    private readonly WorktreeLaunchService _launchService = new WorktreeLaunchService();
    private readonly LauncherSnapshotStore _snapshotStore = new LauncherSnapshotStore();
    private readonly DispatcherTimer _themeTimer;
    private IReadOnlyList<ProjectManifest> _projects = Array.Empty<ProjectManifest>();
    private IReadOnlyList<WorktreeEntry> _worktrees = Array.Empty<WorktreeEntry>();
    private ProjectManifest? _currentProject;
    private string? _selectedPath;
    private string _hint = "Double-click to launch";
    private bool _isRefreshing;
    private bool _webReady;
    private bool _isClosing;
    private bool? _isLightTheme;

    public MainWindow(ProjectCatalog catalog)
    {
        _catalog = catalog;
        InitializeComponent();
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
        Browser.Dispose();
        base.OnClosed(e);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _themeTimer.Start();
        await InitializeWebAsync();
    }

    private async Task InitializeWebAsync()
    {
        try
        {
            LauncherStoragePaths.EnsureDataRoot();
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: LauncherStoragePaths.WebViewUserDataFolder);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
#if !DEBUG
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif
            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "launcher.local",
                AppContext.BaseDirectory,
                CoreWebView2HostResourceAccessKind.Allow);
            Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            Browser.Source = new Uri("https://launcher.local/Web/index.html");
            ApplySystemTheme();
        }
        catch (Exception ex)
        {
            if (_isClosing)
                return;

            MessageBox.Show(
                this,
                $"The launcher web interface could not start.\n\n{ex.Message}",
                "Rhino Worktree Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    private void OnNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_isClosing)
            return;
        if (!e.IsSuccess)
        {
            MessageBox.Show(
                this,
                $"The launcher web interface could not load.\n\n{e.WebErrorStatus}",
                "Rhino Worktree Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
            return;
        }

        Browser.Visibility = Visibility.Visible;
        LoadingSurface.Visibility = Visibility.Collapsed;
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        WebCommand? command;
        try
        {
            command = JsonSerializer.Deserialize<WebCommand>(e.WebMessageAsJson, JsonOptions);
        }
        catch (JsonException)
        {
            return;
        }
        if (command is null)
            return;

        switch (command.Type)
        {
            case "ready":
                _webReady = true;
                SendTheme();
                LauncherSnapshotDto? snapshot = _snapshotStore.Load();
                ReloadProjects(snapshot?.CurrentManifestPath, sendState: false);
                bool restoredSnapshot = snapshot is not null && string.Equals(
                    snapshot.CurrentManifestPath,
                    _currentProject?.ManifestPath,
                    StringComparison.OrdinalIgnoreCase);
                if (restoredSnapshot)
                {
                    _selectedPath = snapshot!.SelectedPath;
                    SendSnapshot(snapshot!);
                }
                else
                {
                    SendState();
                }
                _ = RefreshAsync(fetchRemote: false, preserveDisplayedState: restoredSnapshot);
                break;
            case "refresh":
                await RefreshAsync(fetchRemote: true);
                break;
            case "select-project":
                await SelectProjectAsync(command.ManifestPath);
                break;
            case "select":
                SelectWorktree(command.Path);
                break;
            case "add-project":
                await AddProjectAsync();
                break;
            case "open-folder":
                OpenFolder(command.Path);
                break;
            case "launch":
                Launch(command.Path);
                break;
        }
    }

    private void ReloadProjects(string? selectedManifestPath, bool sendState = true)
    {
        _projects = _catalog.LoadProjects();
        _currentProject = _projects.FirstOrDefault(project => string.Equals(
            project.ManifestPath,
            selectedManifestPath,
            StringComparison.OrdinalIgnoreCase)) ?? _projects.FirstOrDefault();
        _worktrees = Array.Empty<WorktreeEntry>();
        _selectedPath = null;
        _hint = _currentProject is null ? "No projects registered" : "Loading worktrees...";
        if (sendState)
            SendState();
    }

    private async Task SelectProjectAsync(string? manifestPath)
    {
        ProjectManifest? project = _projects.FirstOrDefault(candidate => string.Equals(
            candidate.ManifestPath,
            manifestPath,
            StringComparison.OrdinalIgnoreCase));
        if (project is null)
            return;

        _currentProject = project;
        _worktrees = Array.Empty<WorktreeEntry>();
        _selectedPath = null;
        _hint = "Loading worktrees...";
        SendState();
        await RefreshAsync(fetchRemote: false);
    }

    private void SelectWorktree(string? path)
    {
        if (_worktrees.Any(worktree => string.Equals(worktree.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedPath = path;
            SendState();
            SaveSnapshot();
        }
    }

    private async Task AddProjectAsync()
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
            ReloadProjects(project.ManifestPath);
            await RefreshAsync(fetchRemote: false);
        }
        catch (Exception ex)
        {
            _hint = "Project not supported";
            SendState();
            MessageBox.Show(this, ex.Message, "Project not supported", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RefreshAsync(bool fetchRemote, bool preserveDisplayedState = false)
    {
        if (_isRefreshing || _currentProject is null)
            return;

        _isRefreshing = true;
        string? selectedPath = _selectedPath;
        _hint = fetchRemote ? "Syncing repository..." : "Loading worktrees...";
        if (!preserveDisplayedState)
            SendState();
        SendSync(active: true, local: 0, git: 0);

        try
        {
            ProjectManifest project = _currentProject;
            if (!fetchRemote)
            {
                if (!preserveDisplayedState)
                {
                    _worktrees = await Task.Run(() => _scanner.ScanFast(project));
                    _selectedPath = ResolveSelection(_worktrees, selectedPath);
                    _hint = "Loading local details...";
                    SendState();
                }

                _worktrees = await Task.Run(() => _scanner.ScanLocal(project));
                _selectedPath = ResolveSelection(_worktrees, selectedPath);
                _hint = "Double-click to launch";
                SendSync(active: true, local: 1, git: 1);
                SendState();
                SaveSnapshot();
                _ = EnrichInitialGitAsync(project, _worktrees);
                return;
            }

            Task<IReadOnlyList<WorktreeEntry>> localTask = Task.Run(() => _scanner.ScanLocal(project));
            Task<GitSyncResult> gitTask = Task.Run(() => SynchronizeGit(project));

            IReadOnlyList<WorktreeEntry> localEntries = await localTask;
            _worktrees = localEntries;
            _selectedPath = ResolveSelection(_worktrees, selectedPath);
            _hint = "Local scan complete; syncing Git...";
            SendState();
            SendSync(active: true, local: 1, git: 0);

            GitSyncResult gitResult = await gitTask;
            SendSync(active: true, local: 1, git: 1);

            _worktrees = await Task.Run(() => _scanner.EnrichGit(project, localEntries, gitResult.PullRequests));
            _selectedPath = ResolveSelection(_worktrees, selectedPath);
            _hint = gitResult.FetchSucceeded
                ? "Double-click to launch"
                : "Local data shown; Git sync unavailable";
            SendState();
            SaveSnapshot();
            await Task.Delay(450);
        }
        catch (Exception ex)
        {
            _worktrees = Array.Empty<WorktreeEntry>();
            _selectedPath = null;
            _hint = ex.Message;
            SendState();
        }
        finally
        {
            _isRefreshing = false;
            SendSync(active: false, local: 0, git: 0);
            SendState();
        }
    }

    private async Task EnrichInitialGitAsync(
        ProjectManifest project,
        IReadOnlyList<WorktreeEntry> localEntries)
    {
        try
        {
            IReadOnlyList<WorktreeEntry> enriched = await Task.Run(() =>
                _scanner.EnrichGit(project, localEntries));
            if (_isClosing || _isRefreshing || !ReferenceEquals(_currentProject, project))
                return;

            _worktrees = enriched;
            _selectedPath = ResolveSelection(_worktrees, _selectedPath);
            SendState();
            SaveSnapshot();
        }
        catch
        {
            // Optional divergence enrichment must not delay or break local startup.
        }
    }

    private static string? ResolveSelection(
        IReadOnlyList<WorktreeEntry> worktrees,
        string? selectedPath) =>
        worktrees.FirstOrDefault(worktree => string.Equals(
            worktree.Path,
            selectedPath,
            StringComparison.OrdinalIgnoreCase))?.Path ??
        worktrees.FirstOrDefault(worktree => worktree.IsPrimary)?.Path ??
        worktrees.FirstOrDefault()?.Path;

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

        IReadOnlyDictionary<string, PullRequestInfo> pullRequests = _scanner.GetPullRequests(project);
        return new GitSyncResult(fetchSucceeded, pullRequests);
    }

    private void OpenFolder(string? path)
    {
        WorktreeEntry? worktree = FindWorktree(path);
        if (worktree is null)
            return;
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            ArgumentList = { worktree.Path },
            UseShellExecute = true
        });
    }

    private void Launch(string? path)
    {
        WorktreeEntry? worktree = FindWorktree(path);
        if (worktree is null)
            return;

        try
        {
            _launchService.Launch(worktree);
            _hint = worktree.IsPrimary ? "Normal Rhino started" : "Worktree launch started";
            SendState();
        }
        catch (Exception ex)
        {
            _hint = "Launch failed";
            SendState();
            MessageBox.Show(this, ex.Message, "Rhino launch failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private WorktreeEntry? FindWorktree(string? path) =>
        _worktrees.FirstOrDefault(worktree => string.Equals(
            worktree.Path,
            path,
            StringComparison.OrdinalIgnoreCase));

    private void SendSnapshot(LauncherSnapshotDto snapshot)
    {
        LauncherStateDto state = new LauncherStateDto(
            _projects.Select(project => new LauncherProjectDto(project.DisplayName, project.ManifestPath)).ToArray(),
            snapshot.CurrentManifestPath,
            snapshot.ProjectName,
            snapshot.RepositoryPath,
            snapshot.Worktrees.Select(worktree => worktree with { CanLaunch = false }).ToArray(),
            snapshot.SelectedPath,
            "Refreshing worktrees...",
            true);
        Post(new { type = "state", state });
    }

    private void SaveSnapshot()
    {
        if (_currentProject is null || _worktrees.Count == 0)
            return;

        try
        {
            _snapshotStore.Save(new LauncherSnapshotDto(
                LauncherSnapshotStore.CurrentSchemaVersion,
                _currentProject.ManifestPath,
                _currentProject.DisplayName,
                _currentProject.RepositoryRoot,
                _worktrees.Select(ToDto).ToArray(),
                _selectedPath));
        }
        catch
        {
            // Snapshot persistence is optional and must not affect launcher operation.
        }
    }

    private void SendState()
    {
        if (!_webReady)
            return;

        LauncherStateDto state = new LauncherStateDto(
            _projects.Select(project => new LauncherProjectDto(project.DisplayName, project.ManifestPath)).ToArray(),
            _currentProject?.ManifestPath,
            _currentProject?.DisplayName ?? string.Empty,
            _currentProject?.RepositoryRoot ?? string.Empty,
            _worktrees.Select(ToDto).ToArray(),
            _selectedPath,
            _hint,
            _isRefreshing);
        Post(new { type = "state", state });
    }

    private static LauncherWorktreeDto ToDto(WorktreeEntry worktree) => new LauncherWorktreeDto(
        worktree.DisplayName,
        worktree.Path,
        worktree.IsPrimary,
        worktree.CanLaunch,
        worktree.IsFresh,
        worktree.FreshnessLabel,
        worktree.HasLocalState,
        worktree.HasGitState,
        worktree.LocalAdded,
        worktree.LocalDeleted,
        worktree.RelativeActivityLabel,
        worktree.AheadCount,
        worktree.BehindCount,
        worktree.AheadBarWidth,
        worktree.BehindBarWidth,
        worktree.HasPullRequest,
        worktree.PullRequestLabel,
        worktree.IsPullRequestDraft);

    private void SendSync(bool active, double local, double git) =>
        Post(new { type = "sync", active, local, git });

    private void Post(object message)
    {
        if (!_webReady || Browser.CoreWebView2 is null)
            return;
        Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    private void ApplySystemTheme()
    {
        bool isLight = IsSystemLightTheme();
        if (_isLightTheme == isLight)
            return;
        _isLightTheme = isLight;
        Browser.DefaultBackgroundColor = isLight
            ? DrawingColor.FromArgb(255, 244, 245, 247)
            : DrawingColor.FromArgb(255, 22, 24, 27);
        Background = new System.Windows.Media.SolidColorBrush(isLight
            ? System.Windows.Media.Color.FromRgb(244, 245, 247)
            : System.Windows.Media.Color.FromRgb(22, 24, 27));
        LoadingSurface.Background = Background;
        LoadingLogoPlate.Background = new System.Windows.Media.SolidColorBrush(isLight
            ? System.Windows.Media.Color.FromRgb(27, 30, 35)
            : System.Windows.Media.Color.FromRgb(244, 246, 248));
        LoadingTitle.Foreground = new System.Windows.Media.SolidColorBrush(isLight
            ? System.Windows.Media.Color.FromRgb(23, 27, 33)
            : System.Windows.Media.Color.FromRgb(240, 242, 245));
        LoadingStatus.Foreground = new System.Windows.Media.SolidColorBrush(isLight
            ? System.Windows.Media.Color.FromRgb(120, 127, 137)
            : System.Windows.Media.Color.FromRgb(108, 114, 122));
        SendTheme();
        ApplyWindowChrome();
    }

    private void SendTheme() =>
        Post(new { type = "theme", theme = _isLightTheme == true ? "light" : "dark" });

    private static bool IsSystemLightTheme()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => ApplyWindowChrome();

    private void ApplyWindowChrome()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;
        int dark = _isLightTheme == true ? 0 : 1;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private sealed record GitSyncResult(
        bool FetchSucceeded,
        IReadOnlyDictionary<string, PullRequestInfo> PullRequests);
}
