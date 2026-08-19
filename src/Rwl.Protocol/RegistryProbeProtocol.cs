using System.Text.Json;

namespace Rwl.Protocol;

// One registry read performed by a process that shares nothing with the writer. A launcher
// host can be sandboxed so that its own current-user writes are visible only to itself, so
// reading a key back in the writing process proves nothing: only an independent reader
// answers what Rhino will see (ADR 0015).
internal static class RegistryProbeProtocol
{
    private static readonly JsonSerializerOptions Wire = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string SerializeRequest(RegistryProbeRequest request) =>
        JsonSerializer.Serialize(request, Wire);

    public static RegistryProbeRequest? DeserializeRequest(string json) =>
        JsonSerializer.Deserialize<RegistryProbeRequest>(json, Wire);

    public static string SerializeResult(RegistryProbeResult result) =>
        JsonSerializer.Serialize(result, Wire);

    public static RegistryProbeResult? DeserializeResult(string json) =>
        JsonSerializer.Deserialize<RegistryProbeResult>(json, Wire);
}

internal static class RegistryHives
{
    public const string CurrentUser = "hkcu";
    public const string LocalMachine = "hklm";
}

internal sealed record RegistryProbeRequest
{
    public string Hive { get; init; } = RegistryHives.CurrentUser;
    public string KeyPath { get; init; } = string.Empty;
    public string[] Values { get; init; } = Array.Empty<string>();
}

// Values holds one entry per requested name, null where the value is absent, so a caller
// can tell "the key is there without this value" from "the key is not there".
internal sealed record RegistryProbeResult
{
    public bool Exists { get; init; }
    public Dictionary<string, string?> Values { get; init; } = new Dictionary<string, string?>();
    public string? Error { get; init; }

    public string? Value(string name) => Values.TryGetValue(name, out string? value) ? value : null;
}
