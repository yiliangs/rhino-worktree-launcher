namespace RhinoWorktreeLauncher;

public sealed class LauncherBackend
{
    private readonly ProjectCatalog _catalog;
    private readonly ContextResolver _contextResolver;
    private readonly RemoteMirrorStore _remoteMirrors;
    private readonly WorktreeScanner _scanner;
    private readonly BuildCoordinator _buildCoordinator;
    private readonly LaunchCoordinator _launchCoordinator;

    public LauncherBackend(LauncherBackendOptions? options = null)
    {
        Options = options ?? new LauncherBackendOptions();
        _catalog = new ProjectCatalog(Options.CatalogPath);
        _contextResolver = new ContextResolver(_catalog);
        _remoteMirrors = new RemoteMirrorStore(Options);
        _scanner = new WorktreeScanner(Options, _remoteMirrors);
        _buildCoordinator = new BuildCoordinator(Options, _contextResolver);
        _launchCoordinator = new LaunchCoordinator(Options, _contextResolver, _buildCoordinator);
    }

    public LauncherBackendOptions Options { get; }

    public async Task<CommandResult<ProjectRegistration>> RegisterProjectAsync(
        ProjectRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Access.ReadProject)
        {
            return CommandResult<ProjectRegistration>.Failure(new Diagnostic(
                "project_read_consent_required",
                "Project-wide read consent is required before RWL can inspect or register this Git project."));
        }

