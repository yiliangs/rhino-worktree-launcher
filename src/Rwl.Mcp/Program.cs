using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using RhinoWorktreeLauncher;

namespace Rwl.Mcp;

internal static class Program
{
    public static async Task Main()
    {
        McpServer server = new McpServer(new LauncherBackend(), Console.In, Console.Out);
        await server.RunAsync(CancellationToken.None);
    }
}

internal sealed class McpServer
{
    private const string ProtocolVersion = "2025-06-18";
    private readonly LauncherBackend _backend;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new();

    public McpServer(LauncherBackend backend, TextReader input, TextWriter output)
    {
        _backend = backend;
        _input = input;
        _output = output;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        List<Task> handlers = new List<Task>();
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await _input.ReadLineAsync(cancellationToken);
            if (line is null)
                break;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonObject? message;
            try
            {
                message = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                await WriteErrorAsync(null, -32700, "Parse error.", cancellationToken);
                continue;
            }
            if (message is null)
                continue;

            if (message["method"]?.GetValue<string>() == "notifications/cancelled")
            {
                string? requestId = message["params"]?["requestId"]?.ToJsonString();
                if (requestId is not null && _requests.TryGetValue(requestId, out CancellationTokenSource? source))
                    source.Cancel();
                continue;
            }
            handlers.Add(HandleAsync(message, cancellationToken));
        }
        await Task.WhenAll(handlers);
    }

    private async Task HandleAsync(JsonObject request, CancellationToken serverToken)
    {
        JsonNode? id = request["id"]?.DeepClone();
        string requestKey = id?.ToJsonString() ?? Guid.NewGuid().ToString("N");
        using CancellationTokenSource requestSource = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        _requests[requestKey] = requestSource;
        try
        {
            string method = request["method"]?.GetValue<string>() ?? string.Empty;
            JsonObject parameters = request["params"] as JsonObject ?? new JsonObject();
            object? result = method switch
            {
                "initialize" => new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "rhino-worktree-launcher", version = "1.0.0" }
                },
                "ping" => new { },
                "tools/list" => new { tools = ToolDefinitions.All },
                "tools/call" => await CallToolAsync(parameters, requestSource.Token),
                "notifications/initialized" => null,
                _ => throw new McpException(-32601, $"Method '{method}' was not found.")
            };
            if (id is not null && method != "notifications/initialized")
                await WriteResultAsync(id, result, serverToken);
        }
        catch (McpException exception)
        {
            if (id is not null)
                await WriteErrorAsync(id, exception.Code, exception.Message, serverToken);
        }
        catch (OperationCanceledException)
        {
            if (id is not null)
                await WriteErrorAsync(id, -32800, "Request cancelled.", serverToken);
        }
        catch (Exception exception)
        {
            if (id is not null)
                await WriteErrorAsync(id, -32603, exception.Message, serverToken);
        }
        finally
        {
            _ = _requests.TryRemove(requestKey, out _);
        }
    }

    private async Task<object> CallToolAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        string name = parameters["name"]?.GetValue<string>() ??
            throw new McpException(-32602, "Tool name is required.");
        JsonObject arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        object result = name switch
        {
            "rhino_worktree_resolve_context" => await _backend.ResolveContextAsync(
                RequiredString(arguments, "cwd"),
                cancellationToken),
            "rhino_worktree_list_worktrees" => await ListWorktreesAsync(arguments, cancellationToken),
            "rhino_worktree_inspect" => await _backend.InspectWorktreeAsync(
                RequiredString(arguments, "path"),
                cancellationToken),
            "rhino_worktree_launch" => await _backend.LaunchAsync(
                RequiredString(arguments, "path"),
                TimeSpan.FromSeconds(OptionalDouble(arguments, "timeoutSeconds", 180)),
                progress: null,
                cancellationToken),
            "rhino_worktree_doctor" => await _backend.RunDoctorAsync(cancellationToken),
            _ => throw new McpException(-32602, $"Unknown tool '{name}'.")
        };
        JsonNode structured = JsonSerializer.SerializeToNode(result, JsonDefaultsForMcp.Options)!;
        bool succeeded = structured["succeeded"]?.GetValue<bool>() != false;
        bool unhealthyDoctor = name == "rhino_worktree_doctor" &&
            structured["value"]?["healthy"]?.GetValue<bool>() == false;
        return new
        {
            content = new[]
            {
                new { type = "text", text = structured.ToJsonString(JsonDefaultsForMcp.Options) }
            },
            structuredContent = structured,
            isError = !succeeded || unhealthyDoctor
        };
    }

    private async Task<CommandResult<ProjectWorktrees>> ListWorktreesAsync(
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        string? projectId = arguments["projectId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(projectId))
        {
            string cwd = RequiredString(arguments, "cwd");
            CommandResult<ResolvedContext> context = await _backend.ResolveContextAsync(cwd, cancellationToken);
            if (!context.Succeeded)
                return CommandResult<ProjectWorktrees>.Failure(context.Diagnostics.ToArray());
            projectId = context.Value!.ProjectId;
        }
        return await _backend.GetWorktreeSnapshotAsync(
            projectId,
            includeRemote: true,
            cancellationToken);
    }

    private async Task WriteResultAsync(JsonNode id, object? result, CancellationToken cancellationToken) =>
        await WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = JsonSerializer.SerializeToNode(result, JsonDefaultsForMcp.Options)
        }, cancellationToken);

    private async Task WriteErrorAsync(
        JsonNode? id,
        int code,
        string message,
        CancellationToken cancellationToken) => await WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        }, cancellationToken);

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _output.WriteLineAsync(message.ToJsonString(JsonDefaultsForMcp.Options));
            await _output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string RequiredString(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<string>() is { Length: > 0 } value
            ? value
            : throw new McpException(-32602, $"Argument '{name}' is required.");

    private static double OptionalDouble(JsonObject arguments, string name, double fallback) =>
        arguments[name]?.GetValue<double>() is > 0 and var value ? value : fallback;

    private sealed class McpException : Exception
    {
        public McpException(int code, string message)
            : base(message) => Code = code;

        public int Code { get; }
    }
}

