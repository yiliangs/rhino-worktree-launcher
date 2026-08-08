namespace RhinoWorktreeLauncher;

public sealed class VerifierRequest
{
    public int SchemaVersion { get; init; }
    public string LaunchId { get; init; } = string.Empty;
    public Guid PluginId { get; init; }
    public string PluginPath { get; init; } = string.Empty;
    public VerifiedDependency[] CriticalDependencies { get; init; } = Array.Empty<VerifiedDependency>();
    public string ResultPath { get; init; } = string.Empty;
}

public sealed class VerifierResult
{
    public int SchemaVersion { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? Error { get; init; }
    public string LaunchId { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public string PluginPath { get; init; } = string.Empty;
    public VerifiedDependency[] CriticalDependencies { get; init; } = Array.Empty<VerifiedDependency>();
}
