using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class WorktreeWorkspaceTests
{
    [Fact]
    public async Task Imported_driver_executes_its_app_copy_and_must_return_app_workspace_artifacts()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        string driverPath = temporary.PathFor("selected/Driver.ps1");
        temporary.WriteFile(
            "selected/Driver.ps1",
            """
            param([Parameter(Mandatory = $true)][string]$RequestPath)
            $request = Get-Content -Raw -LiteralPath $RequestPath | ConvertFrom-Json
            $package = Join-Path $request.buildPath 'package'
            New-Item -ItemType Directory -Force -Path $package | Out-Null
            $plugin = Join-Path $package 'Custom.rhp'
            Set-Content -LiteralPath $plugin -Value 'custom artifact'
            [ordered]@{
                protocolVersion = 2
                kind = 'result'
                success = $true
                pluginId = '9a436ad0-1f46-4927-b7ca-832be2c5c791'
                packageDirectory = $package
                pluginPath = $plugin
                rhinoRuntime = 'netfx'
                criticalDependencies = @()
            } | ConvertTo-Json -Compress
            """);
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            WorkspacesDirectory = temporary.PathFor("launcher/workspaces")
        });
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full, driverPath),
            CancellationToken.None);
        File.Delete(driverPath);

        CommandResult<PreparedLaunchArtifacts> result = await backend.BuildWorktreeAsync(
            repository,
            progress: null,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(Guid.Parse("9a436ad0-1f46-4927-b7ca-832be2c5c791"), result.Value!.PluginId);
        Assert.Equal("custom artifact", (await File.ReadAllTextAsync(result.Value.PluginPath)).Trim());
        Assert.StartsWith(
            result.Value.Workspace.BuildPath,
            result.Value.PluginPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(repository, "package")));
    }

    [Fact]
    public async Task Typed_profile_builds_and_resolves_the_plugin_only_in_the_app_workspace()
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
                <TargetFramework>net8.0</TargetFramework>
                <TargetExt>.rhp</TargetExt>
              </PropertyGroup>
            </Project>
            """);
        temporary.WriteFile(
            "sample-plugin/Sample/SamplePlugin.cs",
            """
            using System.Runtime.InteropServices;
            namespace Rhino.PlugIns { public class PlugIn { } }
            [Guid("2dd1e9eb-d475-43b7-95b5-60037504ea7e")]
            public sealed class SamplePlugin : Rhino.PlugIns.PlugIn { }
            """);
        temporary.Run("git", repository, "add", ".");
        temporary.Run("git", repository, "commit", "--quiet", "-m", "initial");
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            WorkspacesDirectory = temporary.PathFor("launcher/workspaces")
        });
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);

        CommandResult<PreparedLaunchArtifacts> result = await backend.BuildWorktreeAsync(
            repository,
            progress: null,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(Guid.Parse("2dd1e9eb-d475-43b7-95b5-60037504ea7e"), result.Value!.PluginId);
        Assert.EndsWith("Sample.rhp", result.Value.PluginPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(
            Path.GetFullPath(temporary.PathFor("launcher/workspaces")),
            result.Value.PluginPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(repository, "Sample", "bin")));
        Assert.False(Directory.Exists(Path.Combine(repository, "Sample", "obj")));
    }

    [Fact]
    public async Task Moving_a_linked_worktree_preserves_its_workspace_identity()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string repository = temporary.PathFor("repository");
        string linked = temporary.PathFor("linked-before");
        string moved = temporary.PathFor("linked-after");
        temporary.Run("git", repository, "worktree", "add", "--quiet", "-b", "movable", linked);
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            WorkspacesDirectory = temporary.PathFor("launcher/workspaces")
        });
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);
        CommandResult<WorktreeWorkspace> before = await backend.PrepareWorktreeAsync(
            linked,
            CancellationToken.None);

        temporary.Run("git", repository, "worktree", "move", linked, moved);
        CommandResult<WorktreeWorkspace> after = await backend.PrepareWorktreeAsync(
            moved,
            CancellationToken.None);

        Assert.True(before.Succeeded);
        Assert.True(after.Succeeded);
        Assert.Equal(before.Value!.WorktreeId, after.Value!.WorktreeId);
        Assert.Equal(before.Value.BuildPath, after.Value.BuildPath);
    }

    [Fact]
    public async Task Workspace_reconciles_git_visible_source_without_mutating_the_repository()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string repository = temporary.CreateDirectory("repository");
        temporary.Run("git", repository, "init", "--quiet");
        temporary.Run("git", repository, "config", "user.email", "tests@example.com");
        temporary.Run("git", repository, "config", "user.name", "RWL Tests");
        temporary.WriteFile("repository/.gitignore", "ignored.txt\nbin/\n");
        temporary.WriteFile("repository/tracked.txt", "committed");
        temporary.WriteFile("repository/deleted-later.txt", "present");
        temporary.Run("git", repository, "add", ".");
        temporary.Run("git", repository, "commit", "--quiet", "-m", "initial");
        temporary.WriteFile("repository/tracked.txt", "modified");
        temporary.WriteFile("repository/untracked.txt", "untracked");
        temporary.WriteFile("repository/ignored.txt", "secret");
        string indexPath = Path.Combine(repository, ".git", "index");
        DateTime indexWriteTime = File.GetLastWriteTimeUtc(indexPath);
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            WorkspacesDirectory = temporary.PathFor("launcher/workspaces")
        });
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(repository, ProjectAccessGrant.Full),
            CancellationToken.None);

        CommandResult<WorktreeWorkspace> first = await backend.PrepareWorktreeAsync(
            repository,
            CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.Equal("modified", await File.ReadAllTextAsync(Path.Combine(first.Value!.SourcePath, "tracked.txt")));
        Assert.Equal("untracked", await File.ReadAllTextAsync(Path.Combine(first.Value.SourcePath, "untracked.txt")));
        Assert.False(File.Exists(Path.Combine(first.Value.SourcePath, "ignored.txt")));
        string cachePath = Path.Combine(first.Value.BuildPath, "bin", "cache.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        await File.WriteAllTextAsync(cachePath, "cache");

        File.Delete(Path.Combine(repository, "deleted-later.txt"));
        CommandResult<WorktreeWorkspace> second = await backend.PrepareWorktreeAsync(
            repository,
            CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.Equal(first.Value.WorktreeId, second.Value!.WorktreeId);
        Assert.Equal(first.Value.SourcePath, second.Value.SourcePath);
        Assert.False(File.Exists(Path.Combine(second.Value.SourcePath, "deleted-later.txt")));
        Assert.False(File.Exists(Path.Combine(second.Value.BuildPath, "deleted-later.txt")));
        Assert.True(File.Exists(Path.Combine(second.Value.BuildPath, "bin", "cache.dat")));
        Assert.Equal(indexWriteTime, File.GetLastWriteTimeUtc(indexPath));
    }
}
