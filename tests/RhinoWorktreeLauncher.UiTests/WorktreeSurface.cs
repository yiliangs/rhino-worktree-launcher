using System.IO;
using System.Windows;
using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.UiTests;

/// <summary>
/// The main window arranged with one selected worktree row, so a test can read where the
/// row's parts actually landed rather than restating the numbers that produced them. The
/// backend is real and is never asked anything: the window reads the catalog only from
/// Window_Loaded, and a window that is never shown never runs it.
/// </summary>
internal static class WorktreeSurface
{
    // The design surface. The window is fixed at this size, so the panel, the row, and the
    // header all measure against the box the user actually sees.
    public static Size Client { get; } = new Size(720, 1000);

    public static IReadOnlyDictionary<string, Rect> Arrange() => SurfaceLayout.Arrange(
        () =>
        {
            MainWindow window = new MainWindow(new LauncherBackend(new LauncherBackendOptions
            {
                CatalogPath = Path.Combine(
                    Path.GetTempPath(),
                    "RhinoWorktreeLauncher.UiTests",
                    Guid.NewGuid().ToString("N"),
                    "projects.json")
            }));
            window.WorktreeList.ItemsSource = new[] { SelectedRow() };
            // Selected, launchable, and not registered, which is exactly the state that
            // offers the row action.
            window.WorktreeList.SelectedIndex = 0;
            return window;
        },
        Client);

    private static WorktreeSnapshot SelectedRow() => new WorktreeSnapshot(
        "sample",
        "feature-branch",
        "feature-branch",
        @"C:\repos\sample",
        DateTimeOffset.Now,
        AheadCount: 3,
        BehindCount: 2,
        LocalAdded: 12,
        LocalDeleted: 4,
        PullRequestNumber: null,
        IsPullRequestDraft: false,
        IsPrimary: false,
        IsRegistered: false,
        LaunchMode.BuildAndLaunch,
        HasBuildConfiguration: true,
        HasLocalState: true,
        HasGitState: true);
}
