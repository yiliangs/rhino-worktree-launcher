using System.Diagnostics;
using System.Text.Json;
using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class LaunchBackendTests
{
    [Fact]
    public async Task Build_and_launch_loads_the_canonical_worktree_artifact()
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
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);
        Assert.True(registration.Succeeded, registration.Diagnostics.FirstOrDefault()?.Message);

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            repository,
            TimeSpan.FromSeconds(20),
            progress: null,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(LaunchStatus.Succeeded, result.Value!.Status);
        Assert.StartsWith(repository, result.Value.PluginPath!, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Sample.rhp", result.Value.PluginPath!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.Value.PluginPath));
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

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            repository,
            TimeSpan.FromSeconds(20),
            progress: null,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(Path.GetFullPath(pluginPath), result.Value!.PluginPath);
        Assert.Equal(builtArtifact, await File.ReadAllBytesAsync(pluginPath));
    }

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
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
        return Process.Start(startInfo)!;
    }
}