        try
        {
            ProjectRegistration registration = await _catalog.RegisterAsync(
                request.RepositoryPath,
                request.Access,
                request.PluginProjectPath,
                request.SolutionPath,
                request.BuildConfiguration,
                request.LaunchMode,
                cancellationToken);
            return CommandResult<ProjectRegistration>.Success(registration);
        }
        catch (Exception exception)
        {
            return CommandResult<ProjectRegistration>.Failure(new Diagnostic(
                "registration_failed",
                exception.Message));
        }
    }

    public async Task<CommandResult<bool>> RemoveProjectAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _catalog.RemoveAsync(projectId, cancellationToken);
            return CommandResult<bool>.Success(true);
        }
        catch (Exception exception)
        {
            return CommandResult<bool>.Failure(new Diagnostic("remove_failed", exception.Message));
        }
    }

    public Task<CommandResult<ResolvedContext>> ResolveContextAsync(
        string path,
        CancellationToken cancellationToken) => _contextResolver.ResolveAsync(path, cancellationToken);

    public async Task<CommandResult<ProjectRegistration>> UpdateProjectConfigAsync(
        ProjectConfigRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return CommandResult<ProjectRegistration>.Success(
                await _catalog.UpdateConfigAsync(request, cancellationToken));
        }
        catch (Exception exception)
        {
            return CommandResult<ProjectRegistration>.Failure(new Diagnostic(
                "project_config_failed",
                exception.Message));
        }
    }

    public async Task<CommandResult<bool>> ClearRemoteCacheAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        CommandResult<ProjectSnapshot> project = await GetProjectSnapshotAsync(projectId, cancellationToken);
        if (!project.Succeeded)
            return CommandResult<bool>.Failure(project.Diagnostics.ToArray());

        try
        {
            await _remoteMirrors.ClearAsync(projectId, cancellationToken);
            return CommandResult<bool>.Success(true);
        }
        catch (Exception exception)
        {
            return CommandResult<bool>.Failure(new Diagnostic("cache_clear_failed", exception.Message));
        }
    }

    public async Task<CommandResult<ProjectBuildOptions>> DiscoverProjectBuildOptionsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProjectBuildOptions options = await Task.Run(
                () => Options.ProjectBuildOptionsDiscovery(path),
                cancellationToken).ConfigureAwait(false);
            return CommandResult<ProjectBuildOptions>.Success(options);
        }
        catch (Exception exception)
        {
            return CommandResult<ProjectBuildOptions>.Failure(new Diagnostic(
                "build_configuration_discovery_failed",
                exception.Message));
        }
    }

    public async Task<CommandResult<ProjectCatalogView>> GetProjectsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            ProjectCatalogView view = await _catalog.LoadViewAsync(cancellationToken);
            return CommandResult<ProjectCatalogView>.Success(
                view,
                view.Projects.SelectMany(project => project.Diagnostics).ToArray());
        }
        catch (Exception exception)
        {
            return CommandResult<ProjectCatalogView>.Failure(new Diagnostic(
                "catalog_read_failed",
                exception.Message));
        }
    }

    public async Task<CommandResult<bool>> RecordProjectSelectionAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _catalog.RecordSelectionAsync(projectId, cancellationToken);
            return CommandResult<bool>.Success(true);
        }
        catch (Exception exception)
        {
            return CommandResult<bool>.Failure(new Diagnostic(
                "project_selection_not_recorded",
                exception.Message));
        }
    }

    public async Task<CommandResult<ProjectSnapshot>> GetProjectSnapshotAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        ProjectSnapshot? project = (await _catalog.LoadAsync(cancellationToken)).FirstOrDefault(candidate =>
            string.Equals(candidate.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
        return project is null
            ? CommandResult<ProjectSnapshot>.Failure(new Diagnostic(
                "project_not_registered",
                $"Project '{projectId}' is not registered."))
            : CommandResult<ProjectSnapshot>.Success(project, project.Diagnostics);
    }

    public Task<CommandResult<ProjectWorktrees>> GetWorktreeSnapshotAsync(
        string projectId,
        bool includeRemote,
        CancellationToken cancellationToken) => GetWorktreeSnapshotAsync(
            projectId,
            includeRemote,
            progress: null,
            cancellationToken);

    public async Task<CommandResult<ProjectWorktrees>> GetWorktreeSnapshotAsync(
        string projectId,
        bool includeRemote,
        IProgress<WorktreeRefreshProgress>? progress,
        CancellationToken cancellationToken)
    {
        CommandResult<ProjectSnapshot> projectResult = await GetProjectSnapshotAsync(
            projectId,
            cancellationToken);
        if (!projectResult.Succeeded ||
            projectResult.Value is null ||
            projectResult.Value.Availability != ProjectAvailability.Available)
        {
            return CommandResult<ProjectWorktrees>.Failure(
                projectResult.Diagnostics.ToArray());
        }

        return await _scanner.ScanAsync(
            projectResult.Value,
            includeRemote,
            progress,
            cancellationToken);
    }

    public async Task<CommandResult<WorktreeInspection>> InspectWorktreeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        CommandResult<ResolvedContext> contextResult = await ResolveContextAsync(path, cancellationToken);
        if (!contextResult.Succeeded)
            return CommandResult<WorktreeInspection>.Failure(contextResult.Diagnostics.ToArray());

        ResolvedContext context = contextResult.Value!;
        string rhinoPath = Options.RhinoExecutableResolver(context.RhinoVersion);
        List<Diagnostic> diagnostics = new List<Diagnostic>();
        if (!context.BuildProfile.IsConfigured)
        {
            diagnostics.Add(new Diagnostic(
                "build_configuration_incomplete",
                "Choose a canonical solution build configuration in Config."));
        }
        else
        {
            try
            {
                _ = BuildProfileResolver.Resolve(
                    context.WorktreePath,
                    context.BuildProfile,
                    BuildProfileResolutionMode.RediscoverCanonicalSelection,
                    context.BuildProfile.LaunchMode);
            }
            catch (Exception exception)
            {
                diagnostics.Add(new Diagnostic("build_configuration_unavailable", exception.Message));
            }
        }
        if (!File.Exists(rhinoPath))
            diagnostics.Add(new Diagnostic("rhino_missing", $"Rhino was not found at '{rhinoPath}'."));

        WorktreeInspection inspection = new WorktreeInspection(
            context.ProjectId,
            context.WorktreePath,
            Options.CatalogPath,
            rhinoPath,
            context.IsPrimary,
            diagnostics.Count == 0);
        return CommandResult<WorktreeInspection>.Success(inspection, diagnostics);
    }

    /// <summary>
    /// Which live Rhino processes exist and which plug-in artifacts each one holds mapped.
    /// Concurrent launches legitimately leave several verified Rhino instances running, each
    /// a different build, so this is how a caller binds an interaction to the right one when
    /// it does not already hold the launch result's process id.
    /// </summary>
    public async Task<CommandResult<RhinoInstanceAttribution>> DescribeRhinoInstancesAsync(
        CancellationToken cancellationToken)
    {
        RhinoInstanceAttribution attribution;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            attribution = await Task.Run(
                () => RhinoInstanceReader.Describe(
                    Options.ProcessSnapshotReader(),
                    Options.MappedPlugInReader),
                cancellationToken).ConfigureAwait(false);
        }
        // Without the process table there is no partial answer to give: a list missing an
        // unknown number of Rhino processes is worse than a named refusal to answer.
        catch (Exception exception)
        {
            return CommandResult<RhinoInstanceAttribution>.Failure(new Diagnostic(
                "rhino_instance_scan_failed",
                $"The live Rhino processes could not be attributed: {exception.Message}"));
        }

        return CommandResult<RhinoInstanceAttribution>.Success(attribution, Unattributable(attribution));
    }

    public Task<CommandResult<LaunchResult>> LaunchAsync(
        string path,
        LaunchMode launchMode,
        TimeSpan timeout,
        IProgress<LaunchProgress>? progress,
        CancellationToken cancellationToken) => _launchCoordinator.LaunchAsync(
            path,
            launchMode,
            timeout,
            progress,
            cancellationToken);

    public async Task<CommandResult<DoctorReport>> RunDoctorAsync(CancellationToken cancellationToken)
    {
        List<DoctorCheck> checks = new List<DoctorCheck>();
        await CheckProcessAsync("git", Options.GitExecutable, new[] { "--version" });
        await CheckProcessAsync("dotnet", Options.DotNetExecutable, new[] { "--version" });
        checks.Add(await CheckRegistryVisibilityAsync(cancellationToken));
        IReadOnlyList<RunningProcess>? snapshot = null;
        string? snapshotFailure = null;
        try
        {
            snapshot = Options.ProcessSnapshotReader();
        }
        // Both process checks below are answers about the same table, so the failure to read
        // it is recorded once here and named by each of them.
        catch (Exception exception)
        {
            snapshotFailure = exception.Message;
        }
        checks.AddRange(CheckProcesses(snapshot, snapshotFailure));
        checks.Add(CheckRhinoInstances(snapshot, snapshotFailure));

        CommandResult<ProjectCatalogView> catalogResult = await GetProjectsAsync(cancellationToken);
        IReadOnlyList<ProjectSnapshot> projects = catalogResult.Value?.Projects ??
            Array.Empty<ProjectSnapshot>();
        checks.Add(new DoctorCheck(
            "catalog",
            catalogResult.Succeeded,
            catalogResult.Succeeded
                ? $"{projects.Count} project(s) registered."
                : catalogResult.Diagnostics[0].Message,
            catalogResult.Succeeded ? DiagnosticSeverity.Info : DiagnosticSeverity.Error));
        foreach (ProjectSnapshot project in projects)
        {
            checks.Add(new DoctorCheck(
                $"project:{project.ProjectId}",
                project.Availability == ProjectAvailability.Available,
                project.Availability == ProjectAvailability.Available
                    ? $"{project.DisplayName} canonical solution configuration is available."
                    : string.Join(" ", project.Diagnostics.Select(diagnostic => diagnostic.Message)),
                project.Availability == ProjectAvailability.Available
                    ? DiagnosticSeverity.Info
                    : DiagnosticSeverity.Error));
            string rhinoPath = Options.RhinoExecutableResolver(project.Registration.RhinoVersion);
            checks.Add(new DoctorCheck(
                $"rhino:{project.Registration.RhinoVersion}",
                File.Exists(rhinoPath),
                File.Exists(rhinoPath) ? rhinoPath : $"Rhino was not found at '{rhinoPath}'.",
                File.Exists(rhinoPath) ? DiagnosticSeverity.Info : DiagnosticSeverity.Error));
        }

        DoctorReport report = new DoctorReport(
            checks.All(check => check.Passed || check.Severity != DiagnosticSeverity.Error),
            checks,
            projects);
        IReadOnlyList<Diagnostic> diagnostics = checks
            .Where(check => !check.Passed)
            .Select(check => new Diagnostic(
                $"doctor_{check.Name.Replace(':', '_')}_failed",
                check.Message,
                check.Severity))
            .ToArray();
        return CommandResult<DoctorReport>.Success(report, diagnostics);

        async Task<DoctorCheck> CheckRegistryVisibilityAsync(CancellationToken token)
        {
            try
            {
                RegistryVisibility visibility = await RegistryVisibilityCanary.VerifyAsync(
                    Options.RegistryProbeRunner,
                    spawnInteractively: true,
                    token);
                return new DoctorCheck(
                    "registry-visibility",
                    visibility.Visible,
                    visibility.Visible
                        ? "A current-user registry write made here is visible to an independent process."
                        : visibility.Describe(),
                    visibility.Visible ? DiagnosticSeverity.Info : DiagnosticSeverity.Error);
            }
            // Without the interactive shell there is no independent reader to ask, so this
            // reports that it could not check rather than that the check failed. Launches
            // refuse under the same condition, by name, before they touch a registration.
            catch (LaunchDiagnosticException exception)
            {
                return new DoctorCheck(
                    "registry-visibility",
                    false,
                    $"[{exception.Code}] {exception.Message}",
                    DiagnosticSeverity.Warning);
            }
        }

        // What RWL has running, which of it nobody can reach, and which of it is serving a
        // release the installation has already replaced. Doctor reports; it never ends a
        // process it found.
        IEnumerable<DoctorCheck> CheckProcesses(
            IReadOnlyList<RunningProcess>? snapshot,
            string? snapshotFailure)
        {
            if (snapshot is null)
            {
                return new[]
                {
                    new DoctorCheck(
                        "processes",
                        false,
                        $"The live RWL processes could not be listed: {snapshotFailure}",
                        DiagnosticSeverity.Error)
                };
            }

            string? currentReleaseId = null;
            string? releaseUnavailable = null;
            try
            {
                currentReleaseId = RwlProcessInventory.ReadCurrentReleaseId(Options.CurrentReleasePath);
            }
            // Not being able to name the installed release is only a finding when something is
            // running from a release directory to compare against it, which is decided below.
            catch (Exception exception)
            {
                releaseUnavailable = exception.Message;
            }

            IReadOnlyList<RwlProcess> processes = RwlProcessInventory.Describe(snapshot, currentReleaseId);
            List<DoctorCheck> processChecks = new List<DoctorCheck>
            {
                new DoctorCheck(
                    "processes",
                    true,
                    $"{processes.Count} live RWL process(es); installed release " +
                    $"{currentReleaseId ?? "unknown"}." +
                    string.Concat(processes.Select(process => $" {process.Describe()}.")),
                    DiagnosticSeverity.Info)
            };
            foreach (RwlProcess process in processes.Where(process => process.IsOrphan || process.ReleaseIsStale))
            {
                processChecks.Add(new DoctorCheck(
                    $"process:{process.ProcessId}",
                    false,
                    $"{process.Describe()}. {string.Join(" ", Findings(process, currentReleaseId))}",
                    DiagnosticSeverity.Warning));
            }
            if (releaseUnavailable is not null && processes.Any(process => process.ReleaseId is not null))
            {
                processChecks.Add(new DoctorCheck(
                    "processes:release",
                    false,
                    $"{releaseUnavailable} RWL processes are running from release directories, so " +
                    "one of them may be serving code this installation has replaced, and that " +
                    "cannot be checked until the pointer is readable. Reinstall RWL.",
                    DiagnosticSeverity.Warning));
            }
            return processChecks;
        }

        // Which Rhino runs which build. Several live Rhino processes are the ordinary result
        // of concurrent launches, so this reports and never warns; only a table it cannot
        // read is a failure, and a Rhino it cannot attribute is named in the line.
        DoctorCheck CheckRhinoInstances(
            IReadOnlyList<RunningProcess>? snapshot,
            string? snapshotFailure)
        {
            if (snapshot is null)
            {
                return new DoctorCheck(
                    "rhino-instances",
                    false,
                    $"The live Rhino processes could not be attributed: {snapshotFailure}",
                    DiagnosticSeverity.Error);
            }

            RhinoInstanceAttribution attribution = RhinoInstanceReader.Describe(
                snapshot,
                Options.MappedPlugInReader);
            return new DoctorCheck(
                "rhino-instances",
                true,
                attribution.Describe(),
                DiagnosticSeverity.Info);
        }

        IEnumerable<string> Findings(RwlProcess process, string? currentReleaseId)
        {
            if (process.IsOrphan)
            {
                yield return $"It is orphaned: process {process.ParentProcessId}, which started it " +
                    "and bridged its client's standard streams, is gone, so nobody can reach it " +
                    "and nothing reads what it answers. End it from Task Manager. RWL servers now " +
                    "end with their session, so an orphan means a process from a release older " +
                    "than that change.";
            }
            if (process.ReleaseIsStale)
            {
                yield return $"It is serving release {process.ReleaseId} while the installed " +
                    $"release is {currentReleaseId}. A process resolves its executable once, when " +
                    "it starts, so it keeps serving that release until it ends. Restart the " +
                    "client that owns it to pick up the installed one.";
            }
        }

        async Task CheckProcessAsync(string name, string executable, string[] arguments)
        {
            try
            {
                string output = await ProcessRunner.RunAsync(
                    executable,
                    Environment.CurrentDirectory,
                    arguments,
                    cancellationToken);
                checks.Add(new DoctorCheck(
                    name,
                    true,
                    output.Trim(),
                    DiagnosticSeverity.Info));
            }
            catch (Exception exception)
            {
                checks.Add(new DoctorCheck(
                    name,
                    false,
                    exception.Message,
                    DiagnosticSeverity.Error));
            }
        }
    }

    // A Rhino this account may not read stays in the list and is also a warning: the caller
    // learns that one of the running instances is running an unknown build.
    private static IReadOnlyList<Diagnostic> Unattributable(RhinoInstanceAttribution attribution) =>
        attribution.Instances
            .Where(instance => !instance.IsAttributed)
            .Select(instance => new Diagnostic(
                "rhino_instance_unattributable",
                $"Rhino process {instance.ProcessId} is running and which plug-in it holds could " +
                $"not be read: {instance.UnattributableReason}",
                DiagnosticSeverity.Warning))
            .ToArray();
}
