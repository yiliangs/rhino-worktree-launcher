using System.Text.Json;

namespace Rwl.Protocol;

internal static class RhinoBrokerProtocol
{
    private static readonly JsonSerializerOptions RequestJson = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions ResponseJson = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    public static string SerializeRequest(RhinoLaunchRequest request) =>
        JsonSerializer.Serialize(request, RequestJson);

    public static RhinoLaunchRequest? DeserializeRequest(string json) =>
        JsonSerializer.Deserialize<RhinoLaunchRequest>(json, RequestJson);

    public static string SerializeResponse(RhinoLaunchResponse response) =>
        JsonSerializer.Serialize(response, ResponseJson);

    public static RhinoLaunchResponse? DeserializeResponse(string json) =>
        JsonSerializer.Deserialize<RhinoLaunchResponse>(json, ResponseJson);
}

internal sealed class RhinoLaunchRequest
{
    public string Executable { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;
    public string[] Arguments { get; init; } = Array.Empty<string>();
    public Dictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
}

internal sealed class RhinoLaunchResponse
{
    public int ProcessId { get; init; }
    public string? Error { get; init; }
}
