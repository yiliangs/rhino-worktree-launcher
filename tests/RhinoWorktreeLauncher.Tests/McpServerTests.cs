using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RhinoWorktreeLauncher;
using Rwl.Mcp;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace RhinoWorktreeLauncher.Tests;

[SupportedOSPlatform("windows")]
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
        RwlTools tools = new RwlTools(backend, TestReadiness.Ready);

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
        RwlTools tools = new RwlTools(backend, TestReadiness.Ready);

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
    public async Task Mcp_build_and_launch_ignores_the_desktop_direct_launch_default()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        LaunchBackendTests.FakeRhino rhino = new LaunchBackendTests.FakeRhino();
        using RegistrySandbox registry = new RegistrySandbox(temporary);
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            LaunchExecutorInvoker = InProcessExecutor.For(registry, rhino)
        });
        CommandResult<ProjectRegistration> registration = await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(
                repository,
                ProjectAccessGrant.Full,
                LaunchMode: LaunchMode.DirectLaunch),
            CancellationToken.None);
        Assert.True(registration.Succeeded, registration.Diagnostics.FirstOrDefault()?.Message);
        RwlTools tools = new RwlTools(backend, TestReadiness.Ready);

        CallToolResult result = await tools.BuildAndLaunchAsync(
            repository,
            timeoutSeconds: 20,
            environment: null,
            progress: null,
            CancellationToken.None);

        Assert.False(result.IsError);
        JsonElement value = result.StructuredContent!.Value.GetProperty("value");
        Assert.True(File.Exists(value.GetProperty("pluginPath").GetString()));
    }

    [Fact]
    public async Task Mcp_launch_existing_ignores_the_desktop_build_and_launch_default()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        temporary.Run(
            "dotnet",
            repository,
            "build",
            temporary.PathFor("repository/Sample.slnx"),
            "-c",
            "Debug",
            "-p:Platform=x64");
        string pluginPath = Assert.Single(Directory.EnumerateFiles(
            temporary.PathFor("repository/Sample/bin"),
            "Sample.rhp",
            SearchOption.AllDirectories));
        byte[] builtArtifact = await File.ReadAllBytesAsync(pluginPath);
        temporary.WriteFile(
            "repository/Sample/ChangedAfterBuild.cs",
            "namespace Sample; public static class ChangedAfterBuild { public const int Value = 2; }");

        LaunchBackendTests.FakeRhino rhino = new LaunchBackendTests.FakeRhino();
        using RegistrySandbox registry = new RegistrySandbox(temporary);
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            LaunchExecutorInvoker = InProcessExecutor.For(registry, rhino)
        });
        CommandResult<ProjectRegistration> registration = await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);
        Assert.True(registration.Succeeded, registration.Diagnostics.FirstOrDefault()?.Message);
        RwlTools tools = new RwlTools(backend, TestReadiness.Ready);

        CallToolResult result = await tools.LaunchExistingAsync(
            repository,
            timeoutSeconds: 20,
            environment: null,
            progress: null,
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(builtArtifact, await File.ReadAllBytesAsync(pluginPath));
    }

    [Fact]
    public async Task List_requires_an_explicit_project_or_directory_context()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        RwlTools tools = new RwlTools(
            new LauncherBackend(new LauncherBackendOptions
            {
                CatalogPath = temporary.PathFor("launcher/projects.json"),
                LogsDirectory = temporary.PathFor("launcher/logs")
            }),
            TestReadiness.Ready);

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

    // A server that cannot reach the interactive Windows shell cannot write a registration
    // Rhino will read. It says so in milliseconds instead of running the launch to a
    // timeout that explains nothing.
    [Fact]
    public async Task A_degraded_host_fails_a_launch_immediately_with_the_reason()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LocksDirectory = temporary.PathFor("launcher/locks"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            LaunchExecutorInvoker = (_, _, _) =>
                throw new InvalidOperationException("No launch may be attempted by a degraded host.")
        });
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);
        RwlTools tools = new RwlTools(
            backend,
            TestReadiness.Degraded(
                "interactive_spawn_unavailable",
                "RWL cannot reach the interactive Windows shell from this process."));

        CallToolResult result = await tools.BuildAndLaunchAsync(
            repository,
            timeoutSeconds: 20,
            environment: null,
            progress: null,
            CancellationToken.None);

        Assert.True(result.IsError);
        JsonElement diagnostic = result.StructuredContent!.Value.GetProperty("diagnostics")[0];
        Assert.Equal("interactive_spawn_unavailable", diagnostic.GetProperty("code").GetString());
        Assert.Contains(
            "interactive Windows shell",
            diagnostic.GetProperty("message").GetString()!,
            StringComparison.Ordinal);
    }

    // The tool an agent reaches for when several Rhino processes are live and it has to know
    // which one runs which build before touching one.
    [Fact]
    public async Task Attribution_tool_reports_each_live_rhino_with_the_artifact_it_holds()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            ProcessSnapshotReader = () => new[]
            {
                new RunningProcess(
                    4242,
                    900,
                    "Rhino.exe",
                    @"C:\Program Files\Rhino 8\System\Rhino.exe",
                    new DateTimeOffset(2026, 8, 19, 9, 0, 0, TimeSpan.Zero))
            },
            MappedPlugInReader = _ => new[] { @"C:\worktrees\branch\Sample.rhp" }
        });
        RwlTools tools = new RwlTools(backend, TestReadiness.Ready);

        CallToolResult result = await tools.AttributionAsync(CancellationToken.None);

        Assert.False(result.IsError);
        JsonElement instance = Assert.Single(result.StructuredContent!.Value
            .GetProperty("value")
            .GetProperty("instances")
            .EnumerateArray()
            .ToArray());
        Assert.Equal(4242, instance.GetProperty("processId").GetInt32());
        Assert.Equal(
            @"C:\worktrees\branch\Sample.rhp",
            instance.GetProperty("plugInPaths")[0].GetString());
    }

    [Fact]
    public void Tool_annotations_distinguish_local_reads_remote_refresh_and_launch()
    {
        McpServerToolAttribute attribution = AttributeFor(nameof(RwlTools.AttributionAsync));
        Assert.True(attribution.ReadOnly);
        Assert.False(attribution.Destructive);
        Assert.False(attribution.OpenWorld);

        McpServerToolAttribute list = AttributeFor(nameof(RwlTools.ListWorktreesAsync));
        McpServerToolAttribute refresh = AttributeFor(nameof(RwlTools.RefreshWorktreesAsync));
        McpServerToolAttribute buildAndLaunch = AttributeFor(nameof(RwlTools.BuildAndLaunchAsync));
        McpServerToolAttribute launchExisting = AttributeFor(nameof(RwlTools.LaunchExistingAsync));

        Assert.True(list.ReadOnly);
        Assert.False(list.OpenWorld);
        Assert.False(refresh.ReadOnly);
        Assert.True(refresh.OpenWorld);
        Assert.False(refresh.Destructive);
        Assert.False(buildAndLaunch.ReadOnly);
        Assert.True(buildAndLaunch.Destructive);
        Assert.False(launchExisting.ReadOnly);
        Assert.True(launchExisting.Destructive);
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
            Assert.Equal(8, tools.Length);
            JsonElement attribution = Assert.Single(
                tools,
                tool => tool.GetProperty("name").GetString() == "rhino_worktree_attribution");
            Assert.Contains(
                "when more than one Rhino is running",
                attribution.GetProperty("description").GetString(),
                StringComparison.Ordinal);
            Assert.True(attribution.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
            JsonElement buildAndLaunch = Assert.Single(
                tools,
                tool => tool.GetProperty("name").GetString() == "rhino_worktree_build_and_launch");
            JsonElement launchExisting = Assert.Single(
                tools,
                tool => tool.GetProperty("name").GetString() == "rhino_worktree_launch_existing");
            Assert.DoesNotContain(
                tools,
                tool => tool.GetProperty("name").GetString() == "rhino_worktree_launch");
            Assert.True(buildAndLaunch.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
            Assert.Equal("object", buildAndLaunch.GetProperty("outputSchema").GetProperty("type").GetString());
            Assert.Contains(
                "without rebuilding or claiming freshness",
                launchExisting.GetProperty("description").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await errorOutput;
        }
    }

    [Fact]
    public async Task Stdio_doctor_completes_without_inheriting_the_protocol_stream()
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
            _ = await process.StandardOutput.ReadLineAsync(timeout.Token);

            await process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rhino_worktree_doctor","arguments":{}}}""");
            await process.StandardInput.FlushAsync(timeout.Token);
            using JsonDocument response = JsonDocument.Parse(
                await process.StandardOutput.ReadLineAsync(timeout.Token) ?? string.Empty);

            Assert.Equal(2, response.RootElement.GetProperty("id").GetInt32());
            Assert.True(response.RootElement
                .GetProperty("result")
                .GetProperty("structuredContent")
                .GetProperty("succeeded")
                .GetBoolean());
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
