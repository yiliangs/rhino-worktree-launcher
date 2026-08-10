using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RhinoWorktreeLauncher;

namespace Rwl.Mcp;

[McpServerToolType]
internal sealed class RwlTools
{
    internal const string ServerInstructions =
        "Use rhino_worktree_resolve_context to map the user's current directory to a registered project and exact Git worktree. " +
        "Never guess or substitute a worktree path. Prefer rhino_worktree_list_worktrees for local state; call " +
        "rhino_worktree_refresh_worktrees only when current remote or pull-request state is needed because it contacts the configured Git remote. " +
        "Call rhino_worktree_inspect before launch when readiness is uncertain. Choose rhino_worktree_build_and_launch when current source should be compiled. " +
        "Choose rhino_worktree_launch_existing only when an existing artifact is intended; it never rebuilds or claims freshness. Both launch tools temporarily change the " +
        "Rhino plug-in registration, start Rhino, and wait for binary verification. Treat returned diagnostics as authoritative and do not bypass " +
        "registration, access grants, readiness checks, or verification failures.";

    private readonly LauncherBackend _backend;

    public RwlTools(LauncherBackend backend) => _backend = backend;

    [McpServerTool(
        Name = "rhino_worktree_resolve_context",
        Title = "Resolve Rhino worktree context",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CommandResult<ResolvedContext>))]
    [Description("Resolve a directory or file to its registered Rhino project and exact Git worktree. Use this before acting from conversational working-directory context.")]
    public async Task<CallToolResult> ResolveContextAsync(
        [Description("Absolute directory or file path inside a Git worktree.")] string cwd,
        CancellationToken cancellationToken) => ToToolResult(
            await _backend.ResolveContextAsync(cwd, cancellationToken));

    [McpServerTool(
        Name = "rhino_worktree_list_worktrees",
        Title = "List local Rhino worktrees",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CommandResult<ProjectWorktrees>))]
    [Description("List registered worktrees using local Git state and cached remote metadata. This tool does not contact the remote.")]
    public async Task<CallToolResult> ListWorktreesAsync(
        [Description("Registered project ID. Optional when cwd is supplied.")] string? projectId = null,
        [Description("Absolute directory or file path used to resolve the project when projectId is omitted.")] string? cwd = null,
        CancellationToken cancellationToken = default) => ToToolResult(
            await GetWorktreesAsync(projectId, cwd, includeRemote: false, cancellationToken));

    [McpServerTool(
        Name = "rhino_worktree_refresh_worktrees",
        Title = "Refresh Rhino worktrees from remote",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CommandResult<ProjectWorktrees>))]
    [Description("Fetch current remote Git and pull-request metadata, update RWL's app-owned cache, and return the worktree list.")]
    public async Task<CallToolResult> RefreshWorktreesAsync(
        [Description("Registered project ID. Optional when cwd is supplied.")] string? projectId = null,
        [Description("Absolute directory or file path used to resolve the project when projectId is omitted.")] string? cwd = null,
        CancellationToken cancellationToken = default) => ToToolResult(
            await GetWorktreesAsync(projectId, cwd, includeRemote: true, cancellationToken));

    [McpServerTool(
        Name = "rhino_worktree_inspect",
        Title = "Inspect Rhino worktree readiness",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CommandResult<WorktreeInspection>))]
    [Description("Inspect whether a worktree has its configured solution build configuration, the bundled verifier, and the required Rhino runtime.")]
    public async Task<CallToolResult> InspectAsync(
        [Description("Absolute path inside the exact worktree to inspect.")] string path,
        CancellationToken cancellationToken) => ToToolResult(
            await _backend.InspectWorktreeAsync(path, cancellationToken));

    [McpServerTool(
        Name = "rhino_worktree_build_and_launch",
        Title = "Build and launch Rhino worktree",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CommandResult<LaunchResult>))]
    [Description("Build the exact worktree's configured solution, start Rhino 8, and wait until the bundled verifier confirms the expected binaries.")]
    public async Task<CallToolResult> BuildAndLaunchAsync(
        [Description("Absolute path inside the exact worktree to build and launch.")] string path,
        [Description("Terminal timeout in seconds. Must be between 1 and 1800.")] double timeoutSeconds = 180,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
        => await LaunchAsync(path, LaunchMode.BuildAndLaunch, timeoutSeconds, progress, cancellationToken);

    [McpServerTool(
        Name = "rhino_worktree_launch_existing",
        Title = "Launch existing Rhino worktree build",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CommandResult<LaunchResult>))]
    [Description("Launch the exact worktree's existing configured artifact without rebuilding or claiming freshness, then wait until the bundled verifier confirms the expected binaries.")]
    public async Task<CallToolResult> LaunchExistingAsync(
        [Description("Absolute path inside the exact worktree whose existing artifact should be launched.")] string path,
        [Description("Terminal timeout in seconds. Must be between 1 and 1800.")] double timeoutSeconds = 180,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
        => await LaunchAsync(path, LaunchMode.DirectLaunch, timeoutSeconds, progress, cancellationToken);

    private async Task<CallToolResult> LaunchAsync(
        string path,
        LaunchMode requestedLaunchMode,
        double timeoutSeconds,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(timeoutSeconds) || timeoutSeconds < 1 || timeoutSeconds > 1800)
        {
            return ToToolResult(CommandResult<LaunchResult>.Failure(new Diagnostic(
                "invalid_timeout",
                "timeoutSeconds must be a finite number between 1 and 1800.")));
        }

        int progressStep = 0;
        Progress<LaunchProgress>? launchProgress = progress is null
            ? null
            : new Progress<LaunchProgress>(update => progress.Report(new ProgressNotificationValue
            {
                Progress = Interlocked.Increment(ref progressStep),
                Message = $"{update.Stage}: {update.Message}"
            }));
        return ToToolResult(await _backend.LaunchAsync(
            path,
            requestedLaunchMode,
            TimeSpan.FromSeconds(timeoutSeconds),
            launchProgress,
            cancellationToken));
    }

    [McpServerTool(
        Name = "rhino_worktree_doctor",
        Title = "Diagnose Rhino Worktree Launcher",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CommandResult<DoctorReport>))]
    [Description("Diagnose RWL's saved project configuration and required local executables.")]
    public async Task<CallToolResult> DoctorAsync(CancellationToken cancellationToken) => ToToolResult(
        await _backend.RunDoctorAsync(cancellationToken),
        valueIsError: report => !report.Healthy);

    private async Task<CommandResult<ProjectWorktrees>> GetWorktreesAsync(
        string? projectId,
        string? cwd,
        bool includeRemote,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            if (string.IsNullOrWhiteSpace(cwd))
            {
                return CommandResult<ProjectWorktrees>.Failure(new Diagnostic(
                    "project_context_required",
                    "Supply projectId or cwd so RWL can identify the registered project."));
            }

            CommandResult<ResolvedContext> context = await _backend.ResolveContextAsync(cwd, cancellationToken);
            if (!context.Succeeded)
                return CommandResult<ProjectWorktrees>.Failure(context.Diagnostics.ToArray());
            projectId = context.Value!.ProjectId;
        }

        return await _backend.GetWorktreeSnapshotAsync(
            projectId.Trim(),
            includeRemote,
            cancellationToken);
    }

    private static CallToolResult ToToolResult<T>(
        CommandResult<T> result,
        Func<T, bool>? valueIsError = null)
    {
        JsonElement structured = JsonSerializer.SerializeToElement(result, McpJson.Options);
        bool isError = !result.Succeeded ||
            result.Value is not null && valueIsError?.Invoke(result.Value) == true;
        return new CallToolResult
        {
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = structured.GetRawText() }
            },
            StructuredContent = structured,
            IsError = isError
        };
    }
}

internal static class McpJson
{
    public static JsonSerializerOptions Options { get; } = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
