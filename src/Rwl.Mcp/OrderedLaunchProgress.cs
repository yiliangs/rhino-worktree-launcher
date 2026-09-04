using ModelContextProtocol;
using RhinoWorktreeLauncher;

namespace Rwl.Mcp;

/// <summary>
/// Relays a launch's backend updates to the MCP session as numbered progress notifications, on
/// the thread that reported them.
/// </summary>
/// <remarks>
/// <para>
/// A progress notification describes work whose end the terminal tool result announces, so it is
/// only ever useful ahead of that result. <see cref="Progress{T}"/> cannot carry that guarantee
/// here. It captures <see cref="SynchronizationContext.Current"/> when it is constructed, and a
/// console hosted server has none, so it hands every callback to the thread pool instead of
/// running it. The launch can then finish and the tool result be written while a queued callback
/// has still not run, which puts a notification behind the result it described the run up to.
/// </para>
/// <para>
/// Reporting inline puts the notification order back under the backend's own call order: an
/// update has reached the session before the reporting call returns, and therefore before the
/// launch that reported it completes. One lock covers the number and the hand off together, so
/// two updates reported at the same moment cannot reach the session in the opposite order to the
/// numbers they carry.
/// </para>
/// </remarks>
internal sealed class OrderedLaunchProgress : IProgress<LaunchProgress>
{
    private readonly IProgress<ProgressNotificationValue> _session;
    private readonly object _gate = new object();
    private int _reported;

    public OrderedLaunchProgress(IProgress<ProgressNotificationValue> session) => _session = session;

    public void Report(LaunchProgress value)
    {
        lock (_gate)
        {
            _session.Report(new ProgressNotificationValue
            {
                Progress = ++_reported,
                Message = $"{value.StageToken}: {value.Message}"
            });
        }
    }
}
