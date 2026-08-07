using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class WorktreeBackendTests
{
    [Fact]
    public async Task Compatible_unregistered_repository_requires_explicit_registration()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs")
        });

        CommandResult<ResolvedContext> result = await backend.ResolveContextAsync(
            temporary.PathFor("repository"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("project_registration_required", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task GitHub_failure_degrades_without_hiding_local_worktrees()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            GitHubExecutable = temporary.PathFor("missing-gh.exe")
        });
        await backend.RegisterProjectAsync(temporary.PathFor("repository"), CancellationToken.None);

        CommandResult<ProjectWorktrees> result = await backend.GetWorktreeSnapshotAsync(
            "sample-plugin",
            includeRemote: true,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        WorktreeSnapshot worktree = Assert.Single(result.Value!.Worktrees);
        Assert.Equal(Path.GetFullPath(temporary.PathFor("repository")), worktree.Path);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "github_unavailable" &&
            diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task Inspection_reports_missing_required_driver_as_machine_readable_failure()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            RhinoExecutableResolver = _ => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe")
        });
        await backend.RegisterProjectAsync(temporary.PathFor("repository"), CancellationToken.None);
        File.Delete(temporary.PathFor("repository/tools/rhino-worktree/Driver.ps1"));

        CommandResult<WorktreeInspection> result = await backend.InspectWorktreeAsync(
            temporary.PathFor("repository"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.Value!.CanLaunch);
        Assert.Equal("driver_missing", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(DiagnosticSeverity.Error, result.Diagnostics[0].Severity);
    }
}

internal static class RepositoryFixture
{
    public static TemporaryDirectory Create(string driverContents = "exit 0")
    {
        TemporaryDirectory temporary = new TemporaryDirectory();
        Initialize(temporary, "repository", "sample-plugin", driverContents);
        return temporary;
    }

    public static string Initialize(
        TemporaryDirectory temporary,
        string relativePath,
        string projectId,
        string driverContents = "exit 0")
    {
        string repository = temporary.CreateDirectory(relativePath);
        temporary.Run("git", repository, "init", "--quiet");
        temporary.Run("git", repository, "config", "user.email", "tests@example.com");
        temporary.Run("git", repository, "config", "user.name", "RWL Tests");
        temporary.WriteFile($"{relativePath}/file.txt", "initial");
        temporary.WriteFile(
            $"{relativePath}/.rhino-worktree-launcher.json",
            $$"""
            {
              "schemaVersion": 2,
              "projectId": "{{projectId}}",
              "displayName": "{{projectId}}",
              "driver": {
                "protocolVersion": 1,
                "entrypoint": "tools/rhino-worktree/Driver.ps1"
              },
              "launch": {
                "rhinoVersion": 8,
                "mode": "rhino-package-directory"
              }
            }
            """);
        temporary.WriteFile($"{relativePath}/tools/rhino-worktree/Driver.ps1", driverContents);
        temporary.Run("git", repository, "add", ".");
        temporary.Run("git", repository, "commit", "--quiet", "-m", "initial");
        return repository;
    }
}
