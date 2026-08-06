namespace RhinoWorktreeLauncher;

public sealed record LauncherProjectDto(string DisplayName, string ManifestPath);

public sealed record LauncherWorktreeDto(
    string DisplayName,
    string Path,
    bool IsPrimary,
    bool CanLaunch,
    bool IsFresh,
    string FreshnessLabel,
    bool HasLocalState,
    bool HasGitState,
    int LocalAdded,
    int LocalDeleted,
    string RelativeActivityLabel,
    int AheadCount,
    int BehindCount,
    double AheadBarWidth,
    double BehindBarWidth,
    bool HasPullRequest,
    string PullRequestLabel,
    bool IsPullRequestDraft);

public sealed record LauncherStateDto(
    IReadOnlyList<LauncherProjectDto> Projects,
    string? CurrentManifestPath,
    string ProjectName,
    string RepositoryPath,
    IReadOnlyList<LauncherWorktreeDto> Worktrees,
    string? SelectedPath,
    string Hint,
    bool Syncing);

public sealed record LauncherSnapshotDto(
    int SchemaVersion,
    string CurrentManifestPath,
    string ProjectName,
    string RepositoryPath,
    IReadOnlyList<LauncherWorktreeDto> Worktrees,
    string? SelectedPath);

public sealed class WebCommand
{
    public string Type { get; set; } = string.Empty;
    public string? Path { get; set; }
    public string? ManifestPath { get; set; }
}
