using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RhinoWorktreeLauncher;
using Rwl.Mcp;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
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
            RhinoExecutableResolver = _ => RhinoInstallation.AtDefaultLocation("fake-rhino.exe"),
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
            RhinoExecutableResolver = _ => RhinoInstallation.AtDefaultLocation("fake-rhino.exe"),
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

    // A progress notification describes work the terminal result announces the end of, so it is
    // only useful ahead of that result. System.Progress<T> cannot carry that: it captures the
    // synchronization context it is constructed on, a console hosted server has none, and it
    // therefore hands every callback to the thread pool, where one can still be queued when the
    // launch has already returned and the result has already been written.
    [Fact]
    public void Launch_progress_reaches_the_session_inline_in_the_order_the_launch_reported_it()
    {
        SynchronizationContext? ambient = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        RecordingSession session = new RecordingSession();
        IReadOnlyList<RecordedNotification> observed;
        int reportingThread;
        try
        {
            IProgress<LaunchProgress> relay = new OrderedLaunchProgress(session);
            reportingThread = Environment.CurrentManagedThreadId;
            relay.Report(Update(LaunchStage.Resolve, "Resolving the registered project and selected worktree."));
            relay.Report(Update(LaunchStage.Registration, "Starting a launch executor."));
            relay.Report(Update(LaunchStage.Complete, "Rhino is using the canonical binaries."));
            // Read before this thread yields anywhere: the point is that the session already
            // holds all three, which is what a queued callback cannot promise.
            observed = session.Observed;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(ambient);
        }

        Assert.Equal(3, observed.Count);
        Assert.All(observed, notification => Assert.Equal(reportingThread, notification.ThreadId));
        Assert.Equal(new[] { 1f, 2f, 3f }, observed.Select(notification => notification.Progress));
        Assert.Equal(
            new[]
            {
                "resolve: Resolving the registered project and selected worktree.",
                "registration: Starting a launch executor.",
                "complete: Rhino is using the canonical binaries."
            },
            observed.Select(notification => notification.Message));

        static LaunchProgress Update(LaunchStage stage, string message) =>
            new LaunchProgress("launch", stage, message, DateTimeOffset.UtcNow);
    }

    // The same guarantee where a client can see it: the launch's last stage is on the wire
    // before the response that reports the launch finished.
    [Fact]
    public async Task Every_launch_stage_is_on_the_wire_before_the_launch_result()
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
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);
        Assert.True(registration.Succeeded, registration.Diagnostics.FirstOrDefault()?.Message);

        IReadOnlyList<string> wire = await CallToolOverTheWireAsync(
            new RwlTools(backend, TestReadiness.Ready),
            "rhino_worktree_build_and_launch",
            repository,
            TimeSpan.FromMinutes(4));

        string result = wire[^1];
        Assert.True(IsResponseTo(result, 2), string.Join(Environment.NewLine, wire));
        Assert.True(
            Structured(result).GetProperty("succeeded").GetBoolean(),
            Structured(result).GetRawText());
        string[] reported = wire.Take(wire.Count - 1)
            .Where(IsProgressNotification)
            .Select(ProgressMessage)
            .ToArray();
        Assert.Equal(
            new[] { "resolve", "prepare", "build", "registration", "rhino", "verify", "complete" },
            reported.Select(message => message.Split(':')[0]).Distinct());
        AssertNumberedInOrder(wire);
    }

    // The failure path carries the same guarantee. A direct launch of a worktree nobody has
    // built reports its stages and then fails, and every one of those stages is on the wire
    // before the failure is.
    [Fact]
    public async Task Every_launch_stage_is_on_the_wire_before_a_failed_launch_result()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            LaunchExecutorInvoker = (_, _, _) =>
                throw new InvalidOperationException("No executor may be started by this test.")
        });
        CommandResult<ProjectRegistration> registration = await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(
                repository,
                ProjectAccessGrant.Full,
                LaunchMode: LaunchMode.DirectLaunch),
            CancellationToken.None);
        Assert.True(registration.Succeeded, registration.Diagnostics.FirstOrDefault()?.Message);

        IReadOnlyList<string> wire = await CallToolOverTheWireAsync(
            new RwlTools(backend, TestReadiness.Ready),
            "rhino_worktree_launch_existing",
            repository,
            TimeSpan.FromMinutes(4));

        string result = wire[^1];
        Assert.True(IsResponseTo(result, 2), string.Join(Environment.NewLine, wire));
        Assert.False(Structured(result).GetProperty("succeeded").GetBoolean());
        string[] reported = wire.Take(wire.Count - 1)
            .Where(IsProgressNotification)
            .Select(ProgressMessage)
            .ToArray();
        Assert.Equal(
            new[] { "resolve", "prepare", "artifact" },
            reported.Select(message => message.Split(':')[0]).Distinct());
        AssertNumberedInOrder(wire);
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
            RhinoExecutableResolver = _ => RhinoInstallation.AtDefaultLocation("fake-rhino.exe"),
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

    // Drives one launch tool over the newline-delimited JSON-RPC protocol against a server
    // hosted in this process, and answers every line that server wrote up to and including the
    // response to that call, in the order it wrote them. The order two messages were written in
    // is the order a client sees them, so it is where a progress notification is either ahead of
    // the terminal result or behind it.
    private static async Task<IReadOnlyList<string>> CallToolOverTheWireAsync(
        RwlTools tools,
        string toolName,
        string worktreePath,
        TimeSpan timeout)
    {
        ServiceCollection services = new ServiceCollection();
        services
            .AddMcpServer(options => options.ServerInfo = new Implementation
            {
                Name = "rhino-worktree-launcher",
                Version = "1.0.0"
            })
            .WithTools(tools, McpJson.Options);
        await using ServiceProvider provider = services.BuildServiceProvider();
        McpServerOptions serverOptions = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        using AnonymousPipeServerStream clientOutput = new AnonymousPipeServerStream(
            PipeDirection.Out,
            HandleInheritability.None);
        AnonymousPipeClientStream serverInput = new AnonymousPipeClientStream(
            PipeDirection.In,
            clientOutput.ClientSafePipeHandle);
        RecordedWire wire = new RecordedWire();
        StreamWriter client = new StreamWriter(clientOutput, new UTF8Encoding(false))
        {
            AutoFlush = true
        };

        using CancellationTokenSource running = new CancellationTokenSource();
        await using StreamServerTransport transport = new StreamServerTransport(serverInput, wire);
        await using McpServer server = McpServer.Create(transport, serverOptions, null, provider);
        Task loop = server.RunAsync(running.Token);
        try
        {
            await client.WriteLineAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"rwl-test","version":"1.0.0"}}}""");
            await wire.LinesThroughAsync(line => IsResponseTo(line, 1))
                .WaitAsync(TimeSpan.FromSeconds(30));

            await client.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            // The progress token is what makes the server report at all: without one the SDK
            // hands the tool a reporter that discards everything.
            await client.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new
                {
                    name = toolName,
                    arguments = new { path = worktreePath, timeoutSeconds = 120 },
                    _meta = new { progressToken = "launch" }
                }
            }));
            return await wire.LinesThroughAsync(line => IsResponseTo(line, 2)).WaitAsync(timeout);
        }
        finally
        {
            running.Cancel();
            clientOutput.Dispose();
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(15));
            }
            // The server is being torn down on purpose, so how its loop ended says nothing
            // about the ordering this test is here to observe.
            catch (Exception)
            {
            }
        }
    }

    private static bool IsResponseTo(string line, int id)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.TryGetProperty("id", out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.GetInt32() == id;
    }

    private static bool IsProgressNotification(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.TryGetProperty("method", out JsonElement method) &&
            method.GetString() == "notifications/progress";
    }

    private static string ProgressMessage(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("params").GetProperty("message").GetString()!;
    }

    private static JsonElement Structured(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("result").GetProperty("structuredContent").Clone();
    }

    // The numbers a launch's notifications carry and the order they were written in are the same
    // sequence, which is what makes them usable as a running count of one launch.
    private static void AssertNumberedInOrder(IReadOnlyList<string> wire)
    {
        float[] numbered = wire
            .Where(IsProgressNotification)
            .Select(line =>
            {
                using JsonDocument document = JsonDocument.Parse(line);
                return document.RootElement.GetProperty("params").GetProperty("progress").GetSingle();
            })
            .ToArray();
        Assert.NotEmpty(numbered);
        Assert.Equal(Enumerable.Range(1, numbered.Length).Select(number => (float)number), numbered);
    }

    private sealed record RecordedNotification(float Progress, string? Message, int ThreadId);

    // Stands in for the session the SDK injects, recording what it was handed and which thread
    // handed it over.
    private sealed class RecordingSession : IProgress<ProgressNotificationValue>
    {
        private readonly List<RecordedNotification> _observed = new List<RecordedNotification>();

        public IReadOnlyList<RecordedNotification> Observed
        {
            get
            {
                lock (_observed)
                    return _observed.ToArray();
            }
        }

        public void Report(ProgressNotificationValue value)
        {
            lock (_observed)
            {
                _observed.Add(new RecordedNotification(
                    value.Progress,
                    value.Message,
                    Environment.CurrentManagedThreadId));
            }
        }
    }

    // The server's outgoing stream, kept as the ordered list of complete lines it wrote.
    private sealed class RecordedWire : Stream
    {
        private readonly MemoryStream _written = new MemoryStream();
        private readonly List<(Func<string, bool> Match, TaskCompletionSource<IReadOnlyList<string>> Waiter)> _waiting =
            new List<(Func<string, bool>, TaskCompletionSource<IReadOnlyList<string>>)>();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>
        /// Every line written so far, once one of them matches, answered as the snapshot taken
        /// at the moment the matching line was written.
        /// </summary>
        public Task<IReadOnlyList<string>> LinesThroughAsync(Func<string, bool> match)
        {
            TaskCompletionSource<IReadOnlyList<string>> waiter =
                new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_written)
            {
                IReadOnlyList<string> lines = Lines();
                if (lines.Any(match))
                    waiter.SetResult(lines);
                else
                    _waiting.Add((match, waiter));
            }

            return waiter.Task;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Append(buffer.AsSpan(offset, count));

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            Append(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Append(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        private void Append(ReadOnlySpan<byte> data)
        {
            List<TaskCompletionSource<IReadOnlyList<string>>> ready =
                new List<TaskCompletionSource<IReadOnlyList<string>>>();
            IReadOnlyList<string> lines;
            lock (_written)
            {
                _written.Write(data);
                lines = Lines();
                for (int index = _waiting.Count - 1; index >= 0; index--)
                {
                    if (!lines.Any(_waiting[index].Match))
                        continue;
                    ready.Add(_waiting[index].Waiter);
                    _waiting.RemoveAt(index);
                }
            }

            foreach (TaskCompletionSource<IReadOnlyList<string>> waiter in ready)
                waiter.TrySetResult(lines);
        }

        // Whatever follows the last newline has not been written in full yet, so it is not a
        // line and cannot be matched against.
        private IReadOnlyList<string> Lines()
        {
            string[] parts = Encoding.UTF8
                .GetString(_written.GetBuffer(), 0, (int)_written.Length)
                .Split('\n');
            return parts
                .Take(parts.Length - 1)
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Length > 0)
                .ToArray();
        }
    }
}
