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
        "registration, access grants, readiness checks, or verification failures. " +
        "Concurrent launches leave more than one Rhino running, each holding a different build, so bind every post-launch check or interaction to the " +
        "rhinoProcessId in the launch result, and call rhino_worktree_attribution when you need to know which live Rhino process holds which artifact.";

    private readonly LauncherBackend _backend;
    private readonly LaunchHostReadiness _readiness;

    public RwlTools(LauncherBackend backend, LaunchHostReadiness readiness)
    {
        _backend = backend;
        _readiness = readiness;
    }

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
    [Description("Inspect whether a worktree has its configured solution build configuration and the required Rhino runtime.")]
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
    [Description("Build the exact worktree's configured solution, start the registered Rhino version, and wait until that Rhino process holds the configured plug-in artifact in use.")]
    public async Task<CallToolResult> BuildAndLaunchAsync(
        [Description("Absolute path inside the exact worktree to build and launch.")] string path,
        [Description("Terminal timeout in seconds. Must be between 1 and 1800.")] double timeoutSeconds = 180,
        [Description("Optional environment variables injected into the launched Rhino process only — how an in-Rhino automation harness that arms on an environment read is entered through an ordinary launch. Names must not start with RWL_ (reserved for the launch identity).")] Dictionary<string, string>? environment = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
        => await LaunchAsync(path, LaunchMode.BuildAndLaunch, timeoutSeconds, environment, progress, cancellationToken);

    [McpServerTool(
        Name = "rhino_worktree_launch_existing",
        Title = "Launch existing Rhino worktree build",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CommandResult<LaunchResult>))]
    [Description("Launch the exact worktree's existing configured artifact without rebuilding or claiming freshness, then wait until the launched Rhino process holds that artifact in use.")]
    public async Task<CallToolResult> LaunchExistingAsync(
        [Description("Absolute path inside the exact worktree whose existing artifact should be launched.")] string path,
        [Description("Terminal timeout in seconds. Must be between 1 and 1800.")] double timeoutSeconds = 180,
        [Description("Optional environment variables injected into the launched Rhino process only — how an in-Rhino automation harness that arms on an environment read is entered through an ordinary launch. Names must not start with RWL_ (reserved for the launch identity).")] Dictionary<string, string>? environment = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
        => await LaunchAsync(path, LaunchMode.DirectLaunch, timeoutSeconds, environment, progress, cancellationToken);

    private async Task<CallToolResult> LaunchAsync(
        string path,
        LaunchMode requestedLaunchMode,
        double timeoutSeconds,
        Dictionary<string, string>? environment,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(timeoutSeconds) || timeoutSeconds < 1 || timeoutSeconds > 1800)
        {
            return ToToolResult(CommandResult<LaunchResult>.Failure(new Diagnostic(
                "invalid_timeout",
                "timeoutSeconds must be a finite number between 1 and 1800.")));
        }

        // A server that cannot reach the interactive Windows shell cannot write a
        // registration Rhino will read, so it refuses here in milliseconds rather than
        // running the whole launch to a timeout that explains nothing.
        LaunchHostState readiness = await _readiness.StateAsync();
        if (!readiness.Ready)
            return ToToolResult(CommandResult<LaunchResult>.Failure(new Diagnostic(readiness.Code, readiness.Message)));

        // Inline, never through Progress<T>: every update has to reach the session before the
        // launch that reported it returns, or a notification can be written after the result
        // that announced the end of the work it describes.
        IProgress<LaunchProgress>? launchProgress = progress is null
            ? null
            : new OrderedLaunchProgress(progress);
        return ToToolResult(await _backend.LaunchAsync(
            path,
            requestedLaunchMode,
            TimeSpan.FromSeconds(timeoutSeconds),
            launchProgress,
            environment,
            cancellationToken));
    }

    [McpServerTool(
        Name = "rhino_worktree_attribution",
        Title = "Attribute live Rhino processes to plug-in builds",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CommandResult<RhinoInstanceAttribution>))]
    [Description("List every live Rhino process with the plug-in artifacts it holds mapped in its address space. Use this when more than one Rhino is running, to identify which process runs which build before interacting with one: concurrent launches from separate sessions legitimately leave several verified Rhino processes running, each a different build. A Rhino this account cannot read is listed as unattributable with the reason rather than omitted.")]
    public async Task<CallToolResult> AttributionAsync(CancellationToken cancellationToken) => ToToolResult(
        await _backend.DescribeRhinoInstancesAsync(cancellationToken));

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
