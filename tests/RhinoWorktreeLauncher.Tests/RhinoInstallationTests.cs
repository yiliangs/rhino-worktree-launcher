using System.Runtime.Versioning;

namespace RhinoWorktreeLauncher.Tests;

/// <summary>
/// Where RWL looks for Rhino. The installer's record is injected and every candidate path is
/// a real file inside a temporary directory, so these decide the rule rather than whatever
/// the machine running the tests happens to have installed, and nothing here reads the
/// machine's registry.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RhinoInstallationTests
{
    // The reason the registry read exists: Rhino installed anywhere but Program Files is
    // discoverable no other way, and the default location is not evidence of anything.
    [Fact]
    public void The_installer_record_locates_a_rhino_outside_the_default_location()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string recorded = Executable(temporary, "elsewhere/Rhino 8/System");
        string standard = Executable(temporary, "ProgramFiles/Rhino 8/System");

        RhinoInstallation installation = RhinoInstallation.Resolve(8, Records(recorded), standard);

        Assert.Equal(recorded, installation.ExecutablePath);
        Assert.Equal(RhinoExecutableSource.InstallerRecord, installation.Source);
        Assert.Contains("the location the Rhino installer recorded", installation.DescribeFound());
    }

    [Fact]
    public void The_default_location_answers_when_the_installer_recorded_nothing()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string standard = Executable(temporary, "ProgramFiles/Rhino 8/System");

        RhinoInstallation installation = RhinoInstallation.Resolve(8, _ => null, standard);

        Assert.Equal(standard, installation.ExecutablePath);
        Assert.Equal(RhinoExecutableSource.DefaultLocation, installation.Source);
    }

    // A record left behind by an installation that was moved or removed is not an answer,
    // because the file it names cannot be started.
    [Fact]
    public void A_recorded_path_that_no_longer_exists_yields_to_the_default_location()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string standard = Executable(temporary, "ProgramFiles/Rhino 8/System");
        string missing = temporary.PathFor("elsewhere/Removed/System/Rhino.exe");

        RhinoInstallation installation = RhinoInstallation.Resolve(8, Records(missing), standard);

        Assert.Equal(standard, installation.ExecutablePath);
        Assert.Equal(RhinoExecutableSource.DefaultLocation, installation.Source);
    }

    // With nothing to launch, the answer is only there to be read in a diagnostic, so it
    // names the place the installer claimed rather than a default nobody ever chose.
    [Fact]
    public void Nothing_launchable_names_the_location_the_installer_recorded()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string missing = temporary.PathFor("elsewhere/Removed/System/Rhino.exe");
        string standard = temporary.PathFor("ProgramFiles/Rhino 8/System/Rhino.exe");

        RhinoInstallation installation = RhinoInstallation.Resolve(8, Records(missing), standard);

        Assert.Equal(missing, installation.ExecutablePath);
        Assert.Equal(RhinoExecutableSource.InstallerRecord, installation.Source);
        Assert.Contains("the location the Rhino installer recorded", installation.DescribeMissing());
    }

    [Fact]
    public void Nothing_recorded_and_nothing_installed_names_the_default_location()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string standard = temporary.PathFor("ProgramFiles/Rhino 9/System/Rhino.exe");

        RhinoInstallation installation = RhinoInstallation.Resolve(9, _ => null, standard);

        Assert.Equal(standard, installation.ExecutablePath);
        Assert.Equal(RhinoExecutableSource.DefaultLocation, installation.Source);
        Assert.Contains("the default installation location", installation.DescribeMissing());
    }

    [Fact]
    public void The_recorded_system_directory_answers_when_no_executable_path_was_recorded()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string recorded = Executable(temporary, "elsewhere/Rhino 8/System");
        RhinoInstallerRecord record = new RhinoInstallerRecord(
            ExePath: null,
            SystemDirectory: temporary.PathFor("elsewhere/Rhino 8/System"),
            InstallDirectory: null);

        RhinoInstallation installation = RhinoInstallation.Resolve(
            8,
            _ => record,
            temporary.PathFor("ProgramFiles/Rhino 8/System/Rhino.exe"));

        Assert.Equal(recorded, installation.ExecutablePath);
        Assert.Equal(RhinoExecutableSource.InstallerRecord, installation.Source);
    }

    [Fact]
    public void The_recorded_install_root_answers_when_it_is_the_only_location_recorded()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string recorded = Executable(temporary, "elsewhere/Rhino 8/System");
        RhinoInstallerRecord record = new RhinoInstallerRecord(
            ExePath: null,
            SystemDirectory: null,
            InstallDirectory: temporary.PathFor("elsewhere/Rhino 8"));

        RhinoInstallation installation = RhinoInstallation.Resolve(
            8,
            _ => record,
            temporary.PathFor("ProgramFiles/Rhino 8/System/Rhino.exe"));

        Assert.Equal(recorded, installation.ExecutablePath);
        Assert.Equal(RhinoExecutableSource.InstallerRecord, installation.Source);
    }

    // A path on its own does not tell a reader whether the installation moved or was never
    // recorded, and those have different remedies, so the check says which answer it shows.
    [Fact]
    public async Task Doctor_says_where_the_rhino_path_came_from()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string recorded = Executable(temporary, "elsewhere/Rhino 8/System");
        LauncherBackend backend = Backend(
            temporary,
            _ => RhinoInstallation.FromInstallerRecord(recorded));
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(temporary.PathFor("repository"), ProjectAccessGrant.Full),
            CancellationToken.None);

        CommandResult<DoctorReport> result = await backend.RunDoctorAsync(CancellationToken.None);

        DoctorCheck check = Assert.Single(
            result.Value!.Checks,
            candidate => candidate.Name.StartsWith("rhino:", StringComparison.Ordinal));
        Assert.True(check.Passed);
        Assert.Contains(recorded, check.Message, StringComparison.Ordinal);
        Assert.Contains(
            "the location the Rhino installer recorded",
            check.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_says_a_missing_rhino_was_looked_for_in_the_default_location()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string missing = temporary.PathFor("ProgramFiles/Rhino 8/System/Rhino.exe");
        LauncherBackend backend = Backend(
            temporary,
            _ => RhinoInstallation.AtDefaultLocation(missing));
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(temporary.PathFor("repository"), ProjectAccessGrant.Full),
            CancellationToken.None);

        CommandResult<DoctorReport> result = await backend.RunDoctorAsync(CancellationToken.None);

        DoctorCheck check = Assert.Single(
            result.Value!.Checks,
            candidate => candidate.Name.StartsWith("rhino:", StringComparison.Ordinal));
        Assert.False(check.Passed);
        Assert.Contains($"Rhino was not found at '{missing}'", check.Message, StringComparison.Ordinal);
        Assert.Contains("the default installation location", check.Message, StringComparison.Ordinal);
    }

    private static LauncherBackend Backend(
        TemporaryDirectory temporary,
        Func<int, RhinoInstallation> resolver) => new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LocksDirectory = temporary.PathFor("launcher/locks"),
            CurrentReleasePath = temporary.PathFor("launcher/current.json"),
            RegistryProbeRunner = TestRegistryProbe.Truthful,
            ProcessSnapshotReader = () => Array.Empty<RunningProcess>(),
            RhinoExecutableResolver = resolver
        });

    private static Func<int, RhinoInstallerRecord?> Records(string executablePath) =>
        _ => new RhinoInstallerRecord(executablePath, SystemDirectory: null, InstallDirectory: null);

    private static string Executable(TemporaryDirectory temporary, string relativeDirectory)
    {
        temporary.WriteFile($"{relativeDirectory}/Rhino.exe", "not a real Rhino");
        return temporary.PathFor($"{relativeDirectory}/Rhino.exe");
    }
}
