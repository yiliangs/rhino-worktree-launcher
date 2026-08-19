using System.Diagnostics;
using System.Text.Json;
using Rwl.Protocol;

namespace RhinoWorktreeLauncher;

// The whole registration-mutating half of a launch, running in a process the interactive
// shell started: pending-journal recovery, the namespace lease, the Rhino start,
// loaded-binary verification, and the restore (ADR 0015).
//
// Nothing here may run inside a launcher host process. A host can be started inside a
// per-process sandbox whose current-user registry writes never reach the real hive, which
// makes an install seed invisible to Rhino while every read this process performs still
// reports it present. The executor is spawned through the shell precisely to leave that
// interception behind.
internal sealed class LaunchExecutorEngine
{
    private readonly LaunchExecutorOptions _options;

    public LaunchExecutorEngine(LaunchExecutorOptions? options = null) =>
        _options = options ?? new LaunchExecutorOptions();

    public async Task<LaunchExecutorEvent> RunAsync(
        LaunchExecutorRequest request,
        IProgress<LaunchExecutorEvent> events,
        ExecutorLog log,
        CancellationToken clientDisconnected,
        CancellationToken cancellationToken)
    {
        ExecutorChannel channel = new ExecutorChannel(request, events, log);
        Guid pluginId = ParsePluginId(request, channel);
        if (pluginId == Guid.Empty)
            return channel.LastResult!;

        PluginNamespaceLeaseRequest leaseRequest = new PluginNamespaceLeaseRequest(
            request.LocksDirectory,
            request.RhinoVersion,
            pluginId,
            request.PluginName,
            request.PluginPath,
            new FileLockHolder(
                request.LaunchId,
                Environment.ProcessId,
                request.HostKind,
                DateTimeOffset.UtcNow));

        using CancellationTokenSource abort = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            clientDisconnected);
        abort.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        CancellationToken token = abort.Token;

        IPluginNamespaceLease? lease = null;
        Process? rhino = null;
        bool verified = false;
        try
        {
            channel.Progress(
                LaunchStage.Registration,
                LaunchExecutorCodes.ExecutorStarted,
                $"Launch executor {Environment.ProcessId} is applying the plug-in registration " +
                $"outside the {request.HostKind} host process.");

            PluginNamespaceLeaseResult acquired = await ObtainAsync(leaseRequest, channel, token);
            if (acquired.Refusal is not null)
            {
                return channel.Result(
                    LaunchExecutorCodes.PluginRegistrationConflict,
                    Describe(acquired.Refusal));
            }
            lease = acquired.Lease;
            ReportDisplacement(channel, acquired);

            rhino = StartRhino(request, channel);
            channel.Progress(
                LaunchStage.Verify,
                code: string.Empty,
                "Waiting for the Rhino process to hold the selected plug-in in use.");
            await WaitForPluginInUseAsync(request, rhino, token);
            verified = true;

            // The lease restores both hives here, but keeps its journal: the Rhino it
            // started is still able to write its registration back, and the post-exit
            // correction owns that (ADR 0015).
            lease?.RestoreRetainingJournal();
            channel.Progress(
                LaunchStage.Verify,
                LaunchExecutorCodes.PluginRegistrationRestored,
                "The displaced plug-in registrations are restored.");
            return channel.Result(
                LaunchExecutorCodes.LaunchVerified,
                $"Rhino process {rhino.Id} holds '{request.PluginPath}' mapped in its address space.",
                succeeded: true,
                rhinoProcessId: rhino.Id);
        }
        catch (OperationCanceledException) when (clientDisconnected.IsCancellationRequested)
        {
            return channel.Result(
                LaunchExecutorCodes.ExecutorClientDisconnected,
                "The launcher host that requested this launch disconnected before verification. " +
                "The plug-in registration is restored and the unverified Rhino is terminated.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return channel.Result(LaunchExecutorCodes.LaunchCancelled, "The launch was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return channel.Result(
                LaunchExecutorCodes.LaunchTimeout,
                $"The launch did not verify within {request.TimeoutSeconds:0.###} seconds.");
        }
        catch (RhinoExitedBeforeVerificationException exception)
        {
            return channel.Result(LaunchExecutorCodes.RhinoExitedBeforeVerification, exception.Message);
        }
        catch (LaunchDiagnosticException exception)
        {
            return channel.Result(exception.Code, exception.Message);
        }
        catch (Exception exception)
        {
            return channel.Result(LaunchExecutorCodes.LaunchFailed, exception.Message);
        }
        finally
        {
            if (!verified)
            {
                TerminateUnverified(rhino, channel);
                // The journal goes with it: nothing this launch started can still write a
                // registration back, so there is nothing left to correct.
                lease?.Dispose();
            }
            rhino?.Dispose();
        }
    }

