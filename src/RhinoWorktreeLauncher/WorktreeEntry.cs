namespace RhinoWorktreeLauncher;

public sealed class WorktreeEntry
{
    public ProjectManifest Project { get; init; } = null!;
    public string DisplayName { get; init; } = string.Empty;
    public string BranchName { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string LauncherPath { get; init; } = string.Empty;
    public DateTimeOffset LastActivityAt { get; init; }
    public int AheadCount { get; init; }
    public int BehindCount { get; init; }
    public int LocalAdded { get; init; }
    public int LocalDeleted { get; init; }
    public int? PullRequestNumber { get; init; }
    public bool IsPullRequestDraft { get; init; }
    public bool HasLocalState { get; init; }
    public bool HasGitState { get; init; }
    public bool IsPrimary { get; init; }
    public bool CanLaunch { get; init; }
    public double BehindBarWidth { get; set; }
    public double AheadBarWidth { get; set; }
    public bool IsFresh => CanLaunch;
    public bool HasPullRequest => PullRequestNumber.HasValue;
    public string FreshnessLabel => IsFresh ? "FRESH" : "STALE";
    public string PullRequestLabel => HasPullRequest ? $"PR #{PullRequestNumber}" : string.Empty;
    public string RelativeActivityLabel => FormatRelativeActivity(LastActivityAt, DateTimeOffset.Now);

    public override string ToString() => DisplayName;

    public static double ScaleDivergence(int value, int cap) =>
        value == 0 ? 0 : Math.Max(3, Math.Round(88 * Math.Sqrt((double)value / Math.Max(1, cap))));

    private static string FormatRelativeActivity(DateTimeOffset activity, DateTimeOffset now)
    {
        if (activity == DateTimeOffset.MinValue)
            return "No commits";

        TimeSpan elapsed = now - activity;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        if (elapsed.TotalMinutes < 1)
            return "just now";
        if (elapsed.TotalHours < 1)
            return FormatUnit((int)elapsed.TotalMinutes, "minute");
        if (elapsed.TotalDays < 1)
            return FormatUnit((int)elapsed.TotalHours, "hour");
        if (elapsed.TotalDays < 2)
            return "yesterday";
        if (elapsed.TotalDays < 7)
            return FormatUnit((int)elapsed.TotalDays, "day");
        if (elapsed.TotalDays < 30)
            return FormatUnit((int)(elapsed.TotalDays / 7), "week");
        if (elapsed.TotalDays < 365)
            return FormatUnit((int)(elapsed.TotalDays / 30), "month");
        return FormatUnit((int)(elapsed.TotalDays / 365), "year");
    }

    private static string FormatUnit(int value, string unit) =>
        $"{value} {unit}{(value == 1 ? string.Empty : "s")} ago";
}
