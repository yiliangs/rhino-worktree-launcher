using System.Diagnostics;
using System.Text.Json;

namespace RhinoWorktreeLauncher;

internal sealed class LaunchCoordinator
{
    private static readonly TimeSpan FileUsePollDelay = TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions LogJson = new JsonSerializerOptions(JsonDefaults.Write)
    {
        WriteIndented = false
    };
    private readonly LauncherBackendOptions _options;
    private readonly ContextResolver _contextResolver;
    private readonly BuildCoordinator _buildCoordinator;

    public LaunchCoordinator(
        LauncherBackendOptions options,
        ContextResolver contextResolver,
        BuildCoordinator buildCoordinator)
    {
        _options = options;
        _contextResolver = contextResolver;
        _buildCoordinator = buildCoordinator;
    }

    public async Task<CommandResult<LaunchResult>> LaunchAsync(
        string path,
        LaunchMode launchMode,
        TimeSpan timeout,
        IProgress<LaunchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        string launchId = Guid.NewGuid().ToString("N");
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(_options.LogsDirectory);
        string logPath = Path.Combine(_options.LogsDirectory, $"{startedAt:yyyyMMdd-HHmmss}-{launchId}.jsonl");
        string worktreePath = Path.GetFullPath(path);
        Process? launchedRhino = null;
        bool launchVerified = false;
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        CancellationToken token = timeoutSource.Token;

        try
        {
            await ReportAsync(LaunchStage.Resolve, "Resolving the registered project and selected worktree.");
            CommandResult<ResolvedContext> contextResult = await _contextResolver.ResolveAsync(path, token);
            if (!contextResult.Succeeded)
                return await FailAsync(contextResult.Diagnostics[0]);
            ResolvedContext context = contextResult.Value!;
            worktreePath = context.WorktreePath;

            await ReportAsync(LaunchStage.Prepare, "Resolving the selected solution configuration and canonical artifact.");
            CommandResult<PreparedLaunchArtifacts> build = await _buildCoordinator.PrepareAsync(
                path,
                launchMode,
                new ForwardBuildProgress(progress, launchId),
                token);
            if (!build.Succeeded)
                return await FailAsync(build.Diagnostics[0]);
            PreparedLaunchArtifacts artifacts = build.Value!;

            await MachineRegistrationSuspension.RecoverAsync(
                _options.LocksDirectory,
                context.RhinoVersion,
                artifacts.PluginId,
                token);
            IReadOnlyList<PluginRegistrationConflict> conflicts = _options.PluginRegistrationScanner(
                context.RhinoVersion,
                artifacts.PluginId,
                artifacts.PluginPath);
            foreach (PluginRegistrationConflict conflict in conflicts.Where(c => c.Scope == "user"))
            {
                await LogDiagnosticAsync(new Diagnostic(
                    "plugin_registration_conflict",
                    Describe(conflict),
                    DiagnosticSeverity.Warning));
            }
            // A machine-wide registration wins over the current-user overlay for the same
            // plug-in ID, so it is suspended for the launch where the user granted write
            // access to the machine Plug-ins key (ADR 0013). Without that access, starting
            // Rhino would only reach the verification timeout, so the launch refuses.
            PluginRegistrationConflict? machineConflict =
                conflicts.FirstOrDefault(c => c.Scope == "machine");
            IDisposable? machineSuspension = machineConflict is null
                ? null
                : await _options.MachineRegistrationSuspender(
                    _options.LocksDirectory,
                    context.RhinoVersion,
                    artifacts.PluginId,
                    token);
            using (machineSuspension)
            {
                if (machineConflict is not null && machineSuspension is null)
                    return await FailAsync(new Diagnostic(
                        "plugin_registration_conflict",
                        Describe(machineConflict)));
                if (machineConflict is not null)
                    await LogDiagnosticAsync(new Diagnostic(
                        "plugin_registration_suspended",
                        $"The machine-wide registration naming '{machineConflict.RegisteredPath}' " +
                            "is suspended for this launch and restored when it ends.",
                        DiagnosticSeverity.Info));

                await ReportAsync(LaunchStage.Registration, "Applying a temporary current-user plug-in registration.");
                using (PluginRegistrationLease registrationLease = await PluginRegistrationLease.AcquireAsync(
                    _options.LocksDirectory,
                    context.RhinoVersion,
                    artifacts.PluginId.ToString("D"),
                    Path.GetFileNameWithoutExtension(artifacts.PluginPath),
                    artifacts.PluginPath,
                    token))
                {
                    await ReportAsync(LaunchStage.Rhino, "Starting Rhino; the temporary registration loads the selected plug-in.");
                    launchedRhino = _options.RhinoProcessStarter(CreateRhinoStartInfo(context, artifacts));

                    await ReportAsync(LaunchStage.Verify, "Waiting for the Rhino process to hold the selected plug-in in use.");
                    await WaitForPluginInUseAsync(artifacts, launchedRhino, token);
                }
                launchVerified = true;
            }

            LaunchResult result = new LaunchResult(
                launchId,
                LaunchStatus.Succeeded,
                context.WorktreePath,
                Path.GetFullPath(artifacts.PluginPath),
                artifacts.CriticalDependencies.ToArray(),
                launchedRhino.Id,
                logPath,
                startedAt,
                DateTimeOffset.UtcNow);
            await ReportAsync(LaunchStage.Complete, "Rhino is using the selected solution configuration's canonical binaries.");
            return CommandResult<LaunchResult>.Success(result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await FailAsync(new Diagnostic(
                "launch_timeout",
                $"Launch did not complete within {timeout.TotalSeconds:0.###} seconds."));
        }
        catch (OperationCanceledException)
        {
            return await FailAsync(new Diagnostic("launch_cancelled", "Launch was cancelled."));
        }
        catch (Exception exception)
        {
            return await FailAsync(new Diagnostic("launch_failed", exception.Message));
        }
        finally
        {
            if (!launchVerified && launchedRhino is not null)
            {
                try
                {
                    if (!launchedRhino.HasExited)
                    {
                        launchedRhino.Kill(entireProcessTree: true);
                        await launchedRhino.WaitForExitAsync(CancellationToken.None);
                    }
                }
                catch
                {
                    // The process may exit between the check and termination request.
                }
            }
            launchedRhino?.Dispose();
        }

        async Task ReportAsync(LaunchStage stage, string message)
        {
            LaunchProgress update = new LaunchProgress(launchId, stage, message, DateTimeOffset.UtcNow);
            progress?.Report(update);
            await AppendLogAsync(logPath, new
            {
                type = "progress",
                update.LaunchId,
                Stage = update.StageToken,
                update.Message,
                update.Timestamp
            }, CancellationToken.None);
        }

        async Task LogDiagnosticAsync(Diagnostic diagnostic) => await AppendLogAsync(logPath, new
        {
            type = "diagnostic",
            diagnostic.Code,
            diagnostic.Message,
            severity = diagnostic.Severity.ToString(),
            timestamp = DateTimeOffset.UtcNow
        }, CancellationToken.None);

        async Task<CommandResult<LaunchResult>> FailAsync(Diagnostic diagnostic)
        {
            DateTimeOffset completedAt = DateTimeOffset.UtcNow;
            await AppendLogAsync(logPath, new
            {
                type = "diagnostic",
                diagnostic.Code,
                diagnostic.Message,
                severity = diagnostic.Severity.ToString(),
                timestamp = completedAt
            }, CancellationToken.None);
            return CommandResult<LaunchResult>.Failure(
                new LaunchResult(
                    launchId,
                    LaunchStatus.Failed,
                    worktreePath,
                    null,
                    Array.Empty<VerifiedDependency>(),
                    null,
                    logPath,
                    startedAt,
                    completedAt),
                diagnostic);
        }
    }

    private static string Describe(PluginRegistrationConflict conflict) => conflict.Scope == "machine"
        ? $"A machine-wide registration for this plug-in ID names '{conflict.RegisteredPath}', " +
            "and Rhino loads that file instead of the selected worktree artifact. " +
            "RWL never elevates: grant this account write access to the machine Plug-ins key " +
            "with an elevated account so launches can suspend and restore that registration, " +
            $"or remove '{conflict.RegistryKeyPath}' if it is stale."
        : $"An existing current-user registration for this plug-in ID names '{conflict.RegisteredPath}'. " +
            "The launch registration temporarily replaces it and restores it afterward.";

    private ProcessStartInfo CreateRhinoStartInfo(ResolvedContext context, PreparedLaunchArtifacts artifacts)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = _options.RhinoExecutableResolver(context.RhinoVersion),
            WorkingDirectory = artifacts.WorktreePath,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("/nosplash");
        startInfo.ArgumentList.Add("/notemplate");
        startInfo.ArgumentList.Add($"/{artifacts.RhinoRuntime}");
        // The registration lease is the only loading mechanism. Also passing the .rhp on
        // the command line asks Rhino to install an ID the lease has already registered,
        // which Rhino rejects as an ID already in use.
        return startInfo;
    }

