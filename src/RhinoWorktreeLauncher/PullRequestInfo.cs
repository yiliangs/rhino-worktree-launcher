namespace RhinoWorktreeLauncher;

public sealed class PullRequestInfo
{
    public int Number { get; init; }
    public string HeadRefName { get; init; } = string.Empty;
    public bool IsDraft { get; init; }
}
