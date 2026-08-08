using System.Text.Json;
using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class ProjectCatalogTests
{
    [Fact]
    public async Task Schema_v2_catalog_migrates_the_repository_manifest_into_app_local_configuration()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        string manifestPath = temporary.PathFor("repository/.rhino-worktree-launcher.json");
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
        string gitCommonDirectory = temporary.Run(
            "git",
            repository,
            "-C",
            repository,
            "rev-parse",
            "--path-format=absolute",
            "--git-common-dir").Trim();
        temporary.WriteFile(
            "launcher/projects.json",
            $$"""
            {
              "schemaVersion": 2,
              "projects": [{
                "projectId": "sample-plugin",
                "gitCommonDirectory": {{JsonSerializer.Serialize(gitCommonDirectory)}},
                "primaryCheckout": {{JsonSerializer.Serialize(repository)}},
                "manifestRelativePath": ".rhino-worktree-launcher.json"
              }]
            }
            """);
        ProjectCatalog catalog = new ProjectCatalog(temporary.PathFor("launcher/projects.json"));

        ProjectSnapshot migrated = Assert.Single(await catalog.LoadAsync(CancellationToken.None));
        File.Delete(manifestPath);
        ProjectSnapshot afterDeletion = Assert.Single(await catalog.LoadAsync(CancellationToken.None));

        Assert.Equal("sample-plugin", migrated.ProjectId);
        Assert.Equal("Sample Plugin", migrated.DisplayName);
        Assert.Equal(ProjectAvailability.Available, afterDeletion.Availability);
        using JsonDocument catalogJson = JsonDocument.Parse(
            await File.ReadAllTextAsync(temporary.PathFor("launcher/projects.json")));
        Assert.Equal(4, catalogJson.RootElement.GetProperty("schemaVersion").GetInt32());
        JsonElement record = catalogJson.RootElement.GetProperty("projects")[0];
        Assert.Equal(
            Path.Combine("projects", "sample-plugin", "Driver.ps1"),
            record.GetProperty("driver").GetProperty("entrypoint").GetString());
        Assert.True(File.Exists(temporary.PathFor("launcher/projects/sample-plugin/Driver.ps1")));
        Assert.False(record.TryGetProperty("manifestRelativePath", out _));
        Assert.False(record.TryGetProperty("manifestPath", out _));
        Assert.True(File.Exists(temporary.PathFor("launcher/projects.schema2.backup.json")));
    }

    [Fact]
    public async Task Schema_v3_catalog_imports_a_legacy_driver_from_a_linked_worktree_into_application_data()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        const string driver = "param([string]$RequestPath)\nWrite-Output 'portable legacy driver'";
        string repository = RepositoryFixture.Initialize(temporary, "repository", driver);
        string linked = temporary.PathFor("linked");
        temporary.Run("git", repository, "worktree", "add", "--quiet", "-b", "linked", linked);
        File.Delete(Path.Combine(repository, "tools", "rhino-worktree", "Driver.ps1"));
        string gitCommonDirectory = temporary.Run(
            "git",
            repository,
            "-C",
            repository,
            "rev-parse",
            "--path-format=absolute",
            "--git-common-dir").Trim();
        temporary.WriteFile(
            "launcher/projects.json",
            $$"""
            {
              "schemaVersion": 3,
              "projects": [{
                "projectId": "repository",
                "displayName": "Repository",
                "gitCommonDirectory": {{JsonSerializer.Serialize(gitCommonDirectory)}},
                "primaryCheckout": {{JsonSerializer.Serialize(repository)}},
                "driver": {
                  "protocolVersion": 1,
                  "entrypoint": "tools/rhino-worktree/Driver.ps1"
                },
                "launch": {
                  "rhinoVersion": 8,
                  "mode": "rhino-package-directory"
                }
              }]
            }
            """);

        ProjectSnapshot project = Assert.Single(await new ProjectCatalog(
            temporary.PathFor("launcher/projects.json")).LoadAsync(CancellationToken.None));

        Assert.Equal(ProjectAvailability.Available, project.Availability);
        Assert.Contains(
            "Write-Output 'portable legacy driver'",
            await File.ReadAllTextAsync(project.Registration.DriverPath));
        Assert.True(File.Exists(temporary.PathFor("launcher/projects.schema3.backup.json")));
        Assert.False(File.Exists(Path.Combine(repository, "tools", "rhino-worktree", "Driver.ps1")));
    }

    [Fact]
    public async Task Context_resolution_uses_git_identity_for_primary_and_linked_worktrees()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        string linked = temporary.PathFor("linked");
        temporary.Run("git", repository, "worktree", "add", "--quiet", "-b", "linked", linked);
        ProjectCatalog catalog = new ProjectCatalog(temporary.PathFor("launcher/projects.json"));
        await catalog.RegisterAsync(repository, CancellationToken.None);
        ContextResolver resolver = new ContextResolver(catalog);

        CommandResult<ResolvedContext> primary = await resolver.ResolveAsync(repository, CancellationToken.None);
        CommandResult<ResolvedContext> worktree = await resolver.ResolveAsync(linked, CancellationToken.None);

        Assert.True(primary.Succeeded);
        Assert.True(worktree.Succeeded);
        Assert.Equal("repository", primary.Value!.ProjectId);
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
        string first = RepositoryFixture.Initialize(temporary, "first");
        string second = RepositoryFixture.Initialize(temporary, "second");
        string catalogPath = temporary.PathFor("launcher/projects.json");

        await Task.WhenAll(
            new ProjectCatalog(catalogPath).RegisterAsync(first, CancellationToken.None),
            new ProjectCatalog(catalogPath).RegisterAsync(second, CancellationToken.None));

        IReadOnlyList<ProjectSnapshot> projects = await new ProjectCatalog(catalogPath)
            .LoadAsync(CancellationToken.None);
        Assert.Equal(
            new[] { "first", "second" },
            projects.Select(project => project.ProjectId).Order().ToArray());
    }

    [Fact]
    public async Task Deleting_a_linked_worktree_does_not_unregister_the_project()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        string linked = temporary.PathFor("linked");
        temporary.Run("git", repository, "worktree", "add", "--quiet", "-b", "disposable", linked);
        ProjectCatalog catalog = new ProjectCatalog(temporary.PathFor("launcher/projects.json"));
        await catalog.RegisterAsync(repository, CancellationToken.None);

        temporary.Run("git", repository, "worktree", "remove", linked);
        IReadOnlyList<ProjectSnapshot> projects = await catalog.LoadAsync(CancellationToken.None);

        Assert.Equal("repository", Assert.Single(projects).ProjectId);
    }
}
