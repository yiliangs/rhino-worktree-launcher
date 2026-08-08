using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class WorktreeBackendTests
{
    [Fact]
    public async Task Registration_requires_project_read_consent_and_preserves_the_remote_choice()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string catalogPath = temporary.PathFor("launcher/projects.json");
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = catalogPath,
            LogsDirectory = temporary.PathFor("launcher/logs")
        });

        CommandResult<ProjectRegistration> refused = await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(
                temporary.PathFor("repository"),
                new ProjectAccessGrant(ReadProject: false, ReadRemote: true)),
            CancellationToken.None);

        Assert.False(refused.Succeeded);
        Assert.Equal("project_read_consent_required", Assert.Single(refused.Diagnostics).Code);
        Assert.False(File.Exists(catalogPath));

        CommandResult<ProjectRegistration> accepted = await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(
                temporary.PathFor("repository"),
                new ProjectAccessGrant(ReadProject: true, ReadRemote: false)),
            CancellationToken.None);

        Assert.True(accepted.Succeeded);
        Assert.True(accepted.Value!.Access.ReadProject);
        Assert.False(accepted.Value.Access.ReadRemote);
        Assert.Contains("\"readProject\": true", await File.ReadAllTextAsync(catalogPath));
        Assert.Contains("\"readRemote\": false", await File.ReadAllTextAsync(catalogPath));
    }

    [Fact]
    public async Task Registration_discovers_an_app_owned_typed_profile_without_scaffolding_a_driver()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string repository = temporary.CreateDirectory("sample-plugin");
        temporary.Run("git", repository, "init", "--quiet");
        temporary.Run("git", repository, "config", "user.email", "tests@example.com");
        temporary.Run("git", repository, "config", "user.name", "RWL Tests");
        temporary.WriteFile(
            "sample-plugin/Sample/Sample.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net481</TargetFramework>
                <TargetExt>.rhp</TargetExt>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="RhinoCommon" />
              </ItemGroup>
            </Project>
            """);
        temporary.WriteFile(
            "sample-plugin/Sample/SamplePlugin.cs",
            """
            using System.Runtime.InteropServices;
            using Rhino.PlugIns;
            [assembly: Guid("ef680fd0-d674-41b5-9c08-5a5d6f925fd1")]
            public sealed class SamplePlugin : PlugIn { }
            """);
        temporary.Run("git", repository, "add", ".");
        temporary.Run("git", repository, "commit", "--quiet", "-m", "initial");
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs")
        });

        CommandResult<ProjectRegistration> result = await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        BuildProfile profile = result.Value!.BuildProfile;
        Assert.Equal(BuildMode.Typed, profile.Mode);
        BuildStep step = Assert.Single(profile.Steps);
        Assert.Equal(BuildStepKind.DotNetBuild, step.Kind);
        Assert.Equal(Path.Combine("Sample", "Sample.csproj"), step.Target);
        Assert.Equal("Sample.rhp", profile.Artifacts.PluginFileName);
        Assert.Equal(Guid.Parse("ef680fd0-d674-41b5-9c08-5a5d6f925fd1"), profile.Artifacts.PluginId);
        Assert.False(Directory.Exists(temporary.PathFor("launcher/projects/sample-plugin")));
    }

    [Fact]
    public async Task Registration_can_import_a_custom_driver_without_linking_its_source_file()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string selectedDriver = temporary.PathFor("user-driver/Custom.ps1");
        temporary.WriteFile("user-driver/Custom.ps1", "Write-Output 'custom build'");
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs")
        });

        CommandResult<ProjectRegistration> result = await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(
                temporary.PathFor("repository"),
                ProjectAccessGrant.Full,
                selectedDriver),
            CancellationToken.None);
        File.Delete(selectedDriver);

        Assert.True(result.Succeeded);
        Assert.Equal(BuildMode.ImportedDriver, result.Value!.BuildProfile.Mode);
        string importedPath = Path.GetFullPath(Path.Combine(
            temporary.PathFor("launcher"),
            result.Value.BuildProfile.ImportedDriverPath!));
        Assert.StartsWith(
            Path.GetFullPath(temporary.PathFor("launcher")),
            importedPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Write-Output 'custom build'", await File.ReadAllTextAsync(importedPath));
    }

    [Fact]
    public async Task Settings_persist_remote_consent_and_allow_switching_to_an_imported_driver()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string driver = temporary.PathFor("drivers/Custom.ps1");
        temporary.WriteFile("drivers/Custom.ps1", "Write-Output 'settings driver'");
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs")
        });
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(temporary.PathFor("repository"), ProjectAccessGrant.Full),
            CancellationToken.None);

        CommandResult<ProjectRegistration> updated = await backend.UpdateProjectSettingsAsync(
            new ProjectSettingsRequest("repository", false, BuildMode.ImportedDriver, driver),
            CancellationToken.None);
        File.Delete(driver);
        ProjectSnapshot reloaded = Assert.Single((await backend.GetProjectsAsync(
            CancellationToken.None)).Value!);

        Assert.True(updated.Succeeded);
        Assert.False(reloaded.Registration.Access.ReadRemote);
        Assert.Equal(BuildMode.ImportedDriver, reloaded.Registration.BuildProfile.Mode);
        string imported = Path.Combine(
            temporary.PathFor("launcher"),
            reloaded.Registration.BuildProfile.ImportedDriverPath!);
        Assert.Equal("Write-Output 'settings driver'", await File.ReadAllTextAsync(imported));
    }

    [Fact]
    public async Task Clearing_project_cache_deletes_only_project_owned_workspace_and_remote_mirror()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string workspaceFile = temporary.PathFor("launcher/workspaces/repository/cache.txt");
        string remoteFile = temporary.PathFor("launcher/remotes/repository.git/cache.txt");
        string retainedFile = temporary.PathFor("launcher/projects/repository/drivers/Driver.ps1");
        temporary.WriteFile("launcher/workspaces/repository/cache.txt", "workspace");
        temporary.WriteFile("launcher/remotes/repository.git/cache.txt", "remote");
        temporary.WriteFile("launcher/projects/repository/drivers/Driver.ps1", "driver");
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            WorkspacesDirectory = temporary.PathFor("launcher/workspaces"),
            RemotesDirectory = temporary.PathFor("launcher/remotes")
        });
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(temporary.PathFor("repository"), ProjectAccessGrant.Full),
            CancellationToken.None);

        CommandResult<bool> result = await backend.ClearProjectCacheAsync(
            "repository",
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(workspaceFile));
        Assert.False(File.Exists(remoteFile));
        Assert.True(File.Exists(retainedFile));
        Assert.True(File.Exists(temporary.PathFor("repository/file.txt")));
    }

    [Fact]
    public async Task Registration_stores_only_app_configuration_and_never_touches_the_repository()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string repository = temporary.CreateDirectory("clean-repository");
        temporary.Run("git", repository, "init", "--quiet");
        temporary.Run("git", repository, "config", "user.email", "tests@example.com");
        temporary.Run("git", repository, "config", "user.name", "RWL Tests");
        temporary.WriteFile("clean-repository/initial.txt", "initial");
        temporary.Run("git", repository, "add", ".");
        temporary.Run("git", repository, "commit", "--quiet", "-m", "initial");
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs")
        });

        CommandResult<ProjectRegistration> registration = await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);
        CommandResult<ProjectWorktrees> worktrees = await backend.GetWorktreeSnapshotAsync(
            "clean-repository",
            includeRemote: false,
            CancellationToken.None);

        Assert.True(registration.Succeeded);
        Assert.Empty(registration.Diagnostics);
        Assert.Equal(BuildProfile.Unconfigured, registration.Value!.BuildProfile);
        Assert.True(worktrees.Succeeded);
        Assert.False(Assert.Single(worktrees.Value!.Worktrees).CanLaunch);
        Assert.Empty(Directory.EnumerateFiles(repository, "*.json", SearchOption.AllDirectories));
        Assert.True(File.Exists(temporary.PathFor("launcher/projects.json")));
    }

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
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(temporary.PathFor("repository"), ProjectAccessGrant.Full),
            CancellationToken.None);

        CommandResult<ProjectWorktrees> result = await backend.GetWorktreeSnapshotAsync(
            "repository",
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
    public async Task Remote_refresh_updates_an_app_mirror_without_fetching_into_the_repository()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string remote = temporary.CreateDirectory("remote.git");
        temporary.Run("git", remote, "init", "--bare", "--quiet");
        string repository = RepositoryFixture.Initialize(temporary, "repository");
        string branch = temporary.Run("git", repository, "branch", "--show-current").Trim();
        temporary.Run("git", repository, "remote", "add", "origin", remote);
        temporary.Run("git", repository, "push", "--quiet", "-u", "origin", branch);
        string trackingRef = $"refs/remotes/origin/{branch}";
        string trackingBefore = temporary.Run("git", repository, "rev-parse", trackingRef).Trim();
        string fetchHead = Path.Combine(repository, ".git", "FETCH_HEAD");
        DateTime? fetchHeadWriteTime = File.Exists(fetchHead) ? File.GetLastWriteTimeUtc(fetchHead) : null;

        string producer = temporary.PathFor("producer");
        temporary.Run("git", temporary.PathFor("."), "clone", "--quiet", remote, producer);
        temporary.Run("git", producer, "config", "user.email", "producer@example.com");
        temporary.Run("git", producer, "config", "user.name", "Producer");
        temporary.WriteFile("producer/remote-change.txt", "new remote commit");
        temporary.Run("git", producer, "add", ".");
        temporary.Run("git", producer, "commit", "--quiet", "-m", "remote change");
        temporary.Run("git", producer, "push", "--quiet", "origin", branch);

        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            RemotesDirectory = temporary.PathFor("launcher/remotes"),
            GitHubExecutable = temporary.PathFor("missing-gh.exe")
        });
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);

        CommandResult<ProjectWorktrees> result = await backend.GetWorktreeSnapshotAsync(
            "repository",
            includeRemote: true,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, Assert.Single(result.Value!.Worktrees).BehindCount);
        Assert.Equal(trackingBefore, temporary.Run("git", repository, "rev-parse", trackingRef).Trim());
        Assert.Equal(fetchHeadWriteTime, File.Exists(fetchHead) ? File.GetLastWriteTimeUtc(fetchHead) : null);
        Assert.True(Directory.Exists(temporary.PathFor("launcher/remotes/repository.git")));
    }

    [Fact]
    public async Task Inspection_reports_an_incomplete_build_profile_as_machine_readable_failure()
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
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(temporary.PathFor("repository"), ProjectAccessGrant.Full),
            CancellationToken.None);

        CommandResult<WorktreeInspection> result = await backend.InspectWorktreeAsync(
            temporary.PathFor("repository"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.Value!.CanLaunch);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "build_profile_incomplete");
        Assert.Equal(DiagnosticSeverity.Error, result.Diagnostics[0].Severity);
    }
}

internal static class RepositoryFixture
{
    public static TemporaryDirectory Create()
    {
        TemporaryDirectory temporary = new TemporaryDirectory();
        Initialize(temporary, "repository");
        return temporary;
    }

    public static string Initialize(
        TemporaryDirectory temporary,
        string relativePath)
    {
        string repository = temporary.CreateDirectory(relativePath);
        temporary.Run("git", repository, "init", "--quiet");
        temporary.Run("git", repository, "config", "user.email", "tests@example.com");
        temporary.Run("git", repository, "config", "user.name", "RWL Tests");
        temporary.WriteFile($"{relativePath}/file.txt", "initial");
        temporary.Run("git", repository, "add", ".");
        temporary.Run("git", repository, "commit", "--quiet", "-m", "initial");
        return repository;
    }
}
