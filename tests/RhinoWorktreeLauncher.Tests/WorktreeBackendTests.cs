using RhinoWorktreeLauncher;
using Rwl.Protocol;

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
    public async Task Registration_requires_a_solution_containing_the_Rhino_plugin_project()
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
              <PropertyGroup><TargetFramework>net481</TargetFramework><TargetExt>.rhp</TargetExt></PropertyGroup>
              <ItemGroup><Reference Include="RhinoCommon" /></ItemGroup>
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

        Assert.False(result.Succeeded);
        Assert.Equal("registration_failed", Assert.Single(result.Diagnostics).Code);
        Assert.Contains("solution", result.Diagnostics[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(temporary.PathFor("launcher/projects.json")));
    }

    [Fact]
    public async Task Registration_selects_the_canonical_solution_Debug_x64_configuration()
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
        temporary.WriteFile(
            "sample-plugin/Sample.slnx",
            """
            <Solution>
              <Configurations>
                <BuildType Name="Debug" />
                <BuildType Name="Release" />
                <Platform Name="Any CPU" />
                <Platform Name="x64" />
              </Configurations>
              <Project Path="Sample/Sample.csproj" />
            </Solution>
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

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        BuildProfile profile = result.Value!.BuildProfile;
        Assert.Equal("Sample.slnx", profile.SolutionPath);
        Assert.Equal(Path.Combine("Sample", "Sample.csproj"), profile.PluginProjectPath);
        Assert.Equal(new BuildConfiguration("Debug", "x64"), profile.SelectedConfiguration);
        Assert.Equal(LaunchMode.BuildAndLaunch, profile.LaunchMode);
        Assert.Equal(Guid.Parse("ef680fd0-d674-41b5-9c08-5a5d6f925fd1"), profile.Artifacts.PluginId);
    }

    [Fact]
    public async Task Registration_excludes_non_assembly_project_references_from_critical_dependencies()
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
                <ProjectReference Include="..\Library\Library.csproj" />
                <ProjectReference Include="..\Overlay\Overlay.csproj" ReferenceOutputAssembly="false" />
                <ProjectReference Include="..\Companion\Companion.csproj">
                  <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
                </ProjectReference>
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
        temporary.WriteFile(
            "sample-plugin/Sample.slnx",
            """
            <Solution>
              <Configurations>
                <BuildType Name="Debug" />
                <BuildType Name="Release" />
                <Platform Name="Any CPU" />
                <Platform Name="x64" />
              </Configurations>
              <Project Path="Sample/Sample.csproj" />
            </Solution>
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

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(
            new[] { "Library" },
            result.Value!.BuildProfile.Artifacts.CriticalDependencies);
    }

    [Fact]
    public async Task Registration_requires_an_explicit_solution_when_multiple_solutions_contain_the_plugin()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        temporary.WriteFile(
            "repository/Alternate.slnx",
            """
            <Solution>
              <Configurations>
                <BuildType Name="Debug" />
                <Platform Name="x64" />
              </Configurations>
              <Project Path="Sample/Sample.csproj" />
            </Solution>
            """);
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs")
        });

        CommandResult<ProjectRegistration> ambiguous = await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);
        CommandResult<ProjectRegistration> selected = await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(
                repository,
                ProjectAccessGrant.Full,
                "Sample/Sample.csproj",
                "Alternate.slnx",
                new BuildConfiguration("Debug", "x64")),
            CancellationToken.None);

        Assert.False(ambiguous.Succeeded);
        Assert.Contains("More than one solution", ambiguous.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.True(selected.Succeeded, selected.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal("Alternate.slnx", selected.Value!.BuildProfile.SolutionPath);
    }

    [Fact]
    public async Task Build_options_expose_multiple_Rhino_plugin_projects_for_Config_selection()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        temporary.WriteFile(
            "repository/Second/Second.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework><TargetExt>.rhp</TargetExt></PropertyGroup>
            </Project>
            """);
        temporary.WriteFile(
            "repository/Second/SecondPlugin.cs",
            """
            using System.Runtime.InteropServices;
            [Guid("25c3cc66-9a88-4e97-a9a5-650a7f63fb1a")]
            public sealed class SecondPlugin : Rhino.PlugIns.PlugIn { }
            """);
        temporary.WriteFile(
            "repository/Second.slnx",
            """
            <Solution>
              <Configurations><BuildType Name="Debug" /><Platform Name="x64" /></Configurations>
              <Project Path="Second/Second.csproj" />
            </Solution>
            """);
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs")
        });

        CommandResult<ProjectBuildOptions> result = await backend.DiscoverProjectBuildOptionsAsync(
            repository,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(
            new[]
            {
                Path.Combine("Sample", "Sample.csproj"),
                Path.Combine("Second", "Second.csproj")
            },
            result.Value!.Plugins.Select(plugin => plugin.PluginProjectPath).ToArray());
        Assert.Equal(
            new[] { "Sample.slnx" },
            result.Value.Plugins[0].Solutions.Select(solution => solution.SolutionPath).ToArray());
        Assert.Equal(
            new[] { "Second.slnx" },
            result.Value.Plugins[1].Solutions.Select(solution => solution.SolutionPath).ToArray());

        CommandResult<ProjectRegistration> selected = await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(
                repository,
                ProjectAccessGrant.Full,
                "Second/Second.csproj",
                "Second.slnx",
                new BuildConfiguration("Debug", "x64")),
            CancellationToken.None);

        Assert.True(selected.Succeeded, selected.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(Path.Combine("Second", "Second.csproj"), selected.Value!.BuildProfile.PluginProjectPath);
        Assert.Equal("Second.slnx", selected.Value.BuildProfile.SolutionPath);
    }

    [Fact]
    public async Task Clearing_remote_cache_does_not_touch_the_registered_project()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string remoteFile = temporary.PathFor("launcher/remotes/repository.git/cache.txt");
        temporary.WriteFile("launcher/remotes/repository.git/cache.txt", "remote");
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            RemotesDirectory = temporary.PathFor("launcher/remotes")
        });
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(temporary.PathFor("repository"), ProjectAccessGrant.Full),
            CancellationToken.None);

        CommandResult<bool> result = await backend.ClearRemoteCacheAsync(
            "repository",
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(remoteFile));
        Assert.True(File.Exists(temporary.PathFor("repository/file.txt")));
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
    public async Task Remote_refresh_reports_local_worktrees_while_the_remote_mirror_is_blocked()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string remote = temporary.CreateDirectory("remote.git");
        temporary.Run("git", remote, "init", "--bare", "--quiet");
        string repository = RepositoryFixture.Initialize(temporary, "repository");
        string branch = temporary.Run("git", repository, "branch", "--show-current").Trim();
        temporary.Run("git", repository, "remote", "add", "origin", remote);
        temporary.Run("git", repository, "push", "--quiet", "-u", "origin", branch);

        string remotesDirectory = temporary.CreateDirectory("launcher/remotes");
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            RemotesDirectory = remotesDirectory,
            GitHubExecutable = temporary.PathFor("missing-gh.exe")
        });
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);

        string remoteLockPath = Path.Combine(remotesDirectory, "repository.git.rwl.lock");
        FileStream remoteLock = new FileStream(
            remoteLockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        TaskCompletionSource<WorktreeRefreshProgress> listReported = new TaskCompletionSource<WorktreeRefreshProgress>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<WorktreeRefreshProgress> localReported = new TaskCompletionSource<WorktreeRefreshProgress>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<CommandResult<ProjectWorktrees>> refresh = backend.GetWorktreeSnapshotAsync(
            "repository",
            includeRemote: true,
            new ImmediateProgress<WorktreeRefreshProgress>(update =>
            {
                if (update.Stage == WorktreeRefreshStage.LocalList)
                    _ = listReported.TrySetResult(update);
                else if (update.Stage == WorktreeRefreshStage.Local)
                    _ = localReported.TrySetResult(update);
            }),
            timeout.Token);

        WorktreeRefreshProgress listed;
        WorktreeRefreshProgress local;
        try
        {
            listed = await listReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            local = await localReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(refresh.IsCompleted);
        }
        catch
        {
            await timeout.CancelAsync();
            throw;
        }
        finally
        {
            remoteLock.Dispose();
        }

        WorktreeSnapshot listedWorktree = Assert.Single(listed.Worktrees.Worktrees);
        WorktreeSnapshot localWorktree = Assert.Single(local.Worktrees.Worktrees);
        Assert.False(listedWorktree.HasLocalState);
        Assert.True(localWorktree.HasLocalState);
        Assert.False(localWorktree.HasGitState);

        CommandResult<ProjectWorktrees> result = await refresh;

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.True(Assert.Single(result.Value!.Worktrees).HasGitState);
    }

    [Fact]
    public async Task Local_refresh_validates_only_the_saved_build_configuration()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs")
        });
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);
        temporary.WriteFile("repository/Unrelated.slnx", "not a solution");

        CommandResult<ProjectWorktrees> result = await backend.GetWorktreeSnapshotAsync(
            "repository",
            includeRemote: false,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.True(Assert.Single(result.Value!.Worktrees).HasBuildConfiguration);
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
    public async Task Inspection_reports_an_unavailable_build_configuration_as_machine_readable_failure()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            RhinoExecutableResolver = _ => RhinoInstallation.AtDefaultLocation(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe"))
        });
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(temporary.PathFor("repository"), ProjectAccessGrant.Full),
            CancellationToken.None);
        File.Delete(temporary.PathFor("repository/Sample.slnx"));

        CommandResult<WorktreeInspection> result = await backend.InspectWorktreeAsync(
            temporary.PathFor("repository"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.Value!.CanLaunch);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "build_configuration_unavailable");
        Assert.Equal(DiagnosticSeverity.Error, result.Diagnostics[0].Severity);
    }

    // Which build Rhino loads at an ordinary start is the standing registration, and the
    // worktree holding that file is the registered one. A worktree nested under the primary
    // checkout lies under both, so the longest path match has to win.
    [Fact]
    public async Task The_worktree_containing_the_standing_registration_is_the_registered_one()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        string nested = NestedWorktree(temporary, repository);
        string registered = Path.Combine(nested, "Sample", "bin", "Debug", "net8.0", "Sample.rhp");
        LauncherBackend backend = Backend(
            temporary,
            new RegisteredPlugin(registered, RegistryHives.LocalMachine, MachineKeyPath));
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);

        CommandResult<ProjectWorktrees> result = await backend.GetWorktreeSnapshotAsync(
            "repository",
            includeRemote: false,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(registered, result.Value!.Registration!.Path);
        Assert.Equal(RegistryHives.LocalMachine, result.Value.Registration.Hive);
        Assert.True(Single(result.Value, nested).IsRegistered);
        Assert.False(Single(result.Value, repository).IsRegistered);
    }

    [Fact]
    public async Task A_registration_outside_every_worktree_marks_no_row_and_is_still_reported()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        string nested = NestedWorktree(temporary, repository);
        string registered = @"C:\Program Files\Rhino 8\Plug-ins\Sample.rhp";
        LauncherBackend backend = Backend(
            temporary,
            new RegisteredPlugin(registered, RegistryHives.LocalMachine, MachineKeyPath));
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);

        CommandResult<ProjectWorktrees> result = await backend.GetWorktreeSnapshotAsync(
            "repository",
            includeRemote: false,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(registered, result.Value!.Registration!.Path);
        Assert.DoesNotContain(result.Value.Worktrees, worktree => worktree.IsRegistered);
        Assert.False(Single(result.Value, nested).IsRegistered);
    }

    [Fact]
    public async Task No_registration_at_all_marks_no_row()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        _ = NestedWorktree(temporary, repository);
        LauncherBackend backend = Backend(temporary, registration: null);
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);

        CommandResult<ProjectWorktrees> result = await backend.GetWorktreeSnapshotAsync(
            "repository",
            includeRemote: false,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Null(result.Value!.Registration);
        Assert.DoesNotContain(result.Value.Worktrees, worktree => worktree.IsRegistered);
    }

    private const string MachineKeyPath =
        @"Software\McNeel\Rhinoceros\8.0\Plug-ins\735b6a53-ddc2-46e9-a82c-c0cd86d0609a";

    private static LauncherBackend Backend(
        TemporaryDirectory temporary,
        RegisteredPlugin? registration) => new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            GitHubExecutable = temporary.PathFor("missing-gh.exe"),
            StandingRegistrationReader = (_, _) => registration
        });

    private static string NestedWorktree(TemporaryDirectory temporary, string repository)
    {
        string nested = temporary.PathFor("repository/.claude/worktrees/nested");
        temporary.Run("git", repository, "worktree", "add", "--quiet", "-b", "nested", nested);
        return nested;
    }

    private static WorktreeSnapshot Single(ProjectWorktrees worktrees, string path) => Assert.Single(
        worktrees.Worktrees,
        worktree => string.Equals(
            worktree.Path,
            Path.GetFullPath(path),
            StringComparison.OrdinalIgnoreCase));
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
        temporary.WriteFile(
            $"{relativePath}/Sample/Sample.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <TargetExt>.rhp</TargetExt>
                <Platforms>AnyCPU;x64</Platforms>
              </PropertyGroup>
            </Project>
            """);
        temporary.WriteFile(
            $"{relativePath}/Sample/SamplePlugin.cs",
            """
            using System.Runtime.InteropServices;
            namespace Rhino.PlugIns { public class PlugIn { } }
            [Guid("735b6a53-ddc2-46e9-a82c-c0cd86d0609a")]
            public sealed class SamplePlugin : Rhino.PlugIns.PlugIn { }
            """);
        temporary.WriteFile(
            $"{relativePath}/Sample.slnx",
            """
            <Solution>
              <Configurations>
                <BuildType Name="Debug" />
                <BuildType Name="Release" />
                <Platform Name="Any CPU" />
                <Platform Name="x64" />
              </Configurations>
              <Project Path="Sample/Sample.csproj" />
            </Solution>
            """);
        temporary.Run("git", repository, "add", ".");
        temporary.Run("git", repository, "commit", "--quiet", "-m", "initial");
        return repository;
    }
}
