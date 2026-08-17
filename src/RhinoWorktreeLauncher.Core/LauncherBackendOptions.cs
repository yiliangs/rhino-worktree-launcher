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
    public string LocksDirectory { get; init; } = Path.Combine(DefaultDataRoot, "locks");
    public string RemotesDirectory { get; init; } = Path.Combine(DefaultDataRoot, "remotes");
    public string GitExecutable { get; init; } = "git";
    public string GitHubExecutable { get; init; } = ResolveGitHubCliPath();
    public string DotNetExecutable { get; init; } = "dotnet";
    public Func<int, string, bool> FileInUseInspector { get; init; } = FileUse.IsFileMappedByProcess;
    public Func<int, string> RhinoExecutableResolver { get; init; } = version => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        $"Rhino {version}",
        "System",
        "Rhino.exe");
    public Func<ProcessStartInfo, Process> RhinoProcessStarter { get; init; } = RhinoProcessBroker.Start;
    // One seam owns the whole registration displacement: reading a registration and
    // displacing it are the same decision, so no second component re-reads a key this one
    // just read.
    internal Func<PluginNamespaceLeaseRequest, CancellationToken, Task<PluginNamespaceLeaseResult>>
        PluginNamespaceLeaseAcquirer
    {
        get;
        init;
    } = PluginNamespaceLease.AcquireAsync;
    internal Func<string, ProjectBuildOptions> ProjectBuildOptionsDiscovery { get; init; } =
        BuildProfileDiscovery.DiscoverOptions;

    private static string ResolveGitHubCliPath()
    {
        string installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "GitHub CLI",
            "gh.exe");
        return File.Exists(installed) ? installed : "gh";
    }
}
