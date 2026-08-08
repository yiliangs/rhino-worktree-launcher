using System.Text.Json;
using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class ProjectCatalogTests
{
    [Fact]
    public async Task Schema_v2_catalog_migrates_once_without_retaining_repository_manifest_or_driver_paths()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        temporary.WriteFile(
            "repository/.rhino-worktree-launcher.json",
            """
            {
              "schemaVersion": 2,
              "projectId": "sample-plugin",
              "displayName": "Sample Plugin",
              "driver": { "protocolVersion": 1, "entrypoint": "tools/rhino-worktree/Driver.ps1" },
              "launch": { "rhinoVersion": 8, "mode": "rhino-package-directory" }
            }
            """);
        string gitCommonDirectory = GitCommonDirectory(temporary, repository);
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

        ProjectSnapshot migrated = Assert.Single(await new ProjectCatalog(
            temporary.PathFor("launcher/projects.json")).LoadAsync(CancellationToken.None));
        File.Delete(temporary.PathFor("repository/.rhino-worktree-launcher.json"));

        Assert.Equal("sample-plugin", migrated.ProjectId);
        Assert.Equal("Sample Plugin", migrated.DisplayName);
        Assert.True(File.Exists(temporary.PathFor("launcher/projects.schema2.backup.json")));
        string current = await File.ReadAllTextAsync(temporary.PathFor("launcher/projects.json"));
        using JsonDocument json = JsonDocument.Parse(current);
        Assert.Equal(5, json.RootElement.GetProperty("schemaVersion").GetInt32());
        JsonElement record = json.RootElement.GetProperty("projects")[0];
        Assert.False(record.TryGetProperty("manifestRelativePath", out _));
        Assert.False(record.TryGetProperty("driver", out _));
        Assert.False(current.Contains(".rhino-worktree-launcher.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Schema_v4_catalog_replaces_legacy_driver_contract_with_detected_profile()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        AddPluginProject(temporary, "repository");
        string gitCommonDirectory = GitCommonDirectory(temporary, repository);
        temporary.WriteFile(
            "launcher/projects.json",
            $$"""
            {
              "schemaVersion": 4,
              "projects": [{
                "projectId": "repository",
                "displayName": "Repository",
                "gitCommonDirectory": {{JsonSerializer.Serialize(gitCommonDirectory)}},
                "primaryCheckout": {{JsonSerializer.Serialize(repository)}},
                "driver": { "protocolVersion": 1, "entrypoint": "projects/repository/Driver.ps1" },
                "launch": { "rhinoVersion": 8, "mode": "rhino-package-directory" }
              }]
            }
            """);

        ProjectSnapshot project = Assert.Single(await new ProjectCatalog(
            temporary.PathFor("launcher/projects.json")).LoadAsync(CancellationToken.None));

        Assert.Equal(BuildMode.Typed, project.Registration.BuildProfile.Mode);
        Assert.Equal("Sample.rhp", project.Registration.BuildProfile.Artifacts.PluginFileName);
        Assert.True(File.Exists(temporary.PathFor("launcher/projects.schema4.backup.json")));
        string current = await File.ReadAllTextAsync(temporary.PathFor("launcher/projects.json"));
        Assert.False(current.Contains("\"driver\"", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Context_resolution_uses_git_identity_for_primary_and_linked_worktrees()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        string linked = temporary.PathFor("linked");
        temporary.Run("git", repository, "worktree", "add", "--quiet", "-b", "linked", linked);
        ProjectCatalog catalog = new ProjectCatalog(temporary.PathFor("launcher/projects.json"));
        await RegisterAsync(catalog, repository);
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
            RegisterAsync(new ProjectCatalog(catalogPath), first),
            RegisterAsync(new ProjectCatalog(catalogPath), second));

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
        await RegisterAsync(catalog, repository);

        temporary.Run("git", repository, "worktree", "remove", linked);
        IReadOnlyList<ProjectSnapshot> projects = await catalog.LoadAsync(CancellationToken.None);

        Assert.Equal("repository", Assert.Single(projects).ProjectId);
    }

    private static Task<ProjectRegistration> RegisterAsync(ProjectCatalog catalog, string repository) =>
        catalog.RegisterAsync(repository, ProjectAccessGrant.Full, null, CancellationToken.None);

    private static string GitCommonDirectory(TemporaryDirectory temporary, string repository) => temporary.Run(
        "git",
        repository,
        "-C",
        repository,
        "rev-parse",
        "--path-format=absolute",
        "--git-common-dir").Trim();

    private static void AddPluginProject(TemporaryDirectory temporary, string root)
    {
        temporary.WriteFile(
            $"{root}/Sample/Sample.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net481</TargetFramework><TargetExt>.rhp</TargetExt></PropertyGroup>
              <ItemGroup><Reference Include="RhinoCommon" /></ItemGroup>
            </Project>
            """);
        temporary.WriteFile(
            $"{root}/Sample/SamplePlugin.cs",
            """
            using System.Runtime.InteropServices;
            using Rhino.PlugIns;
            [assembly: Guid("ef680fd0-d674-41b5-9c08-5a5d6f925fd1")]
            public sealed class SamplePlugin : PlugIn { }
            """);
    }
}