    // Waits for the Rhino this launch verified, then puts the journaled pre-state back once
    // more. Rhino writes the artifact it loaded into its own registration, and the restore
    // above ran while that Rhino was still alive, so without this the machine registration
    // can end a session naming a worktree artifact (ADR 0015).
    public async Task<RegistrationDrift> CorrectAfterExitAsync(
        LaunchExecutorRequest request,
        int rhinoProcessId,
        ExecutorLog log,
        CancellationToken cancellationToken)
    {
        Guid pluginId = Guid.Parse(request.PluginId);
        PluginNamespaceLeaseRequest leaseRequest = new PluginNamespaceLeaseRequest(
            request.LocksDirectory,
            request.RhinoVersion,
            pluginId,
            request.PluginName,
            request.PluginPath,
            new FileLockHolder(
                request.LaunchId,
                Environment.ProcessId,
                request.HostKind,
                DateTimeOffset.UtcNow));

        await WaitForExitAsync(rhinoProcessId, cancellationToken);
        RegistrationDrift drift = await _options.PluginNamespace.CorrectAfterExitAsync(
            leaseRequest,
            cancellationToken);
        log.Append(new LaunchExecutorEvent
        {
            LaunchId = request.LaunchId,
            Stage = LaunchStage.Complete.ToString().ToLowerInvariant(),
            Code = drift.Drifted
                ? LaunchExecutorCodes.RegistrationWriteBackCorrected
                : LaunchExecutorCodes.PluginRegistrationRestored,
            Message = DescribeDrift(drift, rhinoProcessId)
        });
        return drift;
    }

    // Recovering a pending journal and taking the lease both queue on the one lock that
    // owns this plug-in's namespace, so both report the same wait and both end in the same
    // named condition when the holder never releases.
    private async Task<PluginNamespaceLeaseResult> ObtainAsync(
        PluginNamespaceLeaseRequest request,
        ExecutorChannel channel,
        CancellationToken cancellationToken)
    {
        FileLockWait? lastWait = null;
        IProgress<FileLockWait> waiting = new ImmediateProgress<FileLockWait>(wait =>
        {
            lastWait = wait;
            channel.Progress(
                LaunchStage.Registration,
                LaunchExecutorCodes.LeaseWait,
                $"Waiting for the plug-in registration lock held by {wait.HolderDescription}.");
        });
        try
        {
            // A journal left by a killed launch, or by a launch whose Rhino has since
            // exited, is restored before anything reads a registration.
            await _options.PluginNamespace.RecoverAsync(request, waiting, cancellationToken);
            return await _options.PluginNamespace.AcquireAsync(request, waiting, cancellationToken);
        }
        // A launch queued behind another session ends by name, stating who held the lock,
        // rather than expiring as an unexplained launch timeout.
        catch (OperationCanceledException) when (lastWait is not null)
        {
            throw new LaunchDiagnosticException(
                LaunchExecutorCodes.LeaseWaitTimeout,
                $"The plug-in registration lock is held by {lastWait.HolderDescription}. This launch " +
                $"waited {lastWait.Waited.TotalSeconds:0.###} seconds and it was never released. " +
                "Finish or end that launch, then try again.");
        }
    }

    private static void ReportDisplacement(ExecutorChannel channel, PluginNamespaceLeaseResult acquired)
    {
        if (acquired.DisplacedMachineRegistration is not null)
        {
            channel.Progress(
                LaunchStage.Registration,
                LaunchExecutorCodes.PluginRegistrationSuspended,
                $"The machine-wide registration naming '{acquired.DisplacedMachineRegistration}' is " +
                "suspended for this launch and restored when it ends.");
        }
        if (acquired.DisplacedUserRegistration is not null)
        {
            channel.Progress(
                LaunchStage.Registration,
                LaunchExecutorCodes.PluginRegistrationDisplaced,
                $"The current-user registration naming '{acquired.DisplacedUserRegistration}' is " +
                "displaced for this launch and restored when it ends.");
        }
        if (acquired.Seed is PluginSeed seed)
        {
            channel.Progress(
                LaunchStage.Registration,
                LaunchExecutorCodes.PluginRegistrationSeeded,
                $"The install seed names '{seed.FileName}' as '{seed.Name}'" +
                (seed.LoadMode is int mode ? $" with load mode {mode}." : " with no recorded load mode."));
        }
    }