    // Verification principle: an assembly Rhino has loaded is a file mapped into the
    // Rhino process's address space, which attributes the file to that exact PID.
    // Only the plug-in itself is gated: lazily-loaded dependencies (e.g. solvers) are
    // legitimately unmapped at startup, and their presence beside the plug-in is
    // already checked during prepare.
    private async Task WaitForPluginInUseAsync(
        PreparedLaunchArtifacts artifacts,
        Process rhino,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_options.FileInUseInspector(rhino.Id, artifacts.PluginPath))
                return;
            if (rhino.HasExited)
                throw new InvalidOperationException("Rhino exited before it held the selected plug-in in use.");
            await Task.Delay(FileUsePollDelay, cancellationToken);
        }
    }

    private static async Task AppendLogAsync(
        string path,
        object value,
        CancellationToken cancellationToken)
    {
        string line = JsonSerializer.Serialize(value, LogJson) + Environment.NewLine;
        await File.AppendAllTextAsync(path, line, cancellationToken);
    }

    private sealed class ForwardBuildProgress : IProgress<BuildProgress>
    {
        private readonly IProgress<LaunchProgress>? _progress;
        private readonly string _launchId;

        public ForwardBuildProgress(IProgress<LaunchProgress>? progress, string launchId)
        {
            _progress = progress;
            _launchId = launchId;
        }

        public void Report(BuildProgress value) => _progress?.Report(new LaunchProgress(
            _launchId,
            value.Stage == BuildStage.Build ? LaunchStage.Build : LaunchStage.Artifact,
            value.Message,
            value.Timestamp));
    }
}
