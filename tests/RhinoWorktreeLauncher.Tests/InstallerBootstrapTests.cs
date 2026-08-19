using System.IO;
using System.Linq;

namespace RhinoWorktreeLauncher.Tests;

/// <summary>
/// The entry point a downloaded release is opened with. It runs before anything this
/// solution builds, on a machine that has only what Windows ships, so its host assumptions
/// are asserted here rather than discovered by whoever double-clicks it.
///
/// These read the batch file rather than running it. Executing it would install over the
/// machine running the suite, and process-spawning tests here already fail to terminate on
/// a hosted runner (#65).
/// </summary>
public sealed class InstallerBootstrapTests
{
    [Fact]
    public void A_packaged_install_needs_no_script_host_at_all()
    {
        Assert.Contains(
            @"payload\bootstrap\rwl.exe",
            PayloadDefinition(),
            StringComparison.OrdinalIgnoreCase);

        // The payload's own bootstrap performs the install, so nothing on this path
        // depends on which PowerShell exists, and no execution policy governs it.
        string install = PackagedInstallLine();
        Assert.Contains("install", install, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pwsh", install, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", install, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_packaged_payload_is_preferred_over_the_source_checkout()
    {
        string[] lines = Lines();
        int payloadDefinition = Array.FindIndex(lines, line =>
            line.Contains(@"payload\bootstrap\rwl.exe", StringComparison.OrdinalIgnoreCase));
        int sourceScript = Array.FindIndex(lines, line =>
            line.Contains("Install-RhinoWorktreeLauncher.ps1", StringComparison.OrdinalIgnoreCase));

        Assert.True(payloadDefinition >= 0, "The installer never looks for a packaged payload.");
        Assert.True(sourceScript >= 0, "The installer lost its source-checkout path.");
        Assert.True(
            payloadDefinition < sourceScript,
            "A release archive must install from its own payload rather than fall into the " +
            "source path, which needs a toolchain an end user does not have.");
    }

    private static string PayloadDefinition() => Lines().FirstOrDefault(line =>
        line.TrimStart().StartsWith("set \"PAYLOAD_BOOTSTRAP=", StringComparison.OrdinalIgnoreCase)) ??
        throw new InvalidDataException("The installer never locates a packaged payload bootstrap.");

    [Fact]
    public void The_source_path_still_runs_where_only_windows_powershell_exists()
    {
        // Building from source needs PowerShell, so that path keeps the host resolution
        // and the policy bypass that #69 established.
        string installer = Installer();
        Assert.Contains("powershell.exe", installer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe",
            installer,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-ExecutionPolicy Bypass", installer, StringComparison.OrdinalIgnoreCase);
    }

    private static string PackagedInstallLine() => Lines().FirstOrDefault(line =>
        line.TrimStart().StartsWith("\"%PAYLOAD_BOOTSTRAP%\"", StringComparison.OrdinalIgnoreCase)) ??
        throw new InvalidDataException("The installer does not invoke a packaged payload bootstrap.");

    private static string[] Lines() => Installer().Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

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