    private Process StartRhino(LaunchExecutorRequest request, ExecutorChannel channel)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = request.RhinoExecutable,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("/nosplash");
        startInfo.ArgumentList.Add("/notemplate");
        startInfo.ArgumentList.Add($"/{request.RhinoRuntime}");
        // The registration lease is the only loading mechanism. Also passing the .rhp on
        // the command line asks Rhino to install an ID the lease has already registered,
        // which Rhino rejects as an ID already in use. This process was started by the
        // interactive shell, so Rhino needs no further brokering to escape a sandboxed
        // host: it is started here directly.
        channel.Progress(
            LaunchStage.Rhino,
            code: string.Empty,
            "Starting Rhino; the temporary registration loads the selected plug-in.");
        Process rhino = _options.RhinoProcessStarter(startInfo);
        channel.Progress(
            LaunchStage.Rhino,
            LaunchExecutorCodes.RhinoStarted,
            $"Rhino started as process {rhino.Id}.");
        return rhino;
    }

    // Verification principle: an assembly Rhino has loaded is a file mapped into the
    // Rhino process's address space, which attributes the file to that exact PID.
    // Only the plug-in itself is gated: lazily-loaded dependencies (e.g. solvers) are
    // legitimately unmapped at startup, and their presence beside the plug-in is
    // already checked during prepare.
    private async Task WaitForPluginInUseAsync(
        LaunchExecutorRequest request,
        Process rhino,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_options.FileInUseInspector(rhino.Id, request.PluginPath))
                return;
            if (rhino.HasExited)
                throw new RhinoExitedBeforeVerificationException(
                    rhino.Id,
                    "Rhino exited before it held the selected plug-in in use.");
            await Task.Delay(_options.FileUsePollDelay, cancellationToken);
        }
    }

    private static void TerminateUnverified(Process? rhino, ExecutorChannel channel)
    {
        if (rhino is null)
            return;
        try
        {
            if (rhino.HasExited)
                return;
            rhino.Kill(entireProcessTree: true);
            rhino.WaitForExit();
        }
        // The process may exit between the check and the termination request, and an
        // unverified Rhino that is already gone is the outcome this wanted.
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            channel.Progress(
                LaunchStage.Verify,
                LaunchExecutorCodes.LaunchFailed,
                $"The unverified Rhino process {rhino.Id} could not be terminated: {exception.Message}");
        }
    }

    private async Task WaitForExitAsync(int processId, CancellationToken cancellationToken)
    {
        Process rhino;
        try
        {
            rhino = Process.GetProcessById(processId);
        }
        // A verified Rhino that has already exited needs no waiting, and .NET reports an
        // unknown process ID as a bare ArgumentException.
        catch (ArgumentException)
        {
            return;
        }
        using (rhino)
            await rhino.WaitForExitAsync(cancellationToken);
    }

    private static Guid ParsePluginId(LaunchExecutorRequest request, ExecutorChannel channel)
    {
        if (Guid.TryParse(request.PluginId, out Guid pluginId) && pluginId != Guid.Empty)
            return pluginId;
        channel.Result(
            LaunchExecutorCodes.ExecutorRequestInvalid,
            $"The launch request carries '{request.PluginId}' as its plug-in ID, which is not a GUID.");
        return Guid.Empty;
    }

    private static string Describe(PluginRegistrationConflict conflict) =>
        $"A machine-wide registration for this plug-in ID names '{conflict.RegisteredPath}', " +
        "and Rhino loads that file instead of the selected worktree artifact. " +
        "RWL never elevates: grant this account write access to the machine Plug-ins key " +
        "with an elevated account so launches can suspend and restore that registration, " +
        $"or remove '{conflict.RegistryKeyPath}' if it is stale.";

    private static string DescribeDrift(RegistrationDrift drift, int rhinoProcessId)
    {
        if (!drift.JournalFound)
        {
            return $"Rhino process {rhinoProcessId} exited and another launch had already restored " +
                "this plug-in's registration journal.";
        }
        if (!drift.Drifted)
            return $"Rhino process {rhinoProcessId} exited leaving the restored registration unchanged.";
        return $"Rhino process {rhinoProcessId} exited after writing " +
            $"'{drift.ObservedMachineRegistration ?? drift.ObservedUserRegistration}' into its " +
            "registration. The pre-launch state " +
            $"('{drift.ExpectedMachineRegistration ?? drift.ExpectedUserRegistration ?? "no registration"}') " +
            "is restored.";
    }
}

internal sealed class LaunchExecutorOptions
{
    public IPluginNamespace PluginNamespace { get; init; } = RegistryPluginNamespace.Instance;
    public Func<int, string, bool> FileInUseInspector { get; init; } = FileUse.IsFileMappedByProcess;
    public Func<ProcessStartInfo, Process> RhinoProcessStarter { get; init; } = StartDirectly;
    public TimeSpan FileUsePollDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    // The executor is already a child of the interactive shell, so Rhino is started here
    // without a second hop through it.
    private static Process StartDirectly(ProcessStartInfo startInfo) => Process.Start(startInfo) ??
        throw new LaunchDiagnosticException(
            LaunchExecutorCodes.LaunchFailed,
            $"Windows did not start '{startInfo.FileName}'.");
}

