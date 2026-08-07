using System.Text.Json;
using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class ProjectCatalogTests
{
    [Fact]
    public async Task Registered_project_remains_visible_when_manifest_disappears()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string repository = temporary.CreateDirectory("repository");
        temporary.Run("git", repository, "init", "--quiet");
        temporary.WriteFile(
            "repository/.rhino-worktree-launcher.json",
            """
            {
              "schemaVersion": 2,
              "projectId": "sample-plugin",
              "displayName": "Sample Plugin",
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
        temporary.WriteFile("repository/tools/rhino-worktree/Driver.ps1", "exit 0");

        ProjectCatalog catalog = new ProjectCatalog(temporary.PathFor("projects.json"));
        ProjectRegistration registered = await catalog.RegisterAsync(
            repository,
            CancellationToken.None);

        File.Delete(temporary.PathFor("repository/.rhino-worktree-launcher.json"));
        IReadOnlyList<ProjectSnapshot> projects = await catalog.LoadAsync(CancellationToken.None);

        ProjectSnapshot project = Assert.Single(projects);
        Assert.Equal(registered.ProjectId, project.Registration.ProjectId);
        Assert.Equal(ProjectAvailability.Degraded, project.Availability);
        Assert.Equal("manifest_missing", Assert.Single(project.Diagnostics).Code);
        string catalogJson = await File.ReadAllTextAsync(temporary.PathFor("projects.json"));
        Assert.Equal("sample-plugin", JsonDocument.Parse(catalogJson).RootElement
            .GetProperty("projects")[0]
            .GetProperty("projectId")
            .GetString());
    }

    [Fact]
    public async Task Context_resolution_uses_git_identity_for_primary_and_linked_worktrees()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string repository = temporary.CreateDirectory("repository");
        temporary.Run("git", repository, "init", "--quiet");
        temporary.Run("git", repository, "config", "user.email", "tests@example.com");
        temporary.Run("git", repository, "config", "user.name", "RWL Tests");
        temporary.WriteFile("repository/file.txt", "initial");
        temporary.WriteFile(
            "repository/.rhino-worktree-launcher.json",
            """
            {
              "schemaVersion": 2,
              "projectId": "sample-plugin",
              "displayName": "Sample Plugin",
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
        temporary.WriteFile("repository/tools/rhino-worktree/Driver.ps1", "exit 0");
        temporary.Run("git", repository, "add", ".");
        temporary.Run("git", repository, "commit", "--quiet", "-m", "initial");
        string linked = temporary.PathFor("linked");
        temporary.Run("git", repository, "worktree", "add", "--quiet", "-b", "linked", linked);

        ProjectCatalog catalog = new ProjectCatalog(temporary.PathFor("projects.json"));
        await catalog.RegisterAsync(repository, CancellationToken.None);
        ContextResolver resolver = new ContextResolver(catalog);

        CommandResult<ResolvedContext> primary = await resolver.ResolveAsync(
            temporary.PathFor("repository/tools"),
            CancellationToken.None);
        CommandResult<ResolvedContext> worktree = await resolver.ResolveAsync(
            temporary.PathFor("linked/tools"),
            CancellationToken.None);

        Assert.True(primary.Succeeded);
        Assert.True(worktree.Succeeded);
        Assert.Equal("sample-plugin", primary.Value!.ProjectId);
        Assert.Equal(primary.Value.GitCommonDirectory, worktree.Value!.GitCommonDirectory);
        Assert.Equal(Path.GetFullPath(repository), primary.Value.WorktreePath);
        Assert.Equal(Path.GetFullPath(linked), worktree.Value.WorktreePath);
        Assert.True(primary.Value.IsPrimary);
        Assert.False(worktree.Value.IsPrimary);
    }

    [Fact]
    public async Task Concurrent_catalog_writers_do_not_clobber_each_other()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string first = RepositoryFixture.Initialize(temporary, "first", "first-plugin");
        string second = RepositoryFixture.Initialize(temporary, "second", "second-plugin");
        string catalogPath = temporary.PathFor("launcher/projects.json");
        ProjectCatalog firstProcess = new ProjectCatalog(catalogPath);
        ProjectCatalog secondProcess = new ProjectCatalog(catalogPath);

        await Task.WhenAll(
            firstProcess.RegisterAsync(first, CancellationToken.None),
            secondProcess.RegisterAsync(second, CancellationToken.None));

        IReadOnlyList<ProjectSnapshot> projects = await new ProjectCatalog(catalogPath)
            .LoadAsync(CancellationToken.None);
        Assert.Equal(
            new[] { "first-plugin", "second-plugin" },
            projects.Select(project => project.ProjectId).Order().ToArray());
    }

    [Fact]
    public async Task Legacy_catalog_read_is_degraded_and_does_not_rewrite_the_file()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        const string legacy = """
            {
              "Projects": [
                { "ManifestPath": "C:\\old-task\\.rhino-worktree-launcher.json" }
              ]
            }
            """;
        temporary.WriteFile("projects.json", legacy);
        ProjectCatalog catalog = new ProjectCatalog(temporary.PathFor("projects.json"));

        IReadOnlyList<ProjectSnapshot> projects = await catalog.LoadAsync(CancellationToken.None);

        ProjectSnapshot project = Assert.Single(projects);
        Assert.Equal(ProjectAvailability.Degraded, project.Availability);
        Assert.Equal("catalog_registration_legacy", Assert.Single(project.Diagnostics).Code);
        Assert.Equal(legacy, await File.ReadAllTextAsync(temporary.PathFor("projects.json")));
    }

    [Fact]
    public async Task Deleting_a_linked_worktree_does_not_unregister_the_project()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        string linked = temporary.PathFor("linked");
        temporary.Run("git", repository, "worktree", "add", "--quiet", "-b", "disposable", linked);
        ProjectCatalog catalog = new ProjectCatalog(temporary.PathFor("projects.json"));
        await catalog.RegisterAsync(repository, CancellationToken.None);

        temporary.Run("git", repository, "worktree", "remove", linked);
        IReadOnlyList<ProjectSnapshot> projects = await catalog.LoadAsync(CancellationToken.None);

        Assert.Equal("sample-plugin", Assert.Single(projects).ProjectId);
    }
}
