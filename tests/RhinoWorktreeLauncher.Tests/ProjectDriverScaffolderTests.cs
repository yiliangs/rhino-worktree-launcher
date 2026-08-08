using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class ProjectDriverScaffolderTests
{
    [Fact]
    public async Task Create_writes_the_starter_only_to_application_data_and_never_overwrites_it()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string repository = temporary.CreateDirectory("Sample Plugin");
        temporary.Run("git", repository, "init", "--quiet");
        string driverPath = temporary.PathFor("launcher/projects/sample-plugin/Driver.ps1");

        ProjectDriverCreation creation = await ProjectDriverScaffolder.CreateAsync(
            repository,
            driverPath,
            null,
            "sample-plugin",
            CancellationToken.None);

        Assert.True(creation.DriverCreated);
        Assert.Contains("Driver template not configured", await File.ReadAllTextAsync(creation.DriverPath));
        Assert.False(File.Exists(Path.Combine(repository, "tools", "rhino-worktree", "Driver.ps1")));

        const string customDriver = "Write-Output 'existing app driver'";
        await File.WriteAllTextAsync(creation.DriverPath, customDriver);
        ProjectDriverCreation recreated = await ProjectDriverScaffolder.CreateAsync(
            repository,
            driverPath,
            null,
            "sample-plugin",
            CancellationToken.None);

        Assert.False(recreated.DriverCreated);
        Assert.Equal(customDriver, await File.ReadAllTextAsync(recreated.DriverPath));
    }

    [Fact]
    public async Task Create_imports_a_legacy_driver_from_any_linked_worktree()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string repository = RepositoryFixture.Initialize(temporary, "repository", "Write-Output 'legacy'");
        string linked = temporary.PathFor("linked");
        temporary.Run("git", repository, "worktree", "add", "--quiet", "-b", "linked", linked);
        File.Delete(Path.Combine(repository, "tools", "rhino-worktree", "Driver.ps1"));
        string driverPath = temporary.PathFor("launcher/projects/repository/Driver.ps1");

        ProjectDriverCreation creation = await ProjectDriverScaffolder.CreateAsync(
            repository,
            driverPath,
            ProjectDriverScaffolder.LegacyDriverRelativePath,
            "repository",
            CancellationToken.None);

        Assert.True(creation.DriverCreated);
        Assert.Equal("Write-Output 'legacy'", await File.ReadAllTextAsync(driverPath));
        Assert.False(File.Exists(Path.Combine(repository, "tools", "rhino-worktree", "Driver.ps1")));
    }

    [Fact]
    public async Task Create_uses_the_built_in_Natalie_driver_without_repository_infrastructure()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string repository = temporary.CreateDirectory("natalie");
        temporary.Run("git", repository, "init", "--quiet");
        string driverPath = temporary.PathFor("launcher/projects/natalie/Driver.ps1");

        await ProjectDriverScaffolder.CreateAsync(
            repository,
            driverPath,
            null,
            "natalie",
            CancellationToken.None);

        string driver = await File.ReadAllTextAsync(driverPath);
        Assert.Contains("$request.worktreePath", driver);
        Assert.Contains("receipt_support_missing", driver);
        Assert.False(File.Exists(Path.Combine(repository, "tools", "rhino-worktree", "Driver.ps1")));
    }
}
