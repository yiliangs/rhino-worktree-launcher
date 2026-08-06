namespace RhinoWorktreeLauncher;

public sealed class WorktreeEntry
{
    public ProjectManifest Project { get; init; } = null!;
    public string DisplayName { get; init; } = string.Empty;
    public string BranchName { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string LauncherPath { get; init; } = string.Empty;
    public bool IsPrimary { get; init; }
    public bool CanLaunch { get; init; }
    public string KindLabel => IsPrimary ? "Default" : "Worktree";
    public string LaunchLabel => IsPrimary ? "Open normal Rhino" : "Launch worktree Rhino";
    public string AvailabilityLabel => CanLaunch ? "Ready" : "Needs launcher";

    public override string ToString() => DisplayName;
}
