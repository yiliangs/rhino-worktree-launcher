using System.Text.Json;
using Rwl.Protocol;

namespace RhinoWorktreeLauncher;

// Resolves the worktree, builds the canonical solution, and then hands the whole
// registration-mutating half of the launch to an executor process the interactive Windows
// shell starts (ADR 0015). This process reads and builds; it never writes a registration,
// starts Rhino, or verifies a load, because it can be running inside a sandbox whose
// current-user registry writes never reach the hive Rhino reads.
internal sealed class LaunchCoordinator
{
    // Switching the standing registration waits on nothing that involves Rhino, so its budget
    // covers one lock, one registry write, and one independent read of that write.
    private static readonly TimeSpan SetRegistrationTimeout = TimeSpan.FromSeconds(60);

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
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(_options.LogsDirectory);
        LaunchLog log = new LaunchLog(
            Guid.NewGuid().ToString("N"),
            _options.LogsDirectory,
            startedAt,
            progress);
        string worktreePath = Path.GetFullPath(path);
        int? rhinoProcessId = null;
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        CancellationToken token = timeoutSource.Token;

        try
        {
            log.Record(new
            {
                type = "launch",
                launchId = log.LaunchId,
                hostKind = _options.HostKind,
                releaseId = _options.ReleaseId,
                requestedLaunchMode = launchMode.ToString(),
                timeoutSeconds = timeout.TotalSeconds,
                requestedPath = worktreePath,
                timestamp = startedAt
            });

            // The executor re-checks the same rule; refusing here is what keeps an invalid
            // map from costing a resolve and a build first.
            string? environmentOffense = LaunchEnvironment.Describe(environment);
            if (environmentOffense is not null)
                return Fail(new Diagnostic("invalid_environment", environmentOffense));

            log.Report(LaunchStage.Resolve, "Resolving the registered project and selected worktree.");
            CommandResult<ResolvedContext> contextResult = await _contextResolver.ResolveAsync(path, token);
            if (!contextResult.Succeeded)
                return Fail(contextResult.Diagnostics[0]);
            ResolvedContext context = contextResult.Value!;
            worktreePath = context.WorktreePath;

            log.Report(LaunchStage.Prepare, "Resolving the selected solution configuration and canonical artifact.");
            CommandResult<PreparedLaunchArtifacts> build = await _buildCoordinator.PrepareAsync(
                path,
                launchMode,
                new ForwardBuildProgress(log),
                token);
            if (!build.Succeeded)
                return Fail(build.Diagnostics[0]);
            PreparedLaunchArtifacts artifacts = build.Value!;

            TimeSpan remaining = timeout - (DateTimeOffset.UtcNow - startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                return Fail(new Diagnostic(
                    LaunchExecutorCodes.LaunchTimeout,
                    $"Preparing the canonical artifact used the whole {timeout.TotalSeconds:0.###} " +
                    "second budget, leaving none for Rhino."));
            }

            LaunchExecutorRequest request = new LaunchExecutorRequest
            {
                LaunchId = log.LaunchId,
                HostKind = _options.HostKind,
                ReleaseId = _options.ReleaseId,
                RhinoVersion = context.RhinoVersion,
                PluginId = artifacts.PluginId.ToString("D"),
                PluginName = Path.GetFileNameWithoutExtension(artifacts.PluginPath),
                PluginPath = Path.GetFullPath(artifacts.PluginPath),
                RhinoExecutable = _options.RhinoExecutableResolver(context.RhinoVersion),
                RhinoRuntime = artifacts.RhinoRuntime,
                WorkingDirectory = artifacts.WorktreePath,
                LocksDirectory = _options.LocksDirectory,
                LogsDirectory = _options.LogsDirectory,
                TimeoutSeconds = remaining.TotalSeconds,
                Environment = environment is null || environment.Count == 0
                    ? null
                    : new Dictionary<string, string>(environment)
            };
            log.Record(new
            {
                type = "executor_request",
                launchId = log.LaunchId,
                protocolVersion = request.ProtocolVersion,
                request.RhinoVersion,
                request.PluginId,
                request.PluginName,
                request.PluginPath,
                request.RhinoExecutable,
                request.RhinoRuntime,
                request.WorkingDirectory,
                request.TimeoutSeconds,
                // Names only: a caller-supplied value may be sensitive.
                environmentVariables = request.Environment?.Keys.ToArray(),
                timestamp = DateTimeOffset.UtcNow
            });

            log.Report(
                LaunchStage.Registration,
                "Starting a launch executor through the interactive Windows shell.");
            LaunchExecutorEvent result = await _options.LaunchExecutorInvoker(
                request,
                new ImmediateProgress<LaunchExecutorEvent>(log.Relay),
                token);
            log.Relay(result);
            if (!result.Succeeded)
                return Fail(new Diagnostic(result.Code, result.Message));

            rhinoProcessId = result.RhinoProcessId;
            LaunchResult launched = new LaunchResult(
                log.LaunchId,
                LaunchStatus.Succeeded,
                context.WorktreePath,
                Path.GetFullPath(artifacts.PluginPath),
                artifacts.CriticalDependencies.ToArray(),
                rhinoProcessId,
                log.Path,
                startedAt,
                DateTimeOffset.UtcNow);
            log.Report(LaunchStage.Complete, "Rhino is using the selected solution configuration's canonical binaries.");
            return CommandResult<LaunchResult>.Success(launched);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(new Diagnostic(
                LaunchExecutorCodes.LaunchTimeout,
                $"Launch did not complete within {timeout.TotalSeconds:0.###} seconds."));
        }
        catch (OperationCanceledException)
        {
            return Fail(new Diagnostic(LaunchExecutorCodes.LaunchCancelled, "Launch was cancelled."));
        }
        // The executor and its client name their own failure conditions, so the code that
        // reaches the caller identifies the step that failed rather than the launch as a
        // whole.
        catch (LaunchDiagnosticException exception)
        {
            return Fail(new Diagnostic(exception.Code, exception.Message));
        }
        catch (Exception exception)
        {
            return Fail(new Diagnostic(LaunchExecutorCodes.LaunchFailed, exception.Message));
        }

