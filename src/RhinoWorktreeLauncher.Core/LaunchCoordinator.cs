using System.Diagnostics;
using System.Text.Json;

namespace RhinoWorktreeLauncher;

internal sealed class LaunchCoordinator
{
    private static readonly TimeSpan ReceiptPollDelay = TimeSpan.FromMilliseconds(75);
    private readonly LauncherBackendOptions _options;
    private readonly ContextResolver _contextResolver;

    public LaunchCoordinator(LauncherBackendOptions options, ContextResolver contextResolver)
    {
        _options = options;
        _contextResolver = contextResolver;
    }

    public async Task<CommandResult<LaunchResult>> LaunchAsync(
        string path,
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
            await ReportAsync("resolve", "Resolving registered project and selected worktree.");
            CommandResult<ResolvedContext> contextResult = await _contextResolver.ResolveAsync(path, token);
            if (!contextResult.Succeeded)
                return await FailAsync(contextResult.Diagnostics[0]);
            ResolvedContext context = contextResult.Value!;
            worktreePath = context.WorktreePath;

            string launchRoot = Path.Combine(Path.GetTempPath(), "RhinoWorktreeLauncher", launchId);
            Directory.CreateDirectory(launchRoot);
            string requestPath = Path.Combine(launchRoot, "driver-request.json");
            string receiptPath = Path.Combine(launchRoot, "launch-receipt.json");
            DriverRequest request = new DriverRequest(
                1,
                "prepareLaunch",
                launchId,
                context.WorktreePath,
                receiptPath);
            await File.WriteAllTextAsync(
                requestPath,
                JsonSerializer.Serialize(request, JsonDefaults.Write),
                token);

            await ReportAsync("driver", "Running the repository-owned launch driver.");
            DriverResult driver = await RunDriverAsync(context, requestPath, ReportAsync, token);
            ValidateDriverResult(driver, context);
            if (!driver.Success)
            {
                return await FailAsync(new Diagnostic(
                    driver.ErrorCode ?? "driver_failed",
                    driver.ErrorMessage ?? "The project driver reported failure."));
            }

            await ReportAsync("rhino", "Starting Rhino with the selected package directory.");
            ProcessStartInfo startInfo = CreateRhinoStartInfo(context, driver, launchId, receiptPath);
            launchedRhino = _options.RhinoProcessStarter(startInfo);

            await ReportAsync("receipt", "Waiting for the plug-in loaded-binary receipt.");
            LaunchReceipt receipt = await WaitForReceiptAsync(receiptPath, launchedRhino, token);
            IReadOnlyList<VerifiedDependency> dependencies = VerifyReceipt(
                launchId,
                launchedRhino.Id,
                driver,
                receipt);
            launchVerified = true;

            LaunchResult result = new LaunchResult(
                launchId,
                LaunchStatus.Succeeded,
                context.WorktreePath,
                Path.GetFullPath(receipt.PluginPath),
                dependencies,
                receipt.ProcessId,
                logPath,
                startedAt,
                DateTimeOffset.UtcNow);
            await ReportAsync("complete", "Rhino loaded the selected plug-in and dependencies.");
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
                exception is ReceiptVerificationException ? "receipt_mismatch" : "launch_failed",
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

    private async Task<DriverResult> RunDriverAsync(
        ResolvedContext context,
        string requestPath,
        Func<string, string, Task> report,
        CancellationToken cancellationToken)
    {
        DriverResult? result = null;
        await ProcessRunner.RunLinesAsync(
            _options.PowerShellExecutable,
            context.WorktreePath,
            new[]
            {
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                context.Manifest.ResolveDriverPath(context.WorktreePath),
                "-RequestPath",
                requestPath
            },
            async line =>
            {
                if (string.IsNullOrWhiteSpace(line))
                    return;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    if (!document.RootElement.TryGetProperty("kind", out JsonElement kind))
                        return;
                    if (string.Equals(kind.GetString(), "event", StringComparison.Ordinal))
                    {
                        DriverEvent? driverEvent = JsonSerializer.Deserialize<DriverEvent>(line, JsonDefaults.Read);
                        if (driverEvent is not null && driverEvent.ProtocolVersion == 1)
                        {
                            await report(
                                string.IsNullOrWhiteSpace(driverEvent.Stage) ? "driver" : $"driver:{driverEvent.Stage}",
                                driverEvent.Message);
                        }
                    }
                    else if (string.Equals(kind.GetString(), "result", StringComparison.Ordinal))
                    {
                        result = JsonSerializer.Deserialize<DriverResult>(line, JsonDefaults.Read);
                    }
                }
                catch (JsonException)
                {
                    await report("driver:output", line);
                }
            },
            cancellationToken);
        return result ?? throw new InvalidDataException("The driver did not emit a terminal protocol result.");
    }

