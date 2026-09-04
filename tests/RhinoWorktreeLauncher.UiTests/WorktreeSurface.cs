using System.IO;
using System.Windows;
using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.UiTests;

/// <summary>
/// The main window arranged with one worktree row, so a test can read where the row's parts
/// actually landed rather than restating the numbers that produced them. The row's state and
/// its selection are both arguments, because the gutter shows the chip or the button
/// depending on exactly those two. The backend is real and is never asked anything: the
/// window reads the catalog only from Window_Loaded, and a window that is never shown never
/// runs it.
/// </summary>
internal static class WorktreeSurface
{
    // The design surface. The window is fixed at this size, so the panel, the row, and the
    // header all measure against the box the user actually sees.
    public static Size Client { get; } = new Size(720, 1000);

    /// <summary>
    /// Selected, launchable, and not registered, which is the state the row action was
    /// first drawn for.
    /// </summary>
    public static IReadOnlyDictionary<string, Rect> Arrange() => Arrange(Row(), selected: true);

    public static IReadOnlyDictionary<string, Rect> Arrange(
        WorktreeSnapshot row,
        bool selected) => SurfaceLayout.Arrange(
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
            window.WorktreeList.ItemsSource = new[] { row };
            if (selected)
                window.WorktreeList.SelectedIndex = 0;
            return window;
        },
        Client);

    public static WorktreeSnapshot Row(
        bool isRegistered = false,
        bool hasBuildConfiguration = true,
        LaunchMode launchMode = LaunchMode.BuildAndLaunch) => new WorktreeSnapshot(
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
        IsRegistered: isRegistered,
        launchMode,
        HasBuildConfiguration: hasBuildConfiguration,
        HasLocalState: true,
        HasGitState: true);
}
