using Rwl.Protocol;

namespace RhinoWorktreeLauncher;

/// <summary>
/// Whether this host can reach the interactive Windows shell at all, decided once when the
/// host starts rather than discovered by a launch that hangs. Every launch a degraded host
/// is asked for fails immediately, by name, because a host that cannot spawn an executor
/// cannot write a plug-in registration that Rhino will read (ADR 0015).
/// </summary>
public sealed class LaunchHostReadiness
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly Task<LaunchHostState> _state;

    public LaunchHostReadiness()
        : this(ProbeAsync)
    {
    }

    internal LaunchHostReadiness(Func<CancellationToken, Task<LaunchHostState>> probe) =>
        _state = probe(CancellationToken.None);

    /// <summary>
    /// The single answer this host started with. Awaiting it costs nothing once the probe
    /// that ran at startup has finished.
    /// </summary>
    public Task<LaunchHostState> StateAsync() => _state;

    private static async Task<LaunchHostState> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            LaunchExecutorEvent answered = await LaunchExecutorClient.PingAsync(
                ProbeTimeout,
                cancellationToken);
            return answered.Succeeded
                ? new LaunchHostState(true, answered.Code, answered.Message)
                : new LaunchHostState(false, answered.Code, Degraded(answered.Message));
        }
        catch (LaunchDiagnosticException exception)
        {
            return new LaunchHostState(false, exception.Code, Degraded(exception.Message));
        }
        // Anything else reaching here is still a host that cannot launch, and saying so is
        // the point: the alternative is a launch that waits for an executor that will never
        // arrive.
        catch (Exception exception)
        {
            return new LaunchHostState(
                false,
                LaunchExecutorCodes.InteractiveSpawnUnavailable,
                Degraded(exception.Message));
        }
    }

    private static string Degraded(string reason) =>
        $"{reason} Until that is fixed, this host cannot start a launch executor, and every " +
        "launch it is asked for will fail immediately rather than wait. Launch from the RWL " +
        "desktop application or from 'rwl launch' in an ordinary shell, and run 'rwl doctor' " +
        "for the details.";
}

/// <summary>
/// The host's launch readiness: ready, or a named reason it is not.
/// </summary>
public sealed record LaunchHostState(bool Ready, string Code, string Message);
