using System.Runtime.Versioning;

namespace RhinoWorktreeLauncher.Tests;

/// <summary>
/// A build that cannot replace its own output because another program holds it open. The
/// lock is a legitimate developer situation, so the launcher names the condition, the file,
/// and any live Rhino holding that worktree's plug-in, instead of handing the adapter a
/// transcript of MSBuild's retries to display.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LockedBuildOutputTests
{
    [Fact]
    public async Task A_locked_build_output_is_named_as_its_own_failure_rather_than_a_build_transcript()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        string artifact = BuildOnce(temporary, repository);
        using ProcessHoldingArtifact holder = ProcessHoldingArtifact.Start(artifact);
        LauncherBackend backend = Backend(temporary, Array.Empty<RunningProcess>());
        await RegisterAsync(backend, repository);
        ChangeSource(temporary);

        CommandResult<LaunchResult> result = await holder.WhileHoldingAsync(() => backend.LaunchAsync(
            repository,
            LaunchMode.BuildAndLaunch,
            TimeSpan.FromMinutes(2),
            progress: null,
            environment: null,
            CancellationToken.None));

        Assert.False(result.Succeeded);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("build_output_locked", diagnostic.Code);
        Assert.Contains(artifact, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        // The message a person reads, not the transcript the log already holds.
        Assert.DoesNotContain("MSB3026", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("MSB3027", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Build FAILED", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Common.CurrentVersion.targets", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("exited with code", diagnostic.Message, StringComparison.Ordinal);
        // RWL reads Rhino and nothing else, so an unnamed holder is said to be unnamed.
        Assert.Contains("no live Rhino", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            diagnostic.Message.Split('\n').Length <= 6,
            $"The dialog text must stay short. It was:\n{diagnostic.Message}");
        // The transcript still exists, so nothing is lost by leaving it out of the message.
        Assert.Contains(
            "MSB3027",
            await File.ReadAllTextAsync(result.Value!.DiagnosticsLogPath),
            StringComparison.Ordinal);
        // And it travels with the diagnostic, so a surface can offer it without reading the log.
        Assert.NotNull(diagnostic.Detail);
        Assert.Contains("MSB3027", diagnostic.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_live_rhino_holding_the_worktree_artifact_is_named_in_the_failure()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        string artifact = BuildOnce(temporary, repository);
        using ProcessHoldingArtifact holder = ProcessHoldingArtifact.Start(artifact);
        // The real address-space reader, over a process table that names the holder as Rhino.
        LauncherBackend backend = Backend(
            temporary,
            new[] { new RunningProcess(holder.ProcessId, Environment.ProcessId, "Rhino.exe", null, null) });
        await RegisterAsync(backend, repository);
        ChangeSource(temporary);

        CommandResult<LaunchResult> result = await holder.WhileHoldingAsync(() => backend.LaunchAsync(
            repository,
            LaunchMode.BuildAndLaunch,
            TimeSpan.FromMinutes(2),
            progress: null,
            environment: null,
            CancellationToken.None));

        Assert.False(result.Succeeded);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("build_output_locked", diagnostic.Code);
        Assert.Contains($"pid {holder.ProcessId}", diagnostic.Message, StringComparison.Ordinal);
    }

    private static string BuildOnce(TemporaryDirectory temporary, string repository)
    {
        temporary.Run(
            "dotnet",
            repository,
            "build",
            temporary.PathFor("repository/Sample.slnx"),
            "-c",
            "Debug",
            "-p:Platform=x64");
        return Path.GetFullPath(Assert.Single(Directory.EnumerateFiles(
            temporary.PathFor("repository/Sample/bin"),
            "Sample.rhp",
            SearchOption.AllDirectories)));
    }

    // A source change is what makes the next build copy over the held artifact rather than
    // deciding it is already up to date.
    private static void ChangeSource(TemporaryDirectory temporary) => temporary.WriteFile(
        "repository/Sample/ChangedAfterBuild.cs",
        "namespace Sample; public static class ChangedAfterBuild { public const int Value = 2; }");

    private static async Task RegisterAsync(LauncherBackend backend, string repository)
    {
        CommandResult<ProjectRegistration> registration = await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);
        Assert.True(registration.Succeeded, registration.Diagnostics.FirstOrDefault()?.Message);
    }

    private static LauncherBackend Backend(
        TemporaryDirectory temporary,
        IReadOnlyList<RunningProcess> processes) => new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LocksDirectory = temporary.PathFor("launcher/locks"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            ProcessSnapshotReader = () => processes
        });
}
