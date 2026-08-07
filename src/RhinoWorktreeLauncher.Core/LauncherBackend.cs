namespace RhinoWorktreeLauncher;

public sealed class LauncherBackend
{
    private readonly ProjectCatalog _catalog;
    private readonly ContextResolver _contextResolver;
    private readonly WorktreeScanner _scanner;
    private readonly LaunchCoordinator _launchCoordinator;

    public LauncherBackend(LauncherBackendOptions? options = null)
    {
        Options = options ?? new LauncherBackendOptions();
        _catalog = new ProjectCatalog(Options.CatalogPath);
        _contextResolver = new ContextResolver(_catalog);
        _scanner = new WorktreeScanner(Options);
        _launchCoordinator = new LaunchCoordinator(Options, _contextResolver);
    }

    public LauncherBackendOptions Options { get; }

    public async Task<CommandResult<ProjectRegistration>> RegisterProjectAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        try
        {
            ProjectRegistration registration = await _catalog.RegisterAsync(
                repositoryPath,
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

    public async Task<CommandResult<IReadOnlyList<ProjectSnapshot>>> GetProjectsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<ProjectSnapshot> projects = await _catalog.LoadAsync(cancellationToken);
            return CommandResult<IReadOnlyList<ProjectSnapshot>>.Success(
                projects,
                projects.SelectMany(project => project.Diagnostics).ToArray());
        }
        catch (Exception exception)
        {
            return CommandResult<IReadOnlyList<ProjectSnapshot>>.Failure(new Diagnostic(
                "catalog_read_failed",
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

    public async Task<CommandResult<ProjectWorktrees>> GetWorktreeSnapshotAsync(
        string projectId,
        bool includeRemote,
        CancellationToken cancellationToken)
    {
        CommandResult<ProjectSnapshot> projectResult = await GetProjectSnapshotAsync(
            projectId,
            cancellationToken);
        if (!projectResult.Succeeded || projectResult.Value?.Manifest is null)
        {
            return CommandResult<ProjectWorktrees>.Failure(
                projectResult.Diagnostics.ToArray());
        }

        return await _scanner.ScanAsync(projectResult.Value, includeRemote, cancellationToken);
    }

    public async Task<CommandResult<WorktreeInspection>> InspectWorktreeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        CommandResult<ResolvedContext> contextResult = await ResolveContextAsync(path, cancellationToken);
        if (!contextResult.Succeeded)
            return CommandResult<WorktreeInspection>.Failure(contextResult.Diagnostics.ToArray());

        ResolvedContext context = contextResult.Value!;
        string manifestPath = Path.Combine(
            context.WorktreePath,
            ProjectManifest.DefaultFileName);
        string driverPath = context.Manifest.ResolveDriverPath(context.WorktreePath);
        string rhinoPath = Options.RhinoExecutableResolver(context.Manifest.Launch.RhinoVersion);
        List<Diagnostic> diagnostics = new List<Diagnostic>();
        if (!File.Exists(driverPath))
            diagnostics.Add(new Diagnostic("driver_missing", $"Project driver was not found at '{driverPath}'."));
        if (!File.Exists(rhinoPath))
            diagnostics.Add(new Diagnostic("rhino_missing", $"Rhino was not found at '{rhinoPath}'."));

        WorktreeInspection inspection = new WorktreeInspection(
            context.ProjectId,
            context.WorktreePath,
            manifestPath,
            driverPath,
            rhinoPath,
            context.IsPrimary,
            diagnostics.Count == 0);
        return CommandResult<WorktreeInspection>.Success(inspection, diagnostics);
    }

    public Task<CommandResult<LaunchResult>> LaunchAsync(
        string path,
        TimeSpan timeout,
        IProgress<LaunchProgress>? progress,
        CancellationToken cancellationToken) => _launchCoordinator.LaunchAsync(
            path,
            timeout,
            progress,
            cancellationToken);

    public async Task<CommandResult<DoctorReport>> RunDoctorAsync(CancellationToken cancellationToken)
    {
        List<DoctorCheck> checks = new List<DoctorCheck>();
        await CheckProcessAsync("git", Options.GitExecutable, new[] { "--version" });
        await CheckProcessAsync(
            "powershell",
            Options.PowerShellExecutable,
            new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()" });

        CommandResult<IReadOnlyList<ProjectSnapshot>> projectsResult = await GetProjectsAsync(cancellationToken);
        checks.Add(new DoctorCheck(
            "catalog",
            projectsResult.Succeeded,
            projectsResult.Succeeded
                ? $"{projectsResult.Value!.Count} project(s) registered."
                : projectsResult.Diagnostics[0].Message,
            projectsResult.Succeeded ? DiagnosticSeverity.Info : DiagnosticSeverity.Error));
        foreach (ProjectSnapshot project in projectsResult.Value ?? Array.Empty<ProjectSnapshot>())
        {
            checks.Add(new DoctorCheck(
                $"project:{project.ProjectId}",
                project.Availability == ProjectAvailability.Available,
                project.Availability == ProjectAvailability.Available
                    ? $"{project.DisplayName} contract is available."
                    : string.Join(" ", project.Diagnostics.Select(diagnostic => diagnostic.Message)),
                project.Availability == ProjectAvailability.Available
                    ? DiagnosticSeverity.Info
                    : DiagnosticSeverity.Error));
            if (project.Manifest is not null)
            {
                string rhinoPath = Options.RhinoExecutableResolver(project.Manifest.Launch.RhinoVersion);
                checks.Add(new DoctorCheck(
                    $"rhino:{project.Manifest.Launch.RhinoVersion}",
                    File.Exists(rhinoPath),
                    File.Exists(rhinoPath) ? rhinoPath : $"Rhino was not found at '{rhinoPath}'.",
                    File.Exists(rhinoPath) ? DiagnosticSeverity.Info : DiagnosticSeverity.Error));
            }
        }

        DoctorReport report = new DoctorReport(
            checks.All(check => check.Passed || check.Severity != DiagnosticSeverity.Error),
            checks,
            projectsResult.Value ?? Array.Empty<ProjectSnapshot>());
        IReadOnlyList<Diagnostic> diagnostics = checks
            .Where(check => !check.Passed)
            .Select(check => new Diagnostic(
                $"doctor_{check.Name.Replace(':', '_')}_failed",
                check.Message,
                check.Severity))
            .ToArray();
        return CommandResult<DoctorReport>.Success(report, diagnostics);

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
}
