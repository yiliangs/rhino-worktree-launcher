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
            await ReportAsync("resolve", "Resolving the registered project and selected worktree.");
            CommandResult<ResolvedContext> contextResult = await _contextResolver.ResolveAsync(path, token);
            if (!contextResult.Succeeded)
                return await FailAsync(contextResult.Diagnostics[0]);
            ResolvedContext context = contextResult.Value!;
            worktreePath = context.WorktreePath;

            await ReportAsync("prepare", "Resolving the selected solution configuration and canonical artifact.");
            CommandResult<PreparedLaunchArtifacts> build = await _buildCoordinator.PrepareAsync(
                path,
                launchMode,
                new ForwardBuildProgress(progress, launchId),
                token);
            if (!build.Succeeded)
                return await FailAsync(build.Diagnostics[0]);
            PreparedLaunchArtifacts artifacts = build.Value!;

            await ReportAsync("registration", "Applying a temporary current-user plug-in path overlay.");
            using (PluginRegistrationLease registrationLease = await PluginRegistrationLease.AcquireAsync(
                _options.LocksDirectory,
                context.RhinoVersion,
                artifacts.PluginId.ToString("D"),
                artifacts.PluginPath,
                token))
            {
                await ReportAsync("rhino", "Starting Rhino with the selected plug-in on its command line.");
                launchedRhino = _options.RhinoProcessStarter(CreateRhinoStartInfo(context, artifacts));

                await ReportAsync("verify", "Waiting for the Rhino process to hold the selected plug-in in use.");
                await WaitForPluginInUseAsync(artifacts, launchedRhino, token);
            }
            launchVerified = true;

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
            await ReportAsync("complete", "Rhino is using the selected solution configuration's canonical binaries.");
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

        async Task ReportAsync(string stage, string message)
        {
            LaunchProgress update = new LaunchProgress(launchId, stage, message, DateTimeOffset.UtcNow);
            progress?.Report(update);
            await AppendLogAsync(logPath, new
            {
                type = "progress",
                update.LaunchId,
                update.Stage,
                update.Message,
                update.Timestamp
            }, CancellationToken.None);
        }

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
        // Passing the .rhp path makes Rhino load it during startup; the registration
        // lease alone does not make Rhino load a plug-in it has never seen.
        startInfo.ArgumentList.Add(artifacts.PluginPath);
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
            value.Stage,
            value.Message,
            value.Timestamp));
    }
}
