using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RhinoWorktreeLauncher;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ProjectManifest> _projects = new ObservableCollection<ProjectManifest>();
    private readonly ObservableCollection<WorktreeEntry> _worktrees = new ObservableCollection<WorktreeEntry>();
    private readonly ProjectCatalog _catalog;
    private readonly GitWorktreeScanner _scanner = new GitWorktreeScanner();
    private readonly WorktreeLaunchService _launchService = new WorktreeLaunchService();
    private ProjectManifest? _currentProject;
    private bool _isRefreshing;

    public MainWindow(ProjectCatalog catalog)
    {
        _catalog = catalog;
        InitializeComponent();
        ProjectList.ItemsSource = _projects;
        WorktreeList.ItemsSource = _worktrees;
        SourceInitialized += OnSourceInitialized;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => ReloadProjects(null);

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void ProjectList_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _currentProject = ProjectList.SelectedItem as ProjectManifest;
        RepositoryPathText.Text = _currentProject?.RepositoryRoot ?? "No project selected";
        await RefreshAsync();
    }

    private void AddProject_Click(object sender, RoutedEventArgs e)
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
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, false);
            MessageBox.Show(this, ex.Message, "Project not supported", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void WorktreeList_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e) => UpdateSelectionState();

    private void WorktreeList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (WorktreeList.SelectedItem is WorktreeEntry worktree && worktree.CanLaunch)
            Launch(worktree);
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (WorktreeList.SelectedItem is WorktreeEntry worktree)
            Launch(worktree);
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

    private void ReloadProjects(string? selectedManifestPath)
    {
        IReadOnlyList<ProjectManifest> projects = _catalog.LoadProjects();
        _projects.Clear();
        foreach (ProjectManifest project in projects)
            _projects.Add(project);

        ProjectManifest? selection = _projects.FirstOrDefault(project => string.Equals(
            project.ManifestPath,
            selectedManifestPath,
            StringComparison.OrdinalIgnoreCase)) ?? _projects.FirstOrDefault();
        ProjectList.SelectedItem = selection;
        if (selection is null)
        {
            _currentProject = null;
            RepositoryPathText.Text = "Add a project to begin";
            _worktrees.Clear();
            SetStatus("No project manifests registered", null);
            UpdateSelectionState();
        }
    }

    private async Task RefreshAsync()
    {
        if (_isRefreshing || _currentProject is null)
            return;

        _isRefreshing = true;
        LaunchButton.IsEnabled = false;
        OpenFolderButton.IsEnabled = false;
        SetStatus($"Scanning {_currentProject.DisplayName} worktrees...", null);

        try
        {
            IReadOnlyList<WorktreeEntry> entries = await Task.Run(() => _scanner.Scan(_currentProject));
            _worktrees.Clear();
            foreach (WorktreeEntry entry in entries)
                _worktrees.Add(entry);

            WorktreeList.SelectedIndex = _worktrees.Count > 0 ? 0 : -1;
            int readyCount = _worktrees.Count(entry => entry.CanLaunch);
            SetStatus($"{_worktrees.Count} worktrees found, {readyCount} ready to launch", true);
        }
        catch (Exception ex)
        {
            _worktrees.Clear();
            SetStatus(ex.Message, false);
        }
        finally
        {
            _isRefreshing = false;
            UpdateSelectionState();
        }
    }

    private void Launch(WorktreeEntry worktree)
    {
        try
        {
            _launchService.Launch(worktree);
            string message = worktree.IsPrimary
                ? $"Opened normal Rhino for {worktree.Project.DisplayName}"
                : $"Started {worktree.Project.DisplayName} from {worktree.DisplayName}";
            SetStatus(message, true);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, false);
            MessageBox.Show(this, ex.Message, "Rhino launch failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateSelectionState()
    {
        if (WorktreeList.SelectedItem is not WorktreeEntry worktree)
        {
            LaunchButton.Content = "Select a worktree";
            LaunchButton.IsEnabled = false;
            OpenFolderButton.IsEnabled = false;
            return;
        }

        LaunchButton.Content = worktree.LaunchLabel;
        LaunchButton.IsEnabled = worktree.CanLaunch && !_isRefreshing;
        OpenFolderButton.IsEnabled = Directory.Exists(worktree.Path) && !_isRefreshing;
    }

    private void SetStatus(string message, bool? success)
    {
        StatusText.Text = message;
        StatusDot.Fill = success switch
        {
            true => new SolidColorBrush(Color.FromRgb(168, 198, 123)),
            false => new SolidColorBrush(Color.FromRgb(226, 105, 105)),
            _ => new SolidColorBrush(Color.FromRgb(115, 122, 128))
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        int enabled = 1;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