internal static class JsonDefaultsForMcp
{
    public static JsonSerializerOptions Options { get; } = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}

internal static class ToolDefinitions
{
    public static object[] All { get; } =
    {
        Tool(
            "rhino_worktree_resolve_context",
            "Resolve a directory to its registered Rhino project and exact Git worktree.",
            new { cwd = StringProperty("Directory or file inside a Git worktree.") },
            new[] { "cwd" }),
        Tool(
            "rhino_worktree_list_worktrees",
            "List registered project worktrees with local, divergence, readiness, and optional PR state.",
            new
            {
                projectId = StringProperty("Registered project ID. Optional when cwd is supplied."),
                cwd = StringProperty("Directory used to resolve the project when projectId is omitted.")
            },
            Array.Empty<string>()),
        Tool(
            "rhino_worktree_inspect",
            "Inspect whether a selected worktree has a valid contract, driver, and Rhino runtime.",
            new { path = StringProperty("Path inside the selected worktree.") },
            new[] { "path" }),
        Tool(
            "rhino_worktree_launch",
            "Build, launch Rhino 8, and block until the loaded plug-in receipt is verified or the timeout fails.",
            new
            {
                path = StringProperty("Path inside the selected worktree."),
                timeoutSeconds = new { type = "number", minimum = 1, description = "Terminal timeout in seconds." }
            },
            new[] { "path" }),
        Tool(
            "rhino_worktree_doctor",
            "Diagnose the local launcher, catalog, project contracts, and required executables.",
            new { },
            Array.Empty<string>())
    };

    private static object Tool(
        string name,
        string description,
        object properties,
        string[] required) => new
        {
            name,
            description,
            inputSchema = new { type = "object", properties, required, additionalProperties = false }
        };

    private static object StringProperty(string description) => new { type = "string", description };
}
