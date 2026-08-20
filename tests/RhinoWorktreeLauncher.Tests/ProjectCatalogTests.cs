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
        Assert.Equal(6, json.RootElement.GetProperty("schemaVersion").GetInt32());
        JsonElement record = json.RootElement.GetProperty("projects")[0];
        Assert.False(record.TryGetProperty("manifestRelativePath", out _));
        Assert.False(record.TryGetProperty("driver", out _));
        Assert.False(current.Contains(".rhino-worktree-launcher.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Schema_v4_catalog_replaces_legacy_driver_contract_with_solution_configuration()
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

        Assert.Equal("Sample.slnx", project.Registration.BuildProfile.SolutionPath);
        Assert.Equal(new BuildConfiguration("Debug", "x64"), project.Registration.BuildProfile.SelectedConfiguration);
        Assert.Equal(LaunchMode.BuildAndLaunch, project.Registration.BuildProfile.LaunchMode);
        Assert.Equal("netfx", project.Registration.BuildProfile.Artifacts.RhinoRuntime);
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

    [Fact]
    public async Task Project_config_persists_solution_configuration_platform_and_direct_launch_mode()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        ProjectCatalog catalog = new ProjectCatalog(temporary.PathFor("launcher/projects.json"));
        ProjectRegistration registered = await RegisterAsync(catalog, repository);

        await catalog.UpdateConfigAsync(
            new ProjectConfigRequest(
                registered.ProjectId,
                ReadRemote: false,
                "Sample/Sample.csproj",
                "Sample.slnx",
                new BuildConfiguration("Release", "Any CPU"),
                LaunchMode.DirectLaunch),
            CancellationToken.None);

        ProjectRegistration reloaded = Assert.Single(await catalog.LoadRegistrationsAsync(CancellationToken.None));
        Assert.Equal("Sample.slnx", reloaded.BuildProfile.SolutionPath);
        Assert.Equal(new BuildConfiguration("Release", "Any CPU"), reloaded.BuildProfile.SelectedConfiguration);
        Assert.Equal(LaunchMode.DirectLaunch, reloaded.BuildProfile.LaunchMode);
        Assert.False(reloaded.Access.ReadRemote);
    }

    [Fact]
    public async Task Deleting_the_last_plugin_project_degrades_the_registration_without_pruning_it()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        ProjectCatalog catalog = new ProjectCatalog(temporary.PathFor("launcher/projects.json"));
        await RegisterAsync(catalog, repository);

        Directory.Delete(Path.Combine(repository, "Sample"), recursive: true);
        ProjectSnapshot project = Assert.Single(await catalog.LoadAsync(CancellationToken.None));

        Assert.Equal("repository", project.ProjectId);
        Assert.Equal(ProjectAvailability.Degraded, project.Availability);
        Assert.Equal("plugin_project_absent", Assert.Single(project.Diagnostics).Code);
    }

    [Fact]
    public async Task Moving_the_plugin_project_warns_without_degrading_the_registration()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        ProjectCatalog catalog = new ProjectCatalog(temporary.PathFor("launcher/projects.json"));
        await RegisterAsync(catalog, repository);

        Directory.Move(Path.Combine(repository, "Sample"), Path.Combine(repository, "Renamed"));
        ProjectSnapshot project = Assert.Single(await catalog.LoadAsync(CancellationToken.None));

        Assert.Equal(ProjectAvailability.Available, project.Availability);
        Diagnostic diagnostic = Assert.Single(project.Diagnostics);
        Assert.Equal("plugin_project_missing", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public async Task A_repository_without_a_plugin_project_cannot_be_registered()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string repository = RepositoryFixture.Initialize(temporary, "repository");
        Directory.Delete(Path.Combine(repository, "Sample"), recursive: true);

        await Assert.ThrowsAsync<InvalidDataException>(() => RegisterAsync(
            new ProjectCatalog(temporary.PathFor("launcher/projects.json")),
            repository));
    }

    [Fact]
    public async Task The_catalog_opens_on_the_last_selected_project_rather_than_the_first_by_name()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string alpha = RepositoryFixture.Initialize(temporary, "alpha");
        string zulu = RepositoryFixture.Initialize(temporary, "zulu");
        string catalogPath = temporary.PathFor("launcher/projects.json");
        ProjectCatalog catalog = new ProjectCatalog(catalogPath);
        await RegisterAsync(catalog, alpha);
        ProjectRegistration registered = await RegisterAsync(catalog, zulu);

        string unread = await File.ReadAllTextAsync(catalogPath);
        ProjectCatalogView beforeSelection = await new ProjectCatalog(catalogPath)
            .LoadViewAsync(CancellationToken.None);
        string afterReading = await File.ReadAllTextAsync(catalogPath);
        await catalog.RecordSelectionAsync(registered.ProjectId, CancellationToken.None);
        ProjectCatalogView reopened = await new ProjectCatalog(catalogPath)
            .LoadViewAsync(CancellationToken.None);

        // "zulu" sorts last, so it can only be the opening project by memory.
        Assert.Equal("alpha", beforeSelection.SelectedProject?.ProjectId);
        Assert.Equal(unread, afterReading);
        Assert.Equal("zulu", reopened.SelectedProject?.ProjectId);
        Assert.Equal(new[] { "alpha", "zulu" }, reopened.Projects.Select(project => project.ProjectId).ToArray());
    }

    [Fact]
    public async Task Removing_the_remembered_project_opens_on_the_first_by_name_and_forgets_it()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string alpha = RepositoryFixture.Initialize(temporary, "alpha");
        string zulu = RepositoryFixture.Initialize(temporary, "zulu");
        string catalogPath = temporary.PathFor("launcher/projects.json");
        ProjectCatalog catalog = new ProjectCatalog(catalogPath);
        await RegisterAsync(catalog, alpha);
        ProjectRegistration registered = await RegisterAsync(catalog, zulu);
        await catalog.RecordSelectionAsync(registered.ProjectId, CancellationToken.None);

        await catalog.RemoveAsync(registered.ProjectId, CancellationToken.None);
        ProjectCatalogView reopened = await new ProjectCatalog(catalogPath)
            .LoadViewAsync(CancellationToken.None);

        Assert.Equal("alpha", reopened.SelectedProject?.ProjectId);
        using JsonDocument stored = JsonDocument.Parse(await File.ReadAllTextAsync(catalogPath));
        Assert.False(
            stored.RootElement.TryGetProperty("selectedProjectId", out JsonElement selected) &&
                selected.ValueKind != JsonValueKind.Null,
            "A removed project must not stay recorded as the selection.");
    }

    [Fact]
    public async Task An_unregistered_project_cannot_be_recorded_as_the_selection()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        ProjectCatalog catalog = new ProjectCatalog(temporary.PathFor("launcher/projects.json"));
        await RegisterAsync(catalog, temporary.PathFor("repository"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.RecordSelectionAsync(
            "not-registered",
            CancellationToken.None));
    }

    private static Task<ProjectRegistration> RegisterAsync(ProjectCatalog catalog, string repository) =>
        catalog.RegisterAsync(
            repository,
            ProjectAccessGrant.Full,
            null,
            null,
            null,
            LaunchMode.BuildAndLaunch,
            CancellationToken.None);

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
              <PropertyGroup><TargetFrameworkVersion>v4.8</TargetFrameworkVersion><TargetExt>.rhp</TargetExt></PropertyGroup>
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
    [Fact]
    public void Artifact_profile_normalizes_null_critical_dependencies_to_an_empty_list()
    {
        BuildArtifactProfile stored = JsonSerializer.Deserialize<BuildArtifactProfile>(
            """
            { "pluginId": "c50b7fc9-ffee-4ac8-83e0-6290a321eae2", "rhinoRuntime": "netfx", "criticalDependencies": null }
            """,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.NotNull(stored.CriticalDependencies);
        Assert.Empty(stored.CriticalDependencies);
        Assert.DoesNotContain(
            "null",
            JsonSerializer.Serialize(stored, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

}
