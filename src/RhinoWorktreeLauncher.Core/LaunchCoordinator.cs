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
                TimeoutSeconds = remaining.TotalSeconds
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
