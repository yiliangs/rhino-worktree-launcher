using System.Diagnostics;
using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class LaunchBackendTests
{
    [Fact]
    public async Task Build_and_launch_loads_the_canonical_worktree_artifact()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        FakeRhino rhino = new FakeRhino();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            RhinoProcessStarter = rhino.Start,
            FileInUseInspector = rhino.IsFileInUse
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
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(LaunchStatus.Succeeded, result.Value!.Status);
        Assert.StartsWith(repository, result.Value.PluginPath!, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Sample.rhp", result.Value.PluginPath!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.Value.PluginPath));
        Assert.Equal(rhino.ProcessId, result.Value.RhinoProcessId);
        Assert.False(Directory.Exists(temporary.PathFor("launcher/workspaces")));
    }

    [Fact]
    public async Task Direct_launch_uses_the_existing_selected_artifact_without_rebuilding_changed_source()
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

        FakeRhino rhino = new FakeRhino();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            RhinoProcessStarter = rhino.Start,
            FileInUseInspector = rhino.IsFileInUse
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
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            RhinoProcessStarter = rhino.Start,
            FileInUseInspector = (_, _) => false,
            PluginRegistrationScanner = (_, _, _) => new[]
            {
                new PluginRegistrationConflict("machine", competing, competingKey)
            },
            MachineRegistrationSuspender = (_, _, _, _) => Task.FromResult<IDisposable?>(null)
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
    public async Task A_competing_machine_registration_is_suspended_and_restored_when_access_is_granted()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        FakeRhino rhino = new FakeRhino();
        FakeSuspension suspension = new FakeSuspension();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            RhinoProcessStarter = rhino.Start,
            FileInUseInspector = rhino.IsFileInUse,
            PluginRegistrationScanner = (_, _, _) => new[]
            {
                new PluginRegistrationConflict(
                    "machine",
                    @"C:\primary\Sample.rhp",
                    @"HKEY_LOCAL_MACHINE\Software\McNeel\Rhinoceros\8.0\Plug-ins\11111111-2222-3333-4444-555555555555")
            },
            MachineRegistrationSuspender = (_, _, _, _) => Task.FromResult<IDisposable?>(suspension)
        });
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            repository,
            LaunchMode.BuildAndLaunch,
            TimeSpan.FromSeconds(20),
            progress: null,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.True(suspension.Disposed);
        string log = await File.ReadAllTextAsync(result.Value!.DiagnosticsLogPath);
        Assert.Contains("plugin_registration_suspended", log);
    }

    [Fact]
    public async Task A_current_user_registration_is_warned_about_and_never_blocks_the_launch()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        string existing = @"C:\primary\Sample.rhp";
        FakeRhino rhino = new FakeRhino();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            RhinoProcessStarter = rhino.Start,
            FileInUseInspector = rhino.IsFileInUse,
            PluginRegistrationScanner = (_, _, _) => new[]
            {
                new PluginRegistrationConflict(
                    "user",
                    existing,
                    @"HKEY_CURRENT_USER\Software\McNeel\Rhinoceros\8.0\Plug-ins\11111111-2222-3333-4444-555555555555")
            }
        });
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            repository,
            LaunchMode.BuildAndLaunch,
            TimeSpan.FromSeconds(20),
            progress: null,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        string log = await File.ReadAllTextAsync(result.Value!.DiagnosticsLogPath);
        Assert.Contains("plugin_registration_conflict", log);
        Assert.Contains("current-user registration", log);
    }

    [Fact]
    public async Task Rhino_starts_without_the_plugin_on_its_command_line()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        FakeRhino rhino = new FakeRhino();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            RhinoProcessStarter = rhino.Start,
            FileInUseInspector = rhino.IsFileInUse
        });
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            repository,
            LaunchMode.BuildAndLaunch,
            TimeSpan.FromSeconds(20),
            progress: null,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.DoesNotContain(rhino.Arguments, argument =>
            argument.EndsWith(".rhp", StringComparison.OrdinalIgnoreCase));
    }

    internal sealed class FakeSuspension : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    internal sealed class FakeRhino
    {
        public int? ProcessId { get; private set; }

        public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();

        public Process Start(ProcessStartInfo startInfo)
        {
            Arguments = startInfo.ArgumentList.ToArray();
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