    private static void ValidateDriverResult(DriverResult driver, ResolvedContext context)
    {
        if (driver.ProtocolVersion != 1 || !string.Equals(driver.Kind, "result", StringComparison.Ordinal))
            throw new InvalidDataException("The driver emitted an unsupported terminal result.");
        if (!driver.Success)
            return;
        if (string.IsNullOrWhiteSpace(driver.PackageDirectory) ||
            string.IsNullOrWhiteSpace(driver.PluginPath) ||
            string.IsNullOrWhiteSpace(driver.Receipt.LaunchIdEnvironmentVariable) ||
            string.IsNullOrWhiteSpace(driver.Receipt.ReceiptPathEnvironmentVariable))
        {
            throw new InvalidDataException("The successful driver result omitted required launch or receipt fields.");
        }
        if (driver.RhinoRuntime is not null &&
            !string.Equals(driver.RhinoRuntime, "netfx", StringComparison.Ordinal) &&
            !string.Equals(driver.RhinoRuntime, "netcore", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported Rhino runtime '{driver.RhinoRuntime}'.");
        }

        string worktreePrefix = Path.GetFullPath(context.WorktreePath).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string package = Path.GetFullPath(driver.PackageDirectory);
        string plugin = Path.GetFullPath(driver.PluginPath);
        if (!package.StartsWith(worktreePrefix, StringComparison.OrdinalIgnoreCase) ||
            !plugin.StartsWith(package.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Driver artifact paths must belong to the selected worktree package directory.");
        }
        if (!Directory.Exists(package) || !File.Exists(plugin))
            throw new FileNotFoundException("The driver reported artifacts that do not exist.");
        foreach (DriverDependency dependency in driver.CriticalDependencies)
        {
            string dependencyPath = Path.GetFullPath(dependency.Path);
            if (string.IsNullOrWhiteSpace(dependency.Name) ||
                !dependencyPath.StartsWith(package.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(dependencyPath))
            {
                throw new InvalidDataException($"Critical dependency '{dependency.Name}' is missing or outside the package directory.");
            }
        }
    }

    private ProcessStartInfo CreateRhinoStartInfo(
        ResolvedContext context,
        DriverResult driver,
        string launchId,
        string receiptPath)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = _options.RhinoExecutableResolver(context.Manifest.Launch.RhinoVersion),
            WorkingDirectory = context.WorktreePath,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("/nosplash");
        startInfo.ArgumentList.Add("/notemplate");
        if (!string.IsNullOrWhiteSpace(driver.RhinoRuntime))
            startInfo.ArgumentList.Add($"/{driver.RhinoRuntime}");
        startInfo.Environment["RHINO_PACKAGE_DIRS"] =
            Path.GetFullPath(driver.PackageDirectory).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        startInfo.Environment[driver.Receipt.LaunchIdEnvironmentVariable] = launchId;
        startInfo.Environment[driver.Receipt.ReceiptPathEnvironmentVariable] = receiptPath;
        return startInfo;
    }

    private static async Task<LaunchReceipt> WaitForReceiptAsync(
        string receiptPath,
        Process rhino,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(receiptPath))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(receiptPath, cancellationToken);
                    LaunchReceipt? receipt = JsonSerializer.Deserialize<LaunchReceipt>(json, JsonDefaults.Read);
                    if (receipt is not null)
                        return receipt;
                }
                catch (JsonException)
                {
                    // The receipt writer may still be completing an atomic replacement.
                }
                catch (IOException)
                {
                    // Retry while the writer has the receipt open.
                }
            }
            if (rhino.HasExited)
                throw new InvalidOperationException("Rhino exited before writing the loaded-binary receipt.");
            await Task.Delay(ReceiptPollDelay, cancellationToken);
        }
    }

    private static IReadOnlyList<VerifiedDependency> VerifyReceipt(
        string launchId,
        int processId,
        DriverResult expected,
        LaunchReceipt actual)
    {
        if (actual.SchemaVersion != 1)
            throw new ReceiptVerificationException($"Unsupported receipt schema version {actual.SchemaVersion}.");
        if (string.Equals(actual.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            throw new ReceiptVerificationException(
                $"The plug-in reported a load failure: {actual.Error ?? "No error detail was supplied."}");
        }
        if (!string.Equals(actual.Status, "loaded", StringComparison.OrdinalIgnoreCase))
        {
            throw new ReceiptVerificationException(
                $"Receipt status '{actual.Status}' is not a terminal loaded or failed status.");
        }
        if (!string.Equals(actual.LaunchId, launchId, StringComparison.Ordinal))
            throw new ReceiptVerificationException("Receipt launch ID does not match this launch.");
        if (actual.ProcessId != processId)
            throw new ReceiptVerificationException("Receipt process ID does not match the Rhino process.");
        if (!ContextResolver.SamePath(actual.PluginPath, expected.PluginPath))
            throw new ReceiptVerificationException("Rhino loaded the plug-in from an unexpected path.");

        Dictionary<string, DriverDependency> actualDependencies = actual.CriticalDependencies.ToDictionary(
            dependency => dependency.Name,
            StringComparer.OrdinalIgnoreCase);
        List<VerifiedDependency> verified = new List<VerifiedDependency>();
        foreach (DriverDependency dependency in expected.CriticalDependencies)
        {
            if (!actualDependencies.TryGetValue(dependency.Name, out DriverDependency? loaded) ||
                !ContextResolver.SamePath(loaded.Path, dependency.Path))
            {
                throw new ReceiptVerificationException(
                    $"Rhino loaded critical dependency '{dependency.Name}' from an unexpected path.");
            }
            verified.Add(new VerifiedDependency(dependency.Name, Path.GetFullPath(loaded.Path)));
        }
        return verified;
    }

    private static async Task AppendLogAsync(
        string path,
        object value,
        CancellationToken cancellationToken)
    {
        string line = JsonSerializer.Serialize(value, JsonDefaults.Write) + Environment.NewLine;
        await File.AppendAllTextAsync(path, line, cancellationToken);
    }

    private sealed class ReceiptVerificationException : Exception
    {
        public ReceiptVerificationException(string message)
            : base(message)
        {
        }
    }
}
