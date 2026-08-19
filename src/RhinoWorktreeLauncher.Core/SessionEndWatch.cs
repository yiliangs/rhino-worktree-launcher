namespace RhinoWorktreeLauncher;

/// <summary>
/// How a stdio session ended. A stdio server is reachable only through the standard streams
/// its client owns, so once that client is gone nobody can send it a request and nobody reads
/// its answers: the process has nothing left to serve and must not outlive the session.
/// </summary>
public sealed record SessionEnd(string Code, string Message);

/// <summary>
/// Ends this process when its session does. It watches the signals that a session is over,
/// asks the host to stop, and, if the host has not stopped within the grace it allows, ends
/// the process anyway rather than becoming an orphan that serves the release it was spawned
/// from forever.
/// </summary>
public sealed class SessionEndWatch
{
    /// <summary>
    /// How long a stopping host is given. In-flight work is not abandoned silently: a launch
    /// in flight runs in the detached launch executor, which owns the registration journal
    /// and treats a vanished client as its named disconnect (ADR 0015), so the bound costs
    /// the launch nothing it does not already handle.
    /// </summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(10);

    private SessionEndWatch(Task watching) => Watching = watching;

    /// <summary>
    /// Completes when a session-end signal has been handled, so a test can wait for the
    /// decision instead of the process exit it causes.
    /// </summary>
    public Task Watching { get; }

    public static SessionEndWatch Start(
        Func<SessionEnd, CancellationToken, Task> stop,
        Action<SessionEnd, string> abandon,
        Action<string> report) => Start(
            SessionEndSignals.ForCurrentProcess(report),
            DefaultGrace,
            stop,
            abandon);

    internal static SessionEndWatch Start(
        IReadOnlyList<Task<SessionEnd>> signals,
        TimeSpan grace,
        Func<SessionEnd, CancellationToken, Task> stop,
        Action<SessionEnd, string> abandon) => new SessionEndWatch(WatchAsync(signals, grace, stop, abandon));

    private static async Task WatchAsync(
        IReadOnlyList<Task<SessionEnd>> signals,
        TimeSpan grace,
        Func<SessionEnd, CancellationToken, Task> stop,
        Action<SessionEnd, string> abandon)
    {
        if (signals.Count == 0)
        {
            // No observable signal at all. SessionEndSignals has already reported why for
            // each one it could not establish, so this stays silent rather than repeating it.
            return;
        }

        SessionEnd end = await await Task.WhenAny(signals).ConfigureAwait(false);
        using CancellationTokenSource graceful = new CancellationTokenSource(grace);
        try
        {
            await stop(end, graceful.Token).WaitAsync(grace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            abandon(end, $"The host did not stop within {grace.TotalSeconds:0.###} seconds.");
        }
        catch (OperationCanceledException)
        {
            abandon(end, $"The host was still stopping after {grace.TotalSeconds:0.###} seconds.");
        }
        // A host that fails to stop must not keep the process alive either, and the failure
        // is named rather than swallowed.
        catch (Exception exception)
        {
            abandon(end, $"The host failed to stop: {exception.Message}");
        }
    }
}