        CommandResult<LaunchResult> Fail(Diagnostic diagnostic)
        {
            DateTimeOffset completedAt = DateTimeOffset.UtcNow;
            log.RecordDiagnostic(diagnostic, completedAt);
            return CommandResult<LaunchResult>.Failure(
                new LaunchResult(
                    log.LaunchId,
                    LaunchStatus.Failed,
                    worktreePath,
                    null,
                    Array.Empty<VerifiedDependency>(),
                    rhinoProcessId,
                    log.Path,
                    startedAt,
                    completedAt),
                diagnostic);
        }
    }

    /// <summary>
    /// Rewrites the standing registration so an ordinary Rhino start loads the selected
    /// worktree's canonical artifact (ADR 0016). It runs the same way a launch does up to the
    /// executor: resolve the worktree, resolve the existing artifact without building it, and
    /// hand the registry mutation to a process the interactive Windows shell started.
    /// </summary>
    public async Task<CommandResult<RegistrationSwitchOutcome>> SetStandingRegistrationAsync(
        string path,
        IProgress<LaunchProgress>? progress,
        CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(_options.LogsDirectory);
        LaunchLog log = new LaunchLog(
            Guid.NewGuid().ToString("N"),
            _options.LogsDirectory,
            startedAt,
            progress);
        string projectId = string.Empty;
        string worktreePath = Path.GetFullPath(path);
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(SetRegistrationTimeout);
        CancellationToken token = timeoutSource.Token;

        try
        {
            log.Record(new
            {
                type = "set_registration",
                launchId = log.LaunchId,
                hostKind = _options.HostKind,
                releaseId = _options.ReleaseId,
                timeoutSeconds = SetRegistrationTimeout.TotalSeconds,
                requestedPath = worktreePath,
                timestamp = startedAt
            });

            log.Report(LaunchStage.Resolve, "Resolving the registered project and selected worktree.");
            CommandResult<ResolvedContext> contextResult = await _contextResolver.ResolveAsync(path, token);
            if (!contextResult.Succeeded)
                return Fail(contextResult.Diagnostics[0]);
            ResolvedContext context = contextResult.Value!;
            projectId = context.ProjectId;
            worktreePath = context.WorktreePath;

            // The artifact has to exist before Rhino is pointed at it, and it is never built
            // here: this changes which build loads, it does not produce one. A missing
            // artifact therefore fails exactly the way a direct launch fails.
            log.Report(LaunchStage.Prepare, "Resolving the selected solution configuration and canonical artifact.");
            CommandResult<PreparedLaunchArtifacts> prepared = await _buildCoordinator.PrepareAsync(
                path,
                LaunchMode.DirectLaunch,
                new ForwardBuildProgress(log),
                token);
            if (!prepared.Succeeded)
                return Fail(prepared.Diagnostics[0]);
            PreparedLaunchArtifacts artifacts = prepared.Value!;

            LaunchExecutorRequest request = new LaunchExecutorRequest
            {
                Mode = LaunchExecutorMode.SetRegistration,
                LaunchId = log.LaunchId,
                HostKind = _options.HostKind,
                ReleaseId = _options.ReleaseId,
                RhinoVersion = context.RhinoVersion,
                PluginId = artifacts.PluginId.ToString("D"),
                PluginName = Path.GetFileNameWithoutExtension(artifacts.PluginPath),
                PluginPath = Path.GetFullPath(artifacts.PluginPath),
                LocksDirectory = _options.LocksDirectory,
                LogsDirectory = _options.LogsDirectory,
                TimeoutSeconds = SetRegistrationTimeout.TotalSeconds
            };
            log.Record(new
            {
                type = "executor_request",
                launchId = log.LaunchId,
                protocolVersion = request.ProtocolVersion,
                request.Mode,
                request.RhinoVersion,
                request.PluginId,
                request.PluginName,
                request.PluginPath,
                request.TimeoutSeconds,
                timestamp = DateTimeOffset.UtcNow
            });

            log.Report(
                LaunchStage.Registration,
                "Starting a launch executor through the interactive Windows shell.");
            LaunchExecutorEvent result = await _options.LaunchExecutorInvoker(
                request,
                new ImmediateProgress<LaunchExecutorEvent>(log.Relay),
                token);
            log.Relay(result);
            if (!result.Succeeded)
                return Fail(new Diagnostic(result.Code, result.Message));

            log.Report(LaunchStage.Complete, "Rhino now loads this worktree's build by default.");
            return CommandResult<RegistrationSwitchOutcome>.Success(new RegistrationSwitchOutcome(
                projectId,
                worktreePath,
                request.PluginPath,
                result.RegistryHive ?? string.Empty,
                result.RegistryKeyPath ?? string.Empty,
                result.PreviousRegisteredPath,
                log.Path));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(new Diagnostic(
                LaunchExecutorCodes.LaunchTimeout,
                $"The registration change did not complete within {SetRegistrationTimeout.TotalSeconds:0.###} seconds."));
        }
        catch (OperationCanceledException)
        {
            return Fail(new Diagnostic(
                LaunchExecutorCodes.LaunchCancelled,
                "The registration change was cancelled."));
        }
        catch (LaunchDiagnosticException exception)
        {
            return Fail(new Diagnostic(exception.Code, exception.Message));
        }
        catch (Exception exception)
        {
            return Fail(new Diagnostic(LaunchExecutorCodes.LaunchFailed, exception.Message));
        }

        CommandResult<RegistrationSwitchOutcome> Fail(Diagnostic diagnostic)
        {
            log.RecordDiagnostic(diagnostic, DateTimeOffset.UtcNow);
            return CommandResult<RegistrationSwitchOutcome>.Failure(
                new RegistrationSwitchOutcome(
                    projectId,
                    worktreePath,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    null,
                    log.Path),
                diagnostic);
        }
    }

    // One launch's diagnostics: the JSONL file on disk and the adapter's progress surface,
    // written from one place so the two records of a launch cannot disagree about which
    // stage it reached or which executor log holds the rest.
    private sealed class LaunchLog
    {
        private readonly IProgress<LaunchProgress>? _progress;
        private string? _executorLogPath;

        public LaunchLog(
            string launchId,
            string logsDirectory,
            DateTimeOffset startedAt,
            IProgress<LaunchProgress>? progress)
        {
            LaunchId = launchId;
            Path = System.IO.Path.Combine(logsDirectory, $"{startedAt:yyyyMMdd-HHmmss}-{launchId}.jsonl");
            _progress = progress;
        }

        public string LaunchId { get; }

        public string Path { get; }

        public void Record(object value) => File.AppendAllText(
            Path,
            JsonSerializer.Serialize(value, JsonDefaults.Line) + Environment.NewLine);

        public void Report(
            LaunchStage stage,
            string message,
            string code = "",
            DiagnosticSeverity severity = DiagnosticSeverity.Info) => Write(
                new LaunchProgress(LaunchId, stage, message, DateTimeOffset.UtcNow),
                code,
                severity);

        // An executor event is relayed rather than reinterpreted: its stage, code, and
        // timestamp are the executor's, so the launch log records what that process
        // observed and when.
        public void Relay(LaunchExecutorEvent value)
        {
            if (!Enum.TryParse(value.Stage, ignoreCase: true, out LaunchStage stage))
            {
                throw new LaunchDiagnosticException(
                    LaunchExecutorCodes.ExecutorProtocolViolation,
                    $"The launch executor reported stage '{value.Stage}', which is not a launch stage.");
            }
            if (!string.IsNullOrEmpty(value.ExecutorLogPath))
                _executorLogPath = value.ExecutorLogPath;
            Write(
                new LaunchProgress(LaunchId, stage, value.Message, value.Timestamp),
                value.Code,
                string.Equals(value.Severity, "error", StringComparison.OrdinalIgnoreCase)
                    ? DiagnosticSeverity.Error
                    : DiagnosticSeverity.Info);
        }

        public void RecordDiagnostic(Diagnostic diagnostic, DateTimeOffset timestamp) => Record(new
        {
            type = "diagnostic",
            launchId = LaunchId,
            diagnostic.Code,
            diagnostic.Message,
            severity = diagnostic.Severity.ToString(),
            executorLog = _executorLogPath,
            timestamp
        });

        private void Write(LaunchProgress update, string code, DiagnosticSeverity severity)
        {
            _progress?.Report(update);
            Record(new
            {
                type = "progress",
                update.LaunchId,
                Stage = update.StageToken,
                Code = code,
                update.Message,
                Severity = severity.ToString(),
                ExecutorLog = _executorLogPath,
                update.Timestamp
            });
        }
    }

    private sealed class ForwardBuildProgress : IProgress<BuildProgress>
    {
        private readonly LaunchLog _log;

        public ForwardBuildProgress(LaunchLog log) => _log = log;

        public void Report(BuildProgress value) => _log.Report(
            value.Stage == BuildStage.Build ? LaunchStage.Build : LaunchStage.Artifact,
            value.Message);
    }
}
