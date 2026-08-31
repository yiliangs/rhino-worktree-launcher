using System.IO.Pipes;
using System.Text;
using Rwl.Protocol;

namespace RhinoWorktreeLauncher;

/// <summary>
/// The executor process's entry point. It is started by the interactive Windows shell, not
/// by the launcher host that wants the launch, so every registry mutation it performs
/// happens outside any per-process interception the host is subject to (ADR 0015).
/// </summary>
public static class LaunchExecutorHost
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    public static async Task<int> RunAsync(string pipeName, CancellationToken cancellationToken)
    {
        using NamedPipeClientStream pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using CancellationTokenSource connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connect.CancelAfter(ConnectTimeout);
        // A host that is already gone leaves nobody to report to. The launcher host reports
        // that side of the same failure as executor_start_timeout.
        await pipe.ConnectAsync(connect.Token);

        using StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
        using StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        using CancellationTokenSource disconnected = new CancellationTokenSource();
        ExecutorPipe channel = new ExecutorPipe(writer, disconnected);

        string? requestLine = await reader.ReadLineAsync(connect.Token);
        LaunchExecutorRequest? request = requestLine is null
            ? null
            : LaunchExecutorProtocol.DeserializeRequest(requestLine);
        if (request is null)
        {
            channel.Send(Failure(
                LaunchExecutorCodes.ExecutorRequestInvalid,
                "The launcher host closed the pipe without sending a readable launch request."));
            return 1;
        }
        if (request.ProtocolVersion != LaunchExecutorProtocol.Version)
        {
            channel.Send(Failure(
                LaunchExecutorCodes.ExecutorProtocolMismatch,
                $"This executor speaks launch protocol {LaunchExecutorProtocol.Version} and the " +
                $"launcher host sent protocol {request.ProtocolVersion}. Both come from one " +
                "installed release, so a mismatch means the installation is half-updated: " +
                "reinstall RWL.",
                request.LaunchId));
            return 1;
        }
        if (string.Equals(request.Mode, LaunchExecutorMode.Ping, StringComparison.Ordinal))
        {
            channel.Send(new LaunchExecutorEvent
            {
                Kind = LaunchExecutorEventKind.Result,
                LaunchId = request.LaunchId,
                Code = LaunchExecutorCodes.InteractiveSpawnReady,
                Message = $"The interactive Windows shell started launch executor " +
                    $"{Environment.ProcessId}.",
                Succeeded = true
            });
            return 0;
        }
        // Rewriting the standing registration displaces nothing, so this process ends with
        // its one terminal result: no linger, no journal, and nothing to correct after an
        // exit (ADR 0016).
        if (string.Equals(request.Mode, LaunchExecutorMode.SetRegistration, StringComparison.Ordinal))
        {
            using ExecutorLog switchLog = new ExecutorLog(ExecutorLog.PathFor(request));
            LaunchExecutorEvent switched = await new LaunchExecutorEngine().SwitchRegistrationAsync(
                request,
                new ImmediateProgress<LaunchExecutorEvent>(channel.Send),
                switchLog,
                disconnected.Token,
                cancellationToken);
            return switched.Succeeded ? 0 : 1;
        }
        if (!string.Equals(request.Mode, LaunchExecutorMode.Launch, StringComparison.Ordinal))
        {
            channel.Send(Failure(
                LaunchExecutorCodes.ExecutorRequestInvalid,
                $"'{request.Mode}' is not a launch executor mode.",
                request.LaunchId));
            return 1;
        }

        // The host's death is not a reason to leave a registration displaced. Reading the
        // pipe is how this process learns of it, because the host sends nothing else.
        Task watch = WatchForDisconnectAsync(reader, disconnected);
        using ExecutorLog log = new ExecutorLog(ExecutorLog.PathFor(request));
        LaunchExecutorEngine engine = new LaunchExecutorEngine();
        LaunchExecutorEvent result = await engine.RunAsync(
            request,
            new ImmediateProgress<LaunchExecutorEvent>(channel.Send),
            log,
            disconnected.Token,
            cancellationToken);
        if (!result.Succeeded)
            return 1;

        // The client has its answer and stops reading here. This process stays behind for
        // the one thing the client cannot observe: what Rhino writes into the registration
        // it loaded from, which only becomes final when Rhino exits.
        pipe.Dispose();
        await engine.CorrectAfterExitAsync(request, result.RhinoProcessId, log, cancellationToken);
        await watch;
        return 0;
    }

    private static LaunchExecutorEvent Failure(string code, string message, string launchId = "") =>
        new LaunchExecutorEvent
        {
            Kind = LaunchExecutorEventKind.Result,
            LaunchId = launchId,
            Code = code,
            Message = message,
            Severity = "error"
        };

    private static async Task WatchForDisconnectAsync(
        StreamReader reader,
        CancellationTokenSource disconnected)
    {
        try
        {
            // The host writes exactly one request, so any completion of this read is the
            // pipe ending: either the host closed it or the host died.
            _ = await reader.ReadLineAsync();
        }
        catch (IOException)
        {
            // A broken pipe is the same signal as a clean end of stream.
        }
        catch (ObjectDisposedException)
        {
            // The pipe is closed deliberately once a verified launch is reported.
            return;
        }

        try
        {
            disconnected.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The launch already ended and stopped listening for the disconnect it names.
        }
    }

    // The pipe carries reporting only. Losing it never stops the executor, because the
    // restore it still owes does not depend on anyone listening; it becomes the disconnect
    // signal instead, and the executor's own log keeps the record.
    private sealed class ExecutorPipe
    {
        private readonly StreamWriter _writer;
        private readonly CancellationTokenSource _disconnected;
        private bool _broken;

        public ExecutorPipe(StreamWriter writer, CancellationTokenSource disconnected)
        {
            _writer = writer;
            _disconnected = disconnected;
        }

        public void Send(LaunchExecutorEvent value)
        {
            if (_broken)
                return;
            try
            {
                _writer.WriteLine(LaunchExecutorProtocol.SerializeEvent(value));
            }
            catch (IOException)
            {
                _broken = true;
                _disconnected.Cancel();
            }
            catch (ObjectDisposedException)
            {
                _broken = true;
                _disconnected.Cancel();
            }
        }
    }
}
