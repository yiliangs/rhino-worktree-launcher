using System.Text.Json;

namespace Rwl.Protocol;

// The wire between a launcher host (MCP, CLI, desktop) and the launch executor it spawns
// through the interactive shell. One newline-delimited JSON request, then a stream of
// events ending in exactly one result (ADR 0015).
internal static class LaunchExecutorProtocol
{
    // Raised on every incompatible change to the request or event shape. A host and an
    // executor that disagree fail by name instead of misreading each other's fields.
    public const int Version = 1;

    private static readonly JsonSerializerOptions Wire = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string SerializeRequest(LaunchExecutorRequest request) =>
        JsonSerializer.Serialize(request, Wire);

    public static LaunchExecutorRequest? DeserializeRequest(string json) =>
        JsonSerializer.Deserialize<LaunchExecutorRequest>(json, Wire);

    public static string SerializeEvent(LaunchExecutorEvent value) =>
        JsonSerializer.Serialize(value, Wire);

    public static LaunchExecutorEvent? DeserializeEvent(string json) =>
        JsonSerializer.Deserialize<LaunchExecutorEvent>(json, Wire);
}

internal static class LaunchExecutorMode
{
    public const string Launch = "launch";

    // A round trip that proves the interactive spawn chain works without touching a
    // registration: the host spawns an executor, the executor answers, both end.
    public const string Ping = "ping";
}

internal static class LaunchExecutorEventKind
{
    public const string Progress = "progress";
    public const string Result = "result";
}

// Every terminal state the executor and its client can reach has a name. A launch never
// ends in an unnamed timeout.
internal static class LaunchExecutorCodes
{
    public const string InteractiveSpawnUnavailable = "interactive_spawn_unavailable";
    public const string InteractiveSpawnReady = "interactive_spawn_ready";
    public const string ExecutorStartTimeout = "executor_start_timeout";
    public const string ExecutorBootstrapFailed = "executor_bootstrap_failed";
    public const string ExecutorProtocolMismatch = "executor_protocol_mismatch";
    public const string ExecutorProtocolViolation = "executor_protocol_violation";
    public const string ExecutorRequestInvalid = "executor_request_invalid";
    public const string ExecutorPipeClosed = "executor_pipe_closed";
    public const string ExecutorClientDisconnected = "executor_client_disconnected";
    public const string ExecutorStarted = "executor_started";
    public const string RegistrySeedNotVisible = "registry_seed_not_visible";
    public const string RegistryProbeFailed = "registry_probe_failed";
    public const string RegistrySeedVerified = "registry_seed_verified";
    public const string LeaseWait = "lease_wait";
    public const string LeaseWaitTimeout = "lease_wait_timeout";
    public const string PluginRegistrationConflict = "plugin_registration_conflict";
    public const string PluginRegistrationSuspended = "plugin_registration_suspended";
    public const string PluginRegistrationDisplaced = "plugin_registration_displaced";
    public const string PluginRegistrationSeeded = "plugin_registration_seeded";
    public const string PluginRegistrationRestored = "plugin_registration_restored";
    public const string RegistrationWriteBackCorrected = "registration_write_back_corrected";
    public const string RegistrationWriteBackUnrestorable = "registration_write_back_unrestorable";
    public const string RhinoStarted = "rhino_started";
    public const string RhinoIdentityStamped = "rhino_identity_stamped";
    public const string RhinoExitedBeforeVerification = "rhino_exited_before_verification";
    public const string LaunchVerified = "launch_verified";
    public const string LaunchTimeout = "launch_timeout";
    public const string LaunchCancelled = "launch_cancelled";
    public const string LaunchFailed = "launch_failed";
}

// The launch's identity, as the launched Rhino carries it. Code running inside Rhino reads
// these from its own environment and knows which launch started it and which artifact that
// launch resolved, with no callback, no receipt, and no process asking another process what
// it is holding. Nothing in RWL reads them back: the process id to launch id mapping is
// already in every launch result and launch log.
internal static class LaunchIdentity
{
    public const string LaunchIdVariable = "RWL_LAUNCH_ID";
    public const string ArtifactVariable = "RWL_ARTIFACT";
}

// Everything the executor needs to run one launch. The host resolves the worktree, builds
// the solution, and names the artifact; the executor owns every registry mutation, the
// Rhino start, verification, and the restore.
internal sealed record LaunchExecutorRequest
{
    public int ProtocolVersion { get; init; } = LaunchExecutorProtocol.Version;
    public string Mode { get; init; } = LaunchExecutorMode.Launch;
    public string LaunchId { get; init; } = string.Empty;
    public string HostKind { get; init; } = string.Empty;
    public string ReleaseId { get; init; } = string.Empty;
    public int RhinoVersion { get; init; }
    public string PluginId { get; init; } = string.Empty;
    public string PluginName { get; init; } = string.Empty;
    public string PluginPath { get; init; } = string.Empty;
    public string RhinoExecutable { get; init; } = string.Empty;
    public string RhinoRuntime { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;
    public string LocksDirectory { get; init; } = string.Empty;
    public string LogsDirectory { get; init; } = string.Empty;
    public double TimeoutSeconds { get; init; }
}

// One streamed step or the single terminal result. Code is always set on a result and on
// any progress step worth naming in a log.
internal sealed record LaunchExecutorEvent
{
    public string Kind { get; init; } = LaunchExecutorEventKind.Progress;
    public string LaunchId { get; init; } = string.Empty;
    public string Stage { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string Severity { get; init; } = "info";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public bool Succeeded { get; init; }
    public int RhinoProcessId { get; init; }
    public string? ExecutorLogPath { get; init; }

    public bool IsResult => string.Equals(Kind, LaunchExecutorEventKind.Result, StringComparison.Ordinal);
}
