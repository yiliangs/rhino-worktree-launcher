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

    internal sealed class FakeRhino
    {
        public int? ProcessId { get; private set; }

        public Process Start(ProcessStartInfo startInfo)
        {
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
