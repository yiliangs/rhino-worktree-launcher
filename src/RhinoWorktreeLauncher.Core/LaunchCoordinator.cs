using System.Diagnostics;
using System.Text.Json;

namespace RhinoWorktreeLauncher;

internal sealed class LaunchCoordinator
{
    private static readonly TimeSpan VerificationPollDelay = TimeSpan.FromMilliseconds(75);
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

            string verifierPath = Path.GetFullPath(_options.VerifierPluginPath);
            if (!File.Exists(verifierPath))
                return await FailAsync(new Diagnostic(
                    "verifier_missing",
                    $"The app-owned Rhino verifier was not found at '{verifierPath}'."));

            string launchRoot = Path.Combine(_options.LaunchStateDirectory, launchId);
            Directory.CreateDirectory(launchRoot);
            string resultPath = Path.Combine(launchRoot, "verification-result.json");
            string requestPath = Path.Combine(launchRoot, "verification-request.json");
            await File.WriteAllTextAsync(
                requestPath,
                JsonSerializer.Serialize(new VerifierRequest
                {
                    SchemaVersion = 1,
                    LaunchId = launchId,
                    PluginId = artifacts.PluginId,
                    PluginPath = artifacts.PluginPath,
                    CriticalDependencies = artifacts.CriticalDependencies.ToArray(),
                    ResultPath = resultPath
                }, JsonDefaults.Write),
                token);

            VerifierResult verification;
            await ReportAsync("registration", "Applying a temporary current-user plug-in path overlay.");
            using (PluginRegistrationLease registrationLease = await PluginRegistrationLease.AcquireAsync(
                _options.LocksDirectory,
                context.RhinoVersion,
                artifacts.PluginId.ToString("D"),
                artifacts.PluginPath,
                token))
            {
                await ReportAsync("rhino", "Starting Rhino with the app-owned verifier.");
                ProcessStartInfo startInfo = CreateRhinoStartInfo(
                    context,
                    artifacts,
                    verifierPath,
                    requestPath);
                launchedRhino = _options.RhinoProcessStarter(startInfo);

                await ReportAsync("verify", "Waiting for RWL to verify the loaded plug-in and dependencies.");
                verification = await WaitForVerificationAsync(resultPath, launchedRhino, token);
                VerifyResult(launchId, launchedRhino.Id, artifacts, verification);
            }
            launchVerified = true;

            LaunchResult result = new LaunchResult(
                launchId,
                LaunchStatus.Succeeded,
                context.WorktreePath,
                Path.GetFullPath(verification.PluginPath),
                verification.CriticalDependencies,
                verification.ProcessId,
                logPath,
                startedAt,
                DateTimeOffset.UtcNow);
            await ReportAsync("complete", "Rhino loaded the selected solution configuration's canonical binaries.");
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
            return await FailAsync(new Diagnostic(
                exception is VerificationException ? "verification_mismatch" : "launch_failed",
                exception.Message));
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

    private ProcessStartInfo CreateRhinoStartInfo(
        ResolvedContext context,
        PreparedLaunchArtifacts artifacts,
        string verifierPath,
        string requestPath)
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
        startInfo.ArgumentList.Add("/runscript=-_RwlVerifyLaunch");
        startInfo.Environment["RHINO_PACKAGE_DIRS"] = string.Join(
            Path.PathSeparator,
            Path.GetDirectoryName(verifierPath)!,
            artifacts.PackageDirectory);
        startInfo.Environment["RWL_VERIFY_REQUEST"] = requestPath;
        return startInfo;
    }

    private static async Task<VerifierResult> WaitForVerificationAsync(
        string resultPath,
        Process rhino,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(resultPath))
            {
                try
                {
                    VerifierResult? result = JsonSerializer.Deserialize<VerifierResult>(
                        await File.ReadAllTextAsync(resultPath, cancellationToken),
                        JsonDefaults.Read);
                    if (result is not null)
                        return result;
                }
                catch (Exception exception) when (exception is JsonException or IOException)
                {
                    // The verifier may still be atomically promoting the result.
                }
            }
            if (rhino.HasExited)
                throw new InvalidOperationException("Rhino exited before the app-owned verifier returned a result.");
            await Task.Delay(VerificationPollDelay, cancellationToken);
        }
    }

    private static void VerifyResult(
        string launchId,
        int processId,
        PreparedLaunchArtifacts expected,
        VerifierResult actual)
    {
        if (actual.SchemaVersion != 1)
            throw new VerificationException($"Unsupported verifier result schema {actual.SchemaVersion}.");
        if (string.Equals(actual.Status, "failed", StringComparison.OrdinalIgnoreCase))
            throw new VerificationException(actual.Error ?? "The app-owned verifier reported a load failure.");
        if (!string.Equals(actual.Status, "loaded", StringComparison.OrdinalIgnoreCase))
            throw new VerificationException($"Verifier status '{actual.Status}' is not terminal.");
        if (!string.Equals(actual.LaunchId, launchId, StringComparison.Ordinal))
            throw new VerificationException("Verifier launch ID does not match this launch.");
        if (actual.ProcessId != processId)
            throw new VerificationException("Verifier process ID does not match the Rhino process.");
        if (!ContextResolver.SamePath(actual.PluginPath, expected.PluginPath))
            throw new VerificationException("Rhino loaded the plug-in from an unexpected path.");

        Dictionary<string, VerifiedDependency> loaded = actual.CriticalDependencies.ToDictionary(
            dependency => dependency.Name,
            StringComparer.OrdinalIgnoreCase);
        foreach (VerifiedDependency dependency in expected.CriticalDependencies)
        {
            if (!loaded.TryGetValue(dependency.Name, out VerifiedDependency? actualDependency) ||
                !ContextResolver.SamePath(actualDependency.Path, dependency.Path))
            {
                throw new VerificationException(
                    $"Rhino loaded critical dependency '{dependency.Name}' from an unexpected path.");
            }
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

    private sealed class VerificationException : Exception
    {
        public VerificationException(string message)
            : base(message)
        {
        }
    }
}
