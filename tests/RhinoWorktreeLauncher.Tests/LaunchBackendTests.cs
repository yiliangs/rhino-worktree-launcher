using Rwl.Protocol;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

// Every launch path is Windows-only: the registration sandbox these tests run against is
// registry-backed.
[SupportedOSPlatform("windows")]
public sealed class LaunchBackendTests
{
    [Fact]
    public async Task Build_and_launch_loads_the_canonical_worktree_artifact()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        using RegistrySandbox registry = new RegistrySandbox(temporary);
        FakeRhino rhino = new FakeRhino();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LocksDirectory = temporary.PathFor("launcher/locks"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            LaunchExecutorInvoker = InProcessExecutor.For(registry, rhino)
        });
        CommandResult<ProjectRegistration> registration = await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);
        Assert.True(registration.Succeeded, registration.Diagnostics.FirstOrDefault()?.Message);

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            repository,
            LaunchMode.BuildAndLaunch,
            TimeSpan.FromSeconds(20),
            progress: null,
            environment: null,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(LaunchStatus.Succeeded, result.Value!.Status);
        Assert.StartsWith(repository, result.Value.PluginPath!, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Sample.rhp", result.Value.PluginPath!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.Value.PluginPath));
        Assert.Equal(rhino.ProcessId, result.Value.RhinoProcessId);
        Assert.False(Directory.Exists(temporary.PathFor("launcher/workspaces")));
    }

    // The environment is how an in-Rhino automation harness that arms on one environment
    // read is entered through an ordinary launch, so it has to survive the whole chain:
    // backend, coordinator, executor request, Rhino start.
    [Fact]
    public async Task A_launch_carries_the_callers_environment_into_the_rhino_it_starts()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        using RegistrySandbox registry = new RegistrySandbox(temporary);
        FakeRhino rhino = new FakeRhino();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LocksDirectory = temporary.PathFor("launcher/locks"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            LaunchExecutorInvoker = InProcessExecutor.For(registry, rhino)
        });
        CommandResult<ProjectRegistration> registration = await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);
        Assert.True(registration.Succeeded, registration.Diagnostics.FirstOrDefault()?.Message);

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            repository,
            LaunchMode.BuildAndLaunch,
            TimeSpan.FromSeconds(20),
            progress: null,
            new Dictionary<string, string> { ["NATALIE_SUITE_REPRO"] = "1" },
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal("1", rhino.Environment!["NATALIE_SUITE_REPRO"]);
    }

    [Fact]
    public async Task A_reserved_environment_name_refuses_the_launch_by_name_before_any_work()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        using RegistrySandbox registry = new RegistrySandbox(temporary);
        FakeRhino rhino = new FakeRhino();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LocksDirectory = temporary.PathFor("launcher/locks"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            LaunchExecutorInvoker = InProcessExecutor.For(registry, rhino)
        });
        CommandResult<ProjectRegistration> registration = await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);
        Assert.True(registration.Succeeded, registration.Diagnostics.FirstOrDefault()?.Message);

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            repository,
            LaunchMode.BuildAndLaunch,
            TimeSpan.FromSeconds(20),
            progress: null,
            new Dictionary<string, string> { ["RWL_LAUNCH_ID"] = "spoof" },
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_environment", result.Diagnostics[0].Code);
        Assert.Null(rhino.ProcessId);
    }

    [Fact]
    public async Task Build_and_launch_reports_every_stage_in_order_so_adapters_can_show_progress()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        using RegistrySandbox registry = new RegistrySandbox(temporary);
        FakeRhino rhino = new FakeRhino();
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

        List<LaunchProgress> updates = new List<LaunchProgress>();
        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            repository,
            LaunchMode.BuildAndLaunch,
            TimeSpan.FromSeconds(60),
            new ImmediateProgress<LaunchProgress>(updates.Add),
            environment: null,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(
            new[]
            {
                LaunchStage.Resolve,
                LaunchStage.Prepare,
                LaunchStage.Build,
                LaunchStage.Registration,
                LaunchStage.Rhino,
                LaunchStage.Verify,
                LaunchStage.Complete
            },
            updates.Select(update => update.Stage).Distinct());
        Assert.All(updates, update => Assert.Equal(result.Value!.LaunchId, update.LaunchId));
        // The diagnostics log and the text adapters depend on the stable lowercase token.
        Assert.Contains(updates, update => update.StageToken == "registration");
    }

    [Fact]
    public async Task Direct_launch_reports_the_artifact_stage_instead_of_building()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        using RegistrySandbox registry = new RegistrySandbox(temporary);
        temporary.Run(
            "dotnet",
            repository,
            "build",
            temporary.PathFor("repository/Sample.slnx"),
            "-c",
            "Debug",
            "-p:Platform=x64");

        FakeRhino rhino = new FakeRhino();
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

        List<LaunchProgress> updates = new List<LaunchProgress>();
        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            repository,
            LaunchMode.DirectLaunch,
            TimeSpan.FromSeconds(60),
            new ImmediateProgress<LaunchProgress>(updates.Add),
            environment: null,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Contains(updates, update => update.Stage == LaunchStage.Artifact);
        Assert.DoesNotContain(updates, update => update.Stage == LaunchStage.Build);
    }

    [Fact]
    public async Task Direct_launch_uses_the_existing_selected_artifact_without_rebuilding_changed_source()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        using RegistrySandbox registry = new RegistrySandbox(temporary);
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

        FakeRhino rhino = new FakeRhino();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LocksDirectory = temporary.PathFor("launcher/locks"),
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

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            repository,
            LaunchMode.DirectLaunch,
            TimeSpan.FromSeconds(20),
            progress: null,
            environment: null,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(Path.GetFullPath(pluginPath), result.Value!.PluginPath);
        Assert.Equal(builtArtifact, await File.ReadAllBytesAsync(pluginPath));
    }

    [Fact]
    public async Task A_competing_machine_registration_refuses_the_launch_before_starting_rhino()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        string competing = @"C:\primary\Sample.rhp";
        string competingKey =
            @"HKEY_LOCAL_MACHINE\Software\McNeel\Rhinoceros\8.0\Plug-ins\11111111-2222-3333-4444-555555555555";
        FakeRhino rhino = new FakeRhino();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LocksDirectory = temporary.PathFor("launcher/locks"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            LaunchExecutorInvoker = InProcessExecutor.For(
                new StubPluginNamespace(new PluginRegistrationConflict(competing, competingKey)),
                rhino)
        });
        CommandResult<ProjectRegistration> registration = await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);
        Assert.True(registration.Succeeded, registration.Diagnostics.FirstOrDefault()?.Message);

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            repository,
            LaunchMode.BuildAndLaunch,
            TimeSpan.FromSeconds(25),
            progress: null,
            environment: null,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(rhino.ProcessId);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Contains(competing, diagnostic.Message);
        Assert.Contains("machine-wide registration", diagnostic.Message);
        Assert.Contains(competingKey, diagnostic.Message);
        string log = await File.ReadAllTextAsync(result.Value!.DiagnosticsLogPath);
        Assert.Contains("plugin_registration_conflict", log);
    }

    [Fact]
    public async Task A_rhino_that_exited_before_verification_fails_with_its_own_diagnostic()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        using RegistrySandbox registry = new RegistrySandbox(temporary);
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LocksDirectory = temporary.PathFor("launcher/locks"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            // The process is already gone before the executor ever polls it, so the exited
            // path is exercised without depending on a timing window.
            LaunchExecutorInvoker = InProcessExecutor.For(
                registry,
                _ => StartExitedProcess(),
                (_, _) => false)
        });
        CommandResult<ProjectRegistration> registration = await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);
        Assert.True(registration.Succeeded, registration.Diagnostics.FirstOrDefault()?.Message);

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            repository,
            LaunchMode.BuildAndLaunch,
            TimeSpan.FromSeconds(25),
            progress: null,
            environment: null,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("rhino_exited_before_verification", diagnostic.Code);
        string log = await File.ReadAllTextAsync(result.Value!.DiagnosticsLogPath);
        Assert.Contains("rhino_exited_before_verification", log);
    }

    [Fact]
    public async Task A_competing_machine_registration_is_suspended_and_restored_when_access_is_granted()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        FakeRhino rhino = new FakeRhino();
        FakeLease lease = new FakeLease();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LocksDirectory = temporary.PathFor("launcher/locks"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            LaunchExecutorInvoker = InProcessExecutor.For(
                new StubPluginNamespace(lease, DisplacedMachineRegistration: @"C:\primary\Sample.rhp"),
                rhino)
        });
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            repository,
            LaunchMode.BuildAndLaunch,
            TimeSpan.FromSeconds(20),
            progress: null,
            environment: null,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        // A verified launch keeps the journal for the post-exit correction rather than
        // ending the lease outright.
        Assert.True(lease.RestoredRetainingJournal);
        Assert.False(lease.Disposed);
        string log = await File.ReadAllTextAsync(result.Value!.DiagnosticsLogPath);
        Assert.Contains("plugin_registration_suspended", log);
    }

    [Fact]
    public async Task A_displaced_current_user_registration_is_logged_and_never_blocks_the_launch()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        string existing = @"C:\primary\Sample.rhp";
        FakeRhino rhino = new FakeRhino();
        FakeLease lease = new FakeLease();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LocksDirectory = temporary.PathFor("launcher/locks"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            LaunchExecutorInvoker = InProcessExecutor.For(
                new StubPluginNamespace(lease, DisplacedUserRegistration: existing),
                rhino)
        });
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            repository,
            LaunchMode.BuildAndLaunch,
            TimeSpan.FromSeconds(20),
            progress: null,
            environment: null,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        // A verified launch keeps the journal for the post-exit correction rather than
        // ending the lease outright.
        Assert.True(lease.RestoredRetainingJournal);
        Assert.False(lease.Disposed);
        string log = await File.ReadAllTextAsync(result.Value!.DiagnosticsLogPath);
        Assert.Contains("plugin_registration_displaced", log);
        Assert.Contains(existing.Replace(@"\", @"\\"), log);
    }

    [Fact]
    public async Task Rhino_starts_without_the_plugin_on_its_command_line()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        using RegistrySandbox registry = new RegistrySandbox(temporary);
        FakeRhino rhino = new FakeRhino();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LocksDirectory = temporary.PathFor("launcher/locks"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            LaunchExecutorInvoker = InProcessExecutor.For(registry, rhino)
        });
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            repository,
            LaunchMode.BuildAndLaunch,
            TimeSpan.FromSeconds(20),
            progress: null,
            environment: null,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.DoesNotContain(rhino.Arguments, argument =>
            argument.EndsWith(".rhp", StringComparison.OrdinalIgnoreCase));
    }

    // One launch has to be readable after the fact from its log alone: who asked for it,
    // which release ran it, which artifact was registered, what the seed said, and where
    // the executor's own record is.
    [Fact]
    public async Task A_launch_log_records_the_host_the_release_the_artifact_and_the_executor_log()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        using RegistrySandbox registry = new RegistrySandbox(temporary);
        FakeRhino rhino = new FakeRhino();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            HostKind = "mcp",
            ReleaseId = "9.9.9-test",
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LocksDirectory = temporary.PathFor("launcher/locks"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            LaunchExecutorInvoker = InProcessExecutor.For(registry, rhino)
        });
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            repository,
            LaunchMode.BuildAndLaunch,
            TimeSpan.FromSeconds(60),
            progress: null,
            environment: null,
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);

        JsonElement[] records = (await File.ReadAllLinesAsync(result.Value!.DiagnosticsLogPath))
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToArray();
        JsonElement launch = Assert.Single(records, record => Kind(record) == "launch");
        Assert.Equal("mcp", launch.GetProperty("hostKind").GetString());
        Assert.Equal("9.9.9-test", launch.GetProperty("releaseId").GetString());
        Assert.Equal("BuildAndLaunch", launch.GetProperty("requestedLaunchMode").GetString());

        JsonElement request = Assert.Single(records, record => Kind(record) == "executor_request");
        Assert.Equal(result.Value.PluginPath, request.GetProperty("pluginPath").GetString());
        Assert.Equal("fake-rhino.exe", request.GetProperty("rhinoExecutable").GetString());
        Assert.Equal(LaunchExecutorProtocol.Version, request.GetProperty("protocolVersion").GetInt32());

        // Every stage transition carries its own timestamp, and the executor's log is named
        // from the first event it reported onward.
        string[] stages = records
            .Where(record => Kind(record) == "progress")
            .Select(record => record.GetProperty("stage").GetString()!)
            .Distinct()
            .ToArray();
        Assert.Equal(
            new[] { "resolve", "prepare", "build", "registration", "rhino", "verify", "complete" },
            stages);
        Assert.All(records, record => Assert.True(record.TryGetProperty("timestamp", out _)));
        JsonElement seeded = Assert.Single(
            records,
            record => record.TryGetProperty("code", out JsonElement code) &&
                code.GetString() == "plugin_registration_seeded");
        Assert.Contains("Sample.rhp", seeded.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.EndsWith(
            ".executor.jsonl",
            seeded.GetProperty("executorLog").GetString()!,
            StringComparison.Ordinal);
    }

    // Switching which build Rhino loads by default is its own operation with its own log
    // record, and it hands the registry mutation to the executor the way a launch does.
    [Fact]
    public async Task Setting_the_standing_registration_records_it_and_reports_where_it_was_written()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        using RegistrySandbox registry = new RegistrySandbox(temporary);
        temporary.Run(
            "dotnet",
            repository,
            "build",
            temporary.PathFor("repository/Sample.slnx"),
            "-c",
            "Debug",
            "-p:Platform=x64");
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LocksDirectory = temporary.PathFor("launcher/locks"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            LaunchExecutorInvoker = InProcessExecutor.For(registry, new FakeRhino())
        });
        CommandResult<ProjectRegistration> registration = await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);
        Assert.True(registration.Succeeded, registration.Diagnostics.FirstOrDefault()?.Message);

        CommandResult<RegistrationSwitchOutcome> result = await backend.SetStandingRegistrationAsync(
            repository,
            progress: null,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal("repository", result.Value!.ProjectId);
        Assert.EndsWith("Sample.rhp", result.Value.PluginPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.Value.PluginPath));
        Assert.Null(result.Value.PreviousPath);
        Assert.NotEmpty(result.Value.RegistryKeyPath);
        string[] records = await File.ReadAllLinesAsync(result.Value.DiagnosticsLogPath);
        Assert.Equal(
            "set_registration",
            JsonDocument.Parse(records[0]).RootElement.GetProperty("type").GetString());
        Assert.Contains("plugin_registration_switched", string.Join(Environment.NewLine, records));
    }

    // Pointing Rhino at a build that is not there is the one thing this must not do, and it
    // fails the way a direct launch fails, before an executor is ever started.
    [Fact]
    public async Task Setting_the_standing_registration_refuses_a_missing_artifact_before_the_executor()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        bool executorStarted = false;
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LocksDirectory = temporary.PathFor("launcher/locks"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            LaunchExecutorInvoker = (_, _, _) =>
            {
                executorStarted = true;
                return Task.FromException<LaunchExecutorEvent>(
                    new InvalidOperationException("No executor may run for a missing artifact."));
            }
        });
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);

        CommandResult<RegistrationSwitchOutcome> result = await backend.SetStandingRegistrationAsync(
            repository,
            progress: null,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(executorStarted);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("artifact_prepare_failed", diagnostic.Code);
        Assert.Contains("Build this configuration first", diagnostic.Message, StringComparison.Ordinal);
        // The failure still names its log, the way a failed launch does.
        Assert.True(File.Exists(result.Value!.DiagnosticsLogPath));
    }

    private static string? Kind(JsonElement record) => record.GetProperty("type").GetString();

    private static Process StartExitedProcess()
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("exit /b 0");
        Process process = Process.Start(startInfo)!;
        process.WaitForExit();
        return process;
    }

    internal sealed class FakeLease : IPluginNamespaceLease
    {
        public bool Disposed { get; private set; }

        public bool RestoredRetainingJournal { get; private set; }

        public bool ClearedVisibilityNonce { get; private set; }

        public void ClearVisibilityNonce() => ClearedVisibilityNonce = true;

        public void RestoreRetainingJournal() => RestoredRetainingJournal = true;

        public void Dispose() => Disposed = true;
    }

    internal sealed class FakeRhino
    {
        public int? ProcessId { get; private set; }

        public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();

        public IReadOnlyDictionary<string, string?>? Environment { get; private set; }

        public Process Start(ProcessStartInfo startInfo)
        {
            Arguments = startInfo.ArgumentList.ToArray();
            Environment = new Dictionary<string, string?>(startInfo.Environment);
            Process process = StartSleepingProcess();
            ProcessId = process.Id;
            return process;
        }

        public bool IsFileInUse(int processId, string path) => processId == ProcessId;

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
            startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
            return Process.Start(startInfo)!;
        }
    }
}
