using System.IO.Pipes;
using System.Text;
using Rwl.Protocol;

namespace RhinoWorktreeLauncher;

// The launcher host's half of the executor contract. It owns the pipe, spawns one executor
// through the interactive Windows shell, and relays what that executor reports. It performs
// no registry work of its own, which is the whole point: this process may be sandboxed
// (ADR 0015).
internal static class LaunchExecutorClient
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    public static Task<LaunchExecutorEvent> InvokeAsync(
        LaunchExecutorRequest request,
        IProgress<LaunchExecutorEvent> events,
        CancellationToken cancellationToken) => RunAsync(request, events, StartTimeout, cancellationToken);

    // Proves the spawn chain before a launch depends on it: shell to bootstrap to executor
    // to pipe, with no registration touched. A host that cannot complete this can only fail
    // every launch, and says so up front instead of timing out later.
    public static async Task<LaunchExecutorEvent> PingAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken) => await RunAsync(
            new LaunchExecutorRequest
            {
                Mode = LaunchExecutorMode.Ping,
                LaunchId = Guid.NewGuid().ToString("N")
            },
            new ImmediateProgress<LaunchExecutorEvent>(_ => { }),
            timeout,
            cancellationToken);

    private static async Task<LaunchExecutorEvent> RunAsync(
        LaunchExecutorRequest request,
        IProgress<LaunchExecutorEvent> events,
        TimeSpan startTimeout,
        CancellationToken cancellationToken)
    {
        string bootstrapPath = InteractiveProcessSpawner.ResolveBootstrapPath();
        string pipeName = $"rwl-executor-{Guid.NewGuid():N}";
        using NamedPipeServerStream pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using IDisposable spawned = InteractiveProcessSpawner.Spawn(
            bootstrapPath,
            $"launch-executor --pipe {pipeName}");

        using CancellationTokenSource start = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        start.CancelAfter(startTimeout);
        try
        {
            await pipe.WaitForConnectionAsync(start.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LaunchDiagnosticException(
                LaunchExecutorCodes.ExecutorStartTimeout,
                $"The interactive Windows shell did not start a launch executor within " +
                $"{startTimeout.TotalSeconds:0.###} seconds. Run 'rwl doctor' to check the " +
                "installation, and confirm that explorer.exe is running for this session.",
                exception);
        }

        using StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
        using StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        await writer.WriteLineAsync(LaunchExecutorProtocol.SerializeRequest(request))
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        while (true)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new LaunchDiagnosticException(
                    LaunchExecutorCodes.ExecutorPipeClosed,
                    "The launch executor ended without reporting a result. Its own log records how " +
                    "far it reached, and the registration journal is restored by the next launch.");
            }

            LaunchExecutorEvent value = LaunchExecutorProtocol.DeserializeEvent(line) ??
                throw new LaunchDiagnosticException(
                    LaunchExecutorCodes.ExecutorProtocolViolation,
                    $"The launch executor sent '{line}', which is not a launch event.");
            if (value.IsResult)
                return value;
            events.Report(value);
        }
    }
}
