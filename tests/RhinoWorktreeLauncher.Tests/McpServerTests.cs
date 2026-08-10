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
    public async Task Mcp_build_and_launch_ignores_the_desktop_direct_launch_default()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        string verifierPath = temporary.PathFor("launcher/verifier/Rwl.RhinoVerifier.rhp");
        temporary.WriteFile("launcher/verifier/Rwl.RhinoVerifier.rhp", "verifier");
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LaunchStateDirectory = temporary.PathFor("launcher/launches"),
            VerifierPluginPath = verifierPath,
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            RhinoProcessStarter = CompleteVerification
        });
        CommandResult<ProjectRegistration> registration = await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(
                repository,
                ProjectAccessGrant.Full,
                LaunchMode: LaunchMode.DirectLaunch),
            CancellationToken.None);
        Assert.True(registration.Succeeded, registration.Diagnostics.FirstOrDefault()?.Message);
        RwlTools tools = new RwlTools(backend);

        CallToolResult result = await tools.BuildAndLaunchAsync(
            repository,
            timeoutSeconds: 20,
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

        string verifierPath = temporary.PathFor("launcher/verifier/Rwl.RhinoVerifier.rhp");
        temporary.WriteFile("launcher/verifier/Rwl.RhinoVerifier.rhp", "verifier");
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LaunchStateDirectory = temporary.PathFor("launcher/launches"),
            VerifierPluginPath = verifierPath,
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            RhinoProcessStarter = CompleteVerification
        });
        CommandResult<ProjectRegistration> registration = await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);
        Assert.True(registration.Succeeded, registration.Diagnostics.FirstOrDefault()?.Message);
        RwlTools tools = new RwlTools(backend);

        CallToolResult result = await tools.LaunchExistingAsync(
            repository,
            timeoutSeconds: 20,
            progress: null,
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(builtArtifact, await File.ReadAllBytesAsync(pluginPath));
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
            Assert.Equal(7, tools.Length);
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

    private static Process CompleteVerification(ProcessStartInfo startInfo)
    {
        Process process = StartSleepingProcess();
        VerifierRequest request = JsonSerializer.Deserialize<VerifierRequest>(
            File.ReadAllText(startInfo.Environment["RWL_VERIFY_REQUEST"]!),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        File.WriteAllText(request.ResultPath, JsonSerializer.Serialize(new VerifierResult
        {
            SchemaVersion = 1,
            Status = "loaded",
            LaunchId = request.LaunchId,
            ProcessId = process.Id,
            PluginPath = request.PluginPath,
            CriticalDependencies = request.CriticalDependencies
        }));
        return process;
    }

    private static Process StartSleepingProcess()
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 5");
        return Process.Start(startInfo)!;
    }
}
