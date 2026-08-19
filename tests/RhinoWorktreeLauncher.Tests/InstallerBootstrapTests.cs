using System.IO;

namespace RhinoWorktreeLauncher.Tests;

/// <summary>
/// The entry point a downloaded release is opened with. It runs before anything this
/// solution builds, on a machine that has only what Windows ships, so its two host
/// assumptions are asserted here rather than discovered by whoever double-clicks it.
///
/// These read the batch file rather than running it. Executing it would spawn a real
/// PowerShell host, and process-spawning tests in this suite already fail to terminate on
/// a hosted runner (#65), so this stays a document assertion until that is resolved.
/// </summary>
public sealed class InstallerBootstrapTests
{
    [Fact]
    public void The_installer_runs_where_only_windows_powershell_exists()
    {
        string installer = Installer();

        // PowerShell 7 is a separate product. A stock Windows machine resolves
        // powershell.exe and nothing named pwsh, so naming pwsh alone strands the user
        // on "'pwsh' is not recognized" before any of this ever runs.
        Assert.Contains("powershell.exe", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe",
            installer,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_installer_is_not_stopped_by_the_default_execution_policy()
    {
        // The Windows client default is Restricted, and a script extracted from a
        // downloaded archive carries a zone mark that RemoteSigned also refuses. Either
        // one blocks the install script unless the host is told otherwise.
        Assert.Contains("-ExecutionPolicy Bypass", Installer(), StringComparison.OrdinalIgnoreCase);
    }

    private static string Installer() => File.ReadAllText(Path.Combine(RepositoryRoot(), "Install.bat"));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RhinoWorktreeLauncher.slnx")))
            directory = directory.Parent;
        if (directory is null)
            throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
        return directory.FullName;
    }
}
