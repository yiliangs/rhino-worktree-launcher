namespace RhinoWorktreeLauncher;

using Rwl.Protocol;

public sealed class LauncherBackendOptions
{
    private static string DefaultDataRoot => Environment.GetEnvironmentVariable("RWL_DATA_ROOT") ??
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RhinoWorktreeLauncher");

    // Which adapter is running this backend. Every launch log and every lock holder record
    // names it, so a launch can be attributed to the host that asked for it.
    public string HostKind { get; init; } = "unknown";
    public string ReleaseId { get; init; } =
        typeof(LauncherBackendOptions).Assembly.GetName().Version?.ToString() ?? "unknown";
    public string CatalogPath { get; init; } = Path.Combine(DefaultDataRoot, "projects.json");
    // What every bootstrap resolves right now. A process that started before the last update
    // is still serving the release it resolved then, which is why doctor compares them.
    public string CurrentReleasePath { get; init; } = Path.Combine(DefaultDataRoot, "current.json");
    public string LogsDirectory { get; init; } = Path.Combine(DefaultDataRoot, "logs");
    public string LocksDirectory { get; init; } = Path.Combine(DefaultDataRoot, "locks");
    public string RemotesDirectory { get; init; } = Path.Combine(DefaultDataRoot, "remotes");
    public string GitExecutable { get; init; } = "git";
    public string GitHubExecutable { get; init; } = ResolveGitHubCliPath();
    public string DotNetExecutable { get; init; } = "dotnet";
    public Func<int, string> RhinoExecutableResolver { get; init; } = version => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        $"Rhino {version}",
        "System",
        "Rhino.exe");
    // The one seam over everything a launch does after the build. It runs in a process the
    // interactive Windows shell starts, because no registration may be written from a host
    // that can be sandboxed (ADR 0015).
    internal Func<LaunchExecutorRequest, IProgress<LaunchExecutorEvent>, CancellationToken,
        Task<LaunchExecutorEvent>> LaunchExecutorInvoker
    {
        get;
        init;
    } = LaunchExecutorClient.InvokeAsync;
    // Doctor's independent reader. It is spawned through the interactive shell, because the
    // condition it checks for is this process's own writes being intercepted.
    internal RegistryProbeRunner RegistryProbeRunner { get; init; } = BootstrapRegistryProbe.RunAsync;
    // The machine's process table, injectable so what doctor and instance attribution
    // conclude from a process list is decided by tests rather than by whatever happens to be
    // running.
    internal Func<IReadOnlyList<RunningProcess>> ProcessSnapshotReader { get; init; } = ProcessSnapshot.Read;
    // The plug-in artifacts one live process holds mapped in its address space. Same
    // mechanism as launch verification (ADR 0002), asked without an expected path.
    internal Func<int, IReadOnlyList<string>> MappedPlugInReader { get; init; } =
        RhinoInstanceReader.MappedPlugIns;
    internal Func<string, ProjectBuildOptions> ProjectBuildOptionsDiscovery { get; init; } =
        BuildProfileDiscovery.DiscoverOptions;
    // The registration Rhino resolves for a project's plug-in ID outside any launch. Reading
    // it in a host process is allowed; only mutation belongs to the executor (ADR 0015).
    internal Func<int, Guid, RegisteredPlugin?> StandingRegistrationReader { get; init; } =
        StandingRegistration.Read;

    private static string ResolveGitHubCliPath()
    {
        string installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "GitHub CLI",
            "gh.exe");
        return File.Exists(installed) ? installed : "gh";
    }
}
