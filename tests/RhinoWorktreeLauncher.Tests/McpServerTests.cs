using System.Text.Json;
using RhinoWorktreeLauncher;
using Rwl.Mcp;

namespace RhinoWorktreeLauncher.Tests;

public sealed class McpServerTests
{
    [Fact]
    public async Task Doctor_marks_an_unhealthy_machine_as_a_tool_error()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            GitExecutable = temporary.PathFor("missing-git.exe")
        });
        string request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "rhino_worktree_doctor",
                arguments = new { }
            }
        });
        using StringReader input = new StringReader(request + Environment.NewLine);
        using StringWriter output = new StringWriter();
        McpServer server = new McpServer(backend, input, output);

        await server.RunAsync(CancellationToken.None);

        using JsonDocument response = JsonDocument.Parse(output.ToString());
        JsonElement toolResult = response.RootElement.GetProperty("result");
        Assert.True(toolResult.GetProperty("isError").GetBoolean());
        Assert.False(toolResult
            .GetProperty("structuredContent")
            .GetProperty("value")
            .GetProperty("healthy")
            .GetBoolean());
    }

    [Fact]
    public async Task Resolve_context_tool_returns_the_backend_command_shape()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs")
        });
        await backend.RegisterProjectAsync(temporary.PathFor("repository"), CancellationToken.None);
        string request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "rhino_worktree_resolve_context",
                arguments = new { cwd = temporary.PathFor("repository") }
            }
        });
        using StringReader input = new StringReader(request + Environment.NewLine);
        using StringWriter output = new StringWriter();
        McpServer server = new McpServer(backend, input, output);

        await server.RunAsync(CancellationToken.None);

        using JsonDocument response = JsonDocument.Parse(output.ToString());
        JsonElement structured = response.RootElement
            .GetProperty("result")
            .GetProperty("structuredContent");
        Assert.True(structured.GetProperty("succeeded").GetBoolean());
        Assert.Equal(
            "sample-plugin",
            structured.GetProperty("value").GetProperty("projectId").GetString());
    }
}
