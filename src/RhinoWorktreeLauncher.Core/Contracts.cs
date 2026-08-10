using System.Text.Json.Serialization;

namespace RhinoWorktreeLauncher;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record Diagnostic(
    string Code,
    string Message,
    DiagnosticSeverity Severity = DiagnosticSeverity.Error);

public sealed record CommandResult<T>(
    bool Succeeded,
    T? Value,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public static CommandResult<T> Success(
        T value,
        IReadOnlyList<Diagnostic>? diagnostics = null) => new CommandResult<T>(
            true,
            value,
            diagnostics ?? Array.Empty<Diagnostic>());

    public static CommandResult<T> Failure(params Diagnostic[] diagnostics) =>
        new CommandResult<T>(false, default, diagnostics);

    public static CommandResult<T> Failure(T value, params Diagnostic[] diagnostics) =>
        new CommandResult<T>(false, value, diagnostics);
}

public enum ProjectAvailability
{
    Available,
    Degraded
}

public sealed record ProjectAccessGrant(bool ReadProject, bool ReadRemote)
{
    public static ProjectAccessGrant Full { get; } = new ProjectAccessGrant(true, true);
}

public sealed record ProjectRegistrationRequest(
    string RepositoryPath,
    ProjectAccessGrant Access,
    string? PluginProjectPath = null,
    string? SolutionPath = null,
    BuildConfiguration? BuildConfiguration = null,
    LaunchMode LaunchMode = LaunchMode.BuildAndLaunch);

public sealed record ProjectConfigRequest(
    string ProjectId,
    bool ReadRemote,
    string PluginProjectPath,
    string SolutionPath,
    BuildConfiguration BuildConfiguration,
    LaunchMode LaunchMode);

public sealed record ProjectRegistration(
    string ProjectId,
    string DisplayName,
    string GitCommonDirectory,
    string PrimaryCheckout,
    int RhinoVersion,
    ProjectAccessGrant Access,
    BuildProfile BuildProfile);

public sealed record ProjectSnapshot(
    ProjectRegistration Registration,
    ProjectAvailability Availability,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public string ProjectId => Registration.ProjectId;
    public string DisplayName => Registration.DisplayName;
}

public sealed record ResolvedContext(
    string ProjectId,
    string DisplayName,
    string GitCommonDirectory,
    string PrimaryCheckout,
    string WorktreePath,
    bool IsPrimary,
    int RhinoVersion,
    BuildProfile BuildProfile);

public sealed record ProjectWorktrees(
    ProjectSnapshot Project,
    IReadOnlyList<WorktreeSnapshot> Worktrees);

public sealed record BuildProgress(string Stage, string Message, DateTimeOffset Timestamp);

public sealed record PreparedLaunchArtifacts(
    Guid PluginId,
    string PackageDirectory,
    string PluginPath,
    string RhinoRuntime,
    IReadOnlyList<VerifiedDependency> CriticalDependencies,
    string WorktreePath);

public sealed record WorktreeInspection(
    string ProjectId,
    string WorktreePath,
    string ConfigurationPath,
    string RhinoExecutablePath,
    bool IsPrimary,
    bool CanLaunch);

public sealed record WorktreeSnapshot(
    string ProjectId,
    string DisplayName,
    string BranchName,
    string Path,
    DateTimeOffset LastActivityAt,
    int AheadCount,
    int BehindCount,
    int LocalAdded,
    int LocalDeleted,
    int? PullRequestNumber,
    bool IsPullRequestDraft,
    bool IsPrimary,
    LaunchMode LaunchMode,
    bool HasBuildConfiguration)
{
    public bool HasPullRequest => PullRequestNumber.HasValue;
    public bool HasLocalState => true;
    public bool HasGitState => true;
    public string LaunchModeLabel => HasBuildConfiguration
        ? LaunchMode == LaunchMode.BuildAndLaunch ? "BUILD & LAUNCH" : "DIRECT LAUNCH"
        : "CONFIG NEEDED";
    public string PullRequestLabel => HasPullRequest ? $"PR #{PullRequestNumber}" : string.Empty;
    public string RelativeActivityLabel => FormatRelativeActivity(LastActivityAt, DateTimeOffset.Now);
    public double BehindBarWidth { get; set; }
    public double AheadBarWidth { get; set; }

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

public enum LaunchStatus
{
    Succeeded,
    Failed
}

public sealed record LaunchProgress(
    string LaunchId,
    string Stage,
    string Message,
    DateTimeOffset Timestamp);

public sealed record VerifiedDependency(string Name, string Path);

public sealed record LaunchResult(
    string LaunchId,
    LaunchStatus Status,
    string WorktreePath,
    string? PluginPath,
    IReadOnlyList<VerifiedDependency> CriticalDependencies,
    int? RhinoProcessId,
    string DiagnosticsLogPath,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public sealed record DoctorCheck(
    string Name,
    bool Passed,
    string Message,
    DiagnosticSeverity Severity);

public sealed record DoctorReport(
    bool Healthy,
    IReadOnlyList<DoctorCheck> Checks,
    IReadOnlyList<ProjectSnapshot> Projects);
