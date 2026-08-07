namespace RhinoWorktreeLauncher;

using System.Diagnostics;

public sealed class LauncherBackendOptions
{
    private static string DefaultDataRoot => Environment.GetEnvironmentVariable("RWL_DATA_ROOT") ??
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RhinoWorktreeLauncher");

    public string CatalogPath { get; init; } = Path.Combine(DefaultDataRoot, "projects.json");
    public string LogsDirectory { get; init; } = Path.Combine(DefaultDataRoot, "logs");
    public string GitExecutable { get; init; } = "git";
    public string GitHubExecutable { get; init; } = ResolveGitHubCliPath();
    public string PowerShellExecutable { get; init; } = "pwsh";
    public Func<int, string> RhinoExecutableResolver { get; init; } = version => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        $"Rhino {version}",
        "System",
        "Rhino.exe");
    public Func<ProcessStartInfo, Process> RhinoProcessStarter { get; init; } = startInfo =>
        Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Rhino.");

    private static string ResolveGitHubCliPath()
    {
        string installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "GitHub CLI",
            "gh.exe");
        return File.Exists(installed) ? installed : "gh";
    }
}
