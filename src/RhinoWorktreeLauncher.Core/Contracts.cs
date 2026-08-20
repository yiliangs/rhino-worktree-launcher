using System.Globalization;
using System.Text.Json.Serialization;

namespace RhinoWorktreeLauncher;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// One thing that went wrong. <see cref="Message"/> is what is always displayable: short,
/// complete on its own, and safe in a one-line status surface. <see cref="Detail"/> carries
/// the failing tool's own output when there is more to see, so a surface can offer it behind
/// a disclosure instead of choosing between quoting it whole and losing it. Detail is
/// bounded; the launch log holds the unabridged record.
/// </summary>
public sealed record Diagnostic(
    string Code,
    string Message,
    DiagnosticSeverity Severity = DiagnosticSeverity.Error,
    string? Detail = null);

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

// The desktop opens on one project. SelectedProject names it, so the rule for
// which project that is lives with the catalog rather than in window code.
public sealed record ProjectCatalogView(
    IReadOnlyList<ProjectSnapshot> Projects,
    ProjectSnapshot? SelectedProject);

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

public enum WorktreeRefreshStage
{
    LocalList,
    Local,
    Remote
}

public sealed record WorktreeRefreshProgress(
    WorktreeRefreshStage Stage,
    ProjectWorktrees Worktrees);

public enum BuildStage
{
    Build,
    Artifact
}

public sealed record BuildProgress(BuildStage Stage, string Message, DateTimeOffset Timestamp);

public sealed record VerifiedDependency(string Name, string Path);

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
    bool HasBuildConfiguration,
    bool HasLocalState,
    bool HasGitState)
{
    public bool HasPullRequest => PullRequestNumber.HasValue;
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

// The ordered stages a launch passes through. Adapters present the stage the user has
// reached, so it is a closed contract rather than a free-form label.
public enum LaunchStage
{
    Resolve,
    Prepare,
    Build,
    Artifact,
    Registration,
    Rhino,
    Verify,
    Complete
}

public sealed record LaunchProgress(
    string LaunchId,
    LaunchStage Stage,
    string Message,
    DateTimeOffset Timestamp)
{
    // The stable lowercase token diagnostics logs and text adapters have always written.
    public string StageToken => Stage.ToString().ToLowerInvariant();
}

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

// One live Rhino process and the plug-in artifacts it holds mapped in its address space.
// A process this account may not read is carried with the reason rather than dropped: a
// list that accounts for every live Rhino must say so when it cannot attribute one.
public sealed record RhinoInstance(
    int ProcessId,
    DateTimeOffset? StartedAt,
    string? ExecutablePath,
    IReadOnlyList<string> PlugInPaths,
    string? UnattributableReason)
{
    public bool IsAttributed => UnattributableReason is null;

    public string Describe()
    {
        string started = StartedAt is null
            ? "at an unreadable time"
            : StartedAt.Value.ToString("u", CultureInfo.InvariantCulture);
        if (!IsAttributed)
            return $"pid {ProcessId} started {started}, not attributable: {UnattributableReason}";
        return $"pid {ProcessId} started {started}, " + (PlugInPaths.Count == 0
            ? "holding no plug-in artifact"
            : $"holding {string.Join(", ", PlugInPaths)}");
    }
}

// One point-in-time answer to which Rhino runs which build. It is a reading of the machine,
// not a subscription to it: nothing here observes a Rhino over time.
public sealed record RhinoInstanceAttribution(
    DateTimeOffset ObservedAt,
    IReadOnlyList<RhinoInstance> Instances)
{
    public string Describe() =>
        $"{Instances.Count} live Rhino process(es)." +
        string.Concat(Instances.Select(instance => $" {instance.Describe()}."));
}

public sealed record DoctorCheck(
    string Name,
    bool Passed,
    string Message,
    DiagnosticSeverity Severity);

public sealed record DoctorReport(
    bool Healthy,
    IReadOnlyList<DoctorCheck> Checks,
    IReadOnlyList<ProjectSnapshot> Projects);