// One seam over everything registered for one (Rhino version, plug-in ID) pair, so a test
// can run the whole choreography against isolated registry roots.
internal interface IPluginNamespace
{
    Task RecoverAsync(
        PluginNamespaceLeaseRequest request,
        IProgress<FileLockWait>? waiting,
        CancellationToken cancellationToken);

    Task<PluginNamespaceLeaseResult> AcquireAsync(
        PluginNamespaceLeaseRequest request,
        IProgress<FileLockWait>? waiting,
        CancellationToken cancellationToken);

    Task<RegistrationDrift> CorrectAfterExitAsync(
        PluginNamespaceLeaseRequest request,
        CancellationToken cancellationToken);
}

internal sealed class RegistryPluginNamespace : IPluginNamespace
{
    public static RegistryPluginNamespace Instance { get; } = new RegistryPluginNamespace();

    public Task RecoverAsync(
        PluginNamespaceLeaseRequest request,
        IProgress<FileLockWait>? waiting,
        CancellationToken cancellationToken) => PluginNamespaceLease.RecoverAsync(
            request.LocksDirectory,
            request.RhinoVersion,
            request.PluginId,
            request.Holder,
            waiting,
            cancellationToken);

    public Task<PluginNamespaceLeaseResult> AcquireAsync(
        PluginNamespaceLeaseRequest request,
        IProgress<FileLockWait>? waiting,
        CancellationToken cancellationToken) =>
        PluginNamespaceLease.AcquireAsync(request, waiting, cancellationToken);

    public Task<RegistrationDrift> CorrectAfterExitAsync(
        PluginNamespaceLeaseRequest request,
        CancellationToken cancellationToken) =>
        PluginNamespaceLease.CorrectAfterExitAsync(
            request.LocksDirectory,
            request.RhinoVersion,
            request.PluginId,
            request.Holder,
            cancellationToken);
}

// Every event the executor emits goes to the caller and to the executor's own log, so a
// launch whose client died still leaves a complete record on disk.
internal sealed class ExecutorChannel
{
    private readonly LaunchExecutorRequest _request;
    private readonly IProgress<LaunchExecutorEvent> _events;
    private readonly ExecutorLog _log;

    public ExecutorChannel(
        LaunchExecutorRequest request,
        IProgress<LaunchExecutorEvent> events,
        ExecutorLog log)
    {
        _request = request;
        _events = events;
        _log = log;
    }

    public LaunchExecutorEvent? LastResult { get; private set; }

    public void Progress(LaunchStage stage, string code, string message) => Emit(new LaunchExecutorEvent
    {
        Kind = LaunchExecutorEventKind.Progress,
        LaunchId = _request.LaunchId,
        Stage = stage.ToString().ToLowerInvariant(),
        Code = code,
        Message = message,
        ExecutorLogPath = _log.Path
    });

    public LaunchExecutorEvent Result(
        string code,
        string message,
        bool succeeded = false,
        int rhinoProcessId = 0)
    {
        LaunchExecutorEvent result = new LaunchExecutorEvent
        {
            Kind = LaunchExecutorEventKind.Result,
            LaunchId = _request.LaunchId,
            Stage = LaunchStage.Complete.ToString().ToLowerInvariant(),
            Code = code,
            Message = message,
            Severity = succeeded ? "info" : "error",
            Succeeded = succeeded,
            RhinoProcessId = rhinoProcessId,
            ExecutorLogPath = _log.Path
        };
        LastResult = result;
        Emit(result);
        return result;
    }

    private void Emit(LaunchExecutorEvent value)
    {
        _log.Append(value);
        _events.Report(value);
    }
}

// The executor's own append-only record of one launch, named in the launch log so a
// failure inside the executor process is readable from outside it.
internal sealed class ExecutorLog : IDisposable
{
    private readonly StreamWriter _writer;

    public ExecutorLog(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        // Shared for reading: a launch in progress is exactly when someone wants to read
        // how far its executor reached.
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
    }

    public string Path { get; }

    public static string PathFor(LaunchExecutorRequest request) => System.IO.Path.Combine(
        request.LogsDirectory,
        $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{request.LaunchId}.executor.jsonl");

    public void Append(LaunchExecutorEvent value) =>
        _writer.WriteLine(JsonSerializer.Serialize(value, JsonDefaults.Line));

    public void Dispose() => _writer.Dispose();
}

internal sealed class ImmediateProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public ImmediateProgress(Action<T> handler) => _handler = handler;

    public void Report(T value) => _handler(value);
}
