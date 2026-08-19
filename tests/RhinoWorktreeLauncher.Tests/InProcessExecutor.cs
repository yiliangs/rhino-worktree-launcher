using Rwl.Protocol;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace RhinoWorktreeLauncher.Tests;

// Runs the shipped executor engine in the test process instead of spawning one through the
// interactive shell. The choreography under test is the same object the executor process
// runs; only the transport is skipped, and the transport has its own tests.
[SupportedOSPlatform("windows")]
internal static class InProcessExecutor
{
    public static Func<LaunchExecutorRequest, IProgress<LaunchExecutorEvent>, CancellationToken,
        Task<LaunchExecutorEvent>> For(
        IPluginNamespace pluginNamespace,
        LaunchBackendTests.FakeRhino rhino) => For(
            pluginNamespace,
            rhino.Start,
            rhino.IsFileInUse);

    public static Func<LaunchExecutorRequest, IProgress<LaunchExecutorEvent>, CancellationToken,
        Task<LaunchExecutorEvent>> For(
        IPluginNamespace pluginNamespace,
        Func<ProcessStartInfo, Process> rhinoProcessStarter,
        Func<int, string, bool> fileInUseInspector) => async (request, events, cancellationToken) =>
        {
            using ExecutorLog log = new ExecutorLog(ExecutorLog.PathFor(request));
            return await new LaunchExecutorEngine(new LaunchExecutorOptions
            {
                PluginNamespace = pluginNamespace,
                RegistryProbeRunner = TestRegistryProbe.Truthful,
                RhinoProcessStarter = rhinoProcessStarter,
                FileInUseInspector = fileInUseInspector,
                FileUsePollDelay = TimeSpan.FromMilliseconds(50)
            }).RunAsync(request, events, log, CancellationToken.None, cancellationToken);
        };
}

// A namespace that reports one fixed outcome, for the cases that are about what the launch
// says rather than what the registry holds.
internal sealed class StubPluginNamespace : IPluginNamespace
{
    private readonly PluginNamespaceLeaseResult _result;

    public StubPluginNamespace(PluginRegistrationConflict refusal) => _result = new PluginNamespaceLeaseResult(
        Lease: null,
        refusal,
        DisplacedMachineRegistration: null,
        DisplacedUserRegistration: null,
        Seed: null);

    public StubPluginNamespace(
        IPluginNamespaceLease lease,
        string? DisplacedMachineRegistration = null,
        string? DisplacedUserRegistration = null) => _result = new PluginNamespaceLeaseResult(
            lease,
            Refusal: null,
            DisplacedMachineRegistration,
            DisplacedUserRegistration,
            Seed: null);

    public Task RecoverAsync(
        PluginNamespaceLeaseRequest request,
        IProgress<FileLockWait>? waiting,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<PluginNamespaceLeaseResult> AcquireAsync(
        PluginNamespaceLeaseRequest request,
        IProgress<FileLockWait>? waiting,
        CancellationToken cancellationToken) => Task.FromResult(_result);

    public Task<RegistrationDrift> CorrectAfterExitAsync(
        PluginNamespaceLeaseRequest request,
        CancellationToken cancellationToken) => Task.FromResult(RegistrationDrift.NoJournal);
}
