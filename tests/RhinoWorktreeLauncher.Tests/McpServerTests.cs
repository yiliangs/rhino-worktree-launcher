using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RhinoWorktreeLauncher;
using Rwl.Mcp;
using System.Diagnostics;
using System.Text.Json;

namespace RhinoWorktreeLauncher.Tests;

public sealed class McpServerTests
{
    [Fact]
    public void Mcp_executable_uses_the_windowless_Windows_subsystem()
    {
        const ushort WindowsGuiSubsystem = 2;
        string executable = Path.Combine(AppContext.BaseDirectory, "rwl-mcp.exe");
        using FileStream stream = File.OpenRead(executable);
        using BinaryReader reader = new BinaryReader(stream);

        stream.Position = 0x3c;
        int peHeaderOffset = reader.ReadInt32();
        stream.Position = peHeaderOffset + 24 + 68;

        Assert.Equal(WindowsGuiSubsystem, reader.ReadUInt16());
    }

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
        RwlTools tools = new RwlTools(backend);

        CallToolResult result = await tools.DoctorAsync(CancellationToken.None);

        Assert.True(result.IsError);
        Assert.False(result.StructuredContent!.Value
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
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(temporary.PathFor("repository"), ProjectAccessGrant.Full),
            CancellationToken.None);
        RwlTools tools = new RwlTools(backend);

        CallToolResult result = await tools.ResolveContextAsync(
            temporary.PathFor("repository"),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.True(result.StructuredContent!.Value.GetProperty("succeeded").GetBoolean());
        Assert.Equal(
            "repository",
            result.StructuredContent.Value
                .GetProperty("value")
                .GetProperty("projectId")
                .GetString());
    }

    [Fact]
    public async Task List_requires_an_explicit_project_or_directory_context()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        RwlTools tools = new RwlTools(new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs")
        }));

        CallToolResult result = await tools.ListWorktreesAsync(
            projectId: null,
            cwd: null,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(
            "project_context_required",
            result.StructuredContent!.Value
                .GetProperty("diagnostics")[0]
                .GetProperty("code")
                .GetString());
    }

    [Fact]
    public void Tool_annotations_distinguish_local_reads_remote_refresh_and_launch()
    {
        McpServerToolAttribute list = AttributeFor(nameof(RwlTools.ListWorktreesAsync));
        McpServerToolAttribute refresh = AttributeFor(nameof(RwlTools.RefreshWorktreesAsync));
        McpServerToolAttribute launch = AttributeFor(nameof(RwlTools.LaunchAsync));

        Assert.True(list.ReadOnly);
        Assert.False(list.OpenWorld);
        Assert.False(refresh.ReadOnly);
        Assert.True(refresh.OpenWorld);
        Assert.False(refresh.Destructive);
        Assert.False(launch.ReadOnly);
        Assert.True(launch.Destructive);
    }

    [Fact]
    public async Task Stdio_server_negotiates_and_publishes_instructions_annotations_and_schemas()
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "rwl-mcp.exe");
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using Process process = Process.Start(startInfo)!;
        Task<string> errorOutput = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"rwl-test","version":"1.0.0"}}}""");
            await process.StandardInput.FlushAsync(timeout.Token);
            using JsonDocument initialize = JsonDocument.Parse(
                await process.StandardOutput.ReadLineAsync(timeout.Token) ?? string.Empty);
            Assert.Equal(
                "2025-11-25",
                initialize.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
            Assert.False(string.IsNullOrWhiteSpace(
                initialize.RootElement.GetProperty("result").GetProperty("instructions").GetString()));

            await process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");
            await process.StandardInput.FlushAsync(timeout.Token);
            using JsonDocument listed = JsonDocument.Parse(
                await process.StandardOutput.ReadLineAsync(timeout.Token) ?? string.Empty);
            JsonElement[] tools = listed.RootElement
                .GetProperty("result")
                .GetProperty("tools")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(6, tools.Length);
            JsonElement launch = Assert.Single(
                tools,
                tool => tool.GetProperty("name").GetString() == "rhino_worktree_launch");
            Assert.True(launch.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
            Assert.Equal("object", launch.GetProperty("outputSchema").GetProperty("type").GetString());
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await errorOutput;
        }
    }

    private static McpServerToolAttribute AttributeFor(string methodName) =>
        Assert.Single(typeof(RwlTools)
            .GetMethod(methodName)!
            .GetCustomAttributes(typeof(McpServerToolAttribute), inherit: false)
            .Cast<McpServerToolAttribute>());
}
