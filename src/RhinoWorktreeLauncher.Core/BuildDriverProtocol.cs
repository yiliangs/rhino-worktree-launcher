namespace RhinoWorktreeLauncher;

public sealed record BuildDriverRequest(
    int ProtocolVersion,
    string Command,
    string ProjectId,
    string SourcePath,
    string BuildPath);

public sealed class BuildDriverResult
{
    public int ProtocolVersion { get; init; }
    public string Kind { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public Guid PluginId { get; init; }
    public string PackageDirectory { get; init; } = string.Empty;
    public string PluginPath { get; init; } = string.Empty;
    public string RhinoRuntime { get; init; } = string.Empty;
    public BuildDriverDependency[] CriticalDependencies { get; init; } = Array.Empty<BuildDriverDependency>();
}

public sealed class BuildDriverDependency
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
}
