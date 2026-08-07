using System.Text.Json.Serialization;

namespace RhinoWorktreeLauncher;

public sealed record DriverRequest(
    int ProtocolVersion,
    string Command,
    string LaunchId,
    string WorktreePath,
    string ReceiptPath);

public sealed class DriverEvent
{
    public int ProtocolVersion { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string Stage { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed class DriverResult
{
    public int ProtocolVersion { get; init; }
    public string Kind { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string PackageDirectory { get; init; } = string.Empty;
    public string PluginPath { get; init; } = string.Empty;
    public string? RhinoRuntime { get; init; }
    public DriverDependency[] CriticalDependencies { get; init; } = Array.Empty<DriverDependency>();
    public ReceiptContract Receipt { get; init; } = new ReceiptContract();
}

public sealed class DriverDependency
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
}

public sealed class ReceiptContract
{
    public string LaunchIdEnvironmentVariable { get; init; } = string.Empty;
    public string ReceiptPathEnvironmentVariable { get; init; } = string.Empty;
}

public sealed class LaunchReceipt
{
    public int SchemaVersion { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? Error { get; init; }
    public string LaunchId { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public string PluginPath { get; init; } = string.Empty;
    public DriverDependency[] CriticalDependencies { get; init; } = Array.Empty<DriverDependency>();
}
