using System.Runtime.Versioning;

namespace RhinoWorktreeLauncher.Tests;

/// <summary>
/// A build that fails for a reason the launcher cannot name still has to fit on a surface.
/// The diagnostic carries a message short enough to read at a glance and the failing tool's
/// own output beside it, so nothing is hidden and nothing is dumped.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BuildFailureTests
{
    [Fact]
    public async Task A_build_that_cannot_compile_reports_the_first_error_and_carries_the_rest_as_detail()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        LauncherBackend backend = Backend(temporary);
        await RegisterAsync(backend, repository);
        temporary.WriteFile("repository/Sample/Broken.cs", "public sealed class Broken { this is not C# }");

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            repository,
            LaunchMode.BuildAndLaunch,
            TimeSpan.FromMinutes(2),
            progress: null,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("build_failed", diagnostic.Code);
        // The message names what failed and where, and stops.
        Assert.Contains("Broken.cs", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("error CS", diagnostic.Message, StringComparison.Ordinal);
        Assert.True(
            diagnostic.Message.Split('\n').Length <= 3,
            $"The message must stay short. It was:\n{diagnostic.Message}");
        Assert.DoesNotContain("Build FAILED", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("exited with code", diagnostic.Message, StringComparison.Ordinal);
        // The compiler's own text is not lost, it moves beside the message.
        Assert.NotNull(diagnostic.Detail);
        Assert.Contains("Build FAILED", diagnostic.Detail!, StringComparison.Ordinal);
        Assert.Contains("error CS", diagnostic.Detail!, StringComparison.Ordinal);
    }

    // Preparing an artifact is more than building one. A step that is not the build keeps
    // the stage-level code, because no build output exists to summarise or carry.
    [Fact]
    public async Task A_prepare_step_that_is_not_the_build_keeps_the_stage_level_failure()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        LauncherBackend backend = Backend(temporary);
        await RegisterAsync(backend, repository);

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            repository,
            LaunchMode.DirectLaunch,
            TimeSpan.FromMinutes(2),
            progress: null,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("artifact_prepare_failed", diagnostic.Code);
        Assert.Null(diagnostic.Detail);
    }

    private static async Task RegisterAsync(LauncherBackend backend, string repository)
    {
        CommandResult<ProjectRegistration> registration = await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);
        Assert.True(registration.Succeeded, registration.Diagnostics.FirstOrDefault()?.Message);
    }

    private static LauncherBackend Backend(TemporaryDirectory temporary) =>
        new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LocksDirectory = temporary.PathFor("launcher/locks"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            ProcessSnapshotReader = Array.Empty<RunningProcess>
        });
}
