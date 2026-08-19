using Microsoft.Win32;
using System.Runtime.Versioning;

namespace RhinoWorktreeLauncher.Tests;

// The plug-in namespace bound to two isolated current-user keys, one standing in for each
// hive, so the full journal, displace, restore, and post-exit correction cycle runs without
// ever writing the machine registry or any McNeel key.
[SupportedOSPlatform("windows")]
internal sealed class RegistrySandbox : IPluginNamespace, IDisposable
{
    public const int RhinoVersion = 8;

    private readonly TemporaryDirectory _temporary;
    private readonly bool _ownsTemporary;
    private readonly string _root = $@"Software\RhinoWorktreeLauncherTests\{Guid.NewGuid():N}";
    private readonly List<IPluginNamespaceLease> _leases = new List<IPluginNamespaceLease>();

    public RegistrySandbox()
        : this(new TemporaryDirectory(), ownsTemporary: true)
    {
    }

    public RegistrySandbox(TemporaryDirectory temporary)
        : this(temporary, ownsTemporary: false)
    {
    }

    private RegistrySandbox(TemporaryDirectory temporary, bool ownsTemporary)
    {
        _temporary = temporary;
        _ownsTemporary = ownsTemporary;
        LocksDirectory = temporary.CreateDirectory("locks");
    }

    public Guid PluginId { get; } = Guid.NewGuid();

    public string LocksDirectory { get; }

    public string JournalPath => JournalPathFor(PluginId);

    public string LockPath => LockPathFor(PluginId);

    public string UserPluginsKeyPath => $@"{_root}\user";

    public string MachinePluginsKeyPath => $@"{_root}\machine";

    public string PathFor(string relativePath) => _temporary.PathFor(relativePath);

    public string JournalPathFor(Guid pluginId) =>
        Path.Combine(LocksDirectory, $"rhino-{RhinoVersion}-{pluginId:D}.registration.json");

    public string LockPathFor(Guid pluginId) =>
        Path.Combine(LocksDirectory, $"rhino-{RhinoVersion}-{pluginId:D}.registration.lock");

    public Task RecoverAsync(
        PluginNamespaceLeaseRequest request,
        IProgress<FileLockWait>? waiting,
        CancellationToken cancellationToken) => PluginNamespaceLease.RecoverAsync(
            Registry.CurrentUser,
            UserPluginsKeyPath,
            Registry.CurrentUser,
            MachinePluginsKeyPath,
            JournalPathFor(request.PluginId),
            LockPathFor(request.PluginId),
            request.Holder,
            waiting,
            cancellationToken);

    public async Task<PluginNamespaceLeaseResult> AcquireAsync(
        PluginNamespaceLeaseRequest request,
        IProgress<FileLockWait>? waiting,
        CancellationToken cancellationToken)
    {
        PluginNamespaceLeaseResult result = await PluginNamespaceLease.AcquireAsync(
            Registry.CurrentUser,
            UserPluginsKeyPath,
            Registry.CurrentUser,
            MachinePluginsKeyPath,
            JournalPathFor(request.PluginId),
            LockPathFor(request.PluginId),
            request.PluginId,
            request.PluginName,
            request.PluginPath,
            request.Holder,
            request.VisibilityNonce,
            waiting,
            cancellationToken);
        // Tracked so a failed assertion releases the lock file instead of masking itself
        // behind a locked-directory teardown error.
        if (result.Lease is not null)
            _leases.Add(result.Lease);
        return result;
    }

    public Task<RegistrationDrift> CorrectAfterExitAsync(
        PluginNamespaceLeaseRequest request,
        CancellationToken cancellationToken) =>
        PluginNamespaceLease.CorrectAfterExitAsync(
            Registry.CurrentUser,
            UserPluginsKeyPath,
            Registry.CurrentUser,
            MachinePluginsKeyPath,
            JournalPathFor(request.PluginId),
            LockPathFor(request.PluginId),
            request.Holder,
            cancellationToken);

    public Task<PluginNamespaceLeaseResult> AcquireAsync(string pluginPath) =>
        AcquireAsync(pluginPath, PluginId, CancellationToken.None);

    public Task<PluginNamespaceLeaseResult> AcquireAsync(
        string pluginPath,
        Guid pluginId,
        CancellationToken cancellationToken) => AcquireAsync(
            new PluginNamespaceLeaseRequest(
                LocksDirectory,
                RhinoVersion,
                pluginId,
                "Sample",
                pluginPath,
                Holder("test-launch"),
                Guid.NewGuid().ToString("N")),
            waiting: null,
            cancellationToken);

    public Task RecoverAsync() => RecoverAsync(
        new PluginNamespaceLeaseRequest(
            LocksDirectory,
            RhinoVersion,
            PluginId,
            "Sample",
            PathFor("selected/Sample.rhp"),
            Holder("test-recovery"),
            Guid.NewGuid().ToString("N")),
        waiting: null,
        CancellationToken.None);

    public static FileLockHolder Holder(string launchId) => new FileLockHolder(
        launchId,
        Environment.ProcessId,
        "test",
        DateTimeOffset.UtcNow);

    public RegistryKey CreateUserRegistration() => CreateUserRegistration(PluginId);

    public RegistryKey CreateUserRegistration(Guid pluginId) =>
        Registry.CurrentUser.CreateSubKey($@"{UserPluginsKeyPath}\{pluginId:D}", writable: true)!;

    public RegistryKey CreateMachineRegistration() => CreateMachineRegistration(PluginId);

    public RegistryKey CreateMachineRegistration(Guid pluginId) =>
        Registry.CurrentUser.CreateSubKey($@"{MachinePluginsKeyPath}\{pluginId:D}", writable: true)!;

    public RegistryKey? OpenUserRegistration() => OpenUserRegistration(PluginId);

    public RegistryKey? OpenUserRegistration(Guid pluginId) =>
        Registry.CurrentUser.OpenSubKey($@"{UserPluginsKeyPath}\{pluginId:D}", writable: false);

    public RegistryKey? OpenMachineRegistration() => OpenMachineRegistration(PluginId);

    public RegistryKey? OpenMachineRegistration(Guid pluginId) =>
        Registry.CurrentUser.OpenSubKey($@"{MachinePluginsKeyPath}\{pluginId:D}", writable: false);

    // Simulates a killed process: the lock is released without restoring, so the journal
    // and the mutated hives stay as the launch left them.
    public void Abandon(IPluginNamespaceLease lease)
    {
        string journal = File.ReadAllText(JournalPath);
        lease.Dispose();
        File.WriteAllText(JournalPath, journal);
        Registry.CurrentUser.DeleteSubKeyTree($@"{UserPluginsKeyPath}\{PluginId:D}", throwOnMissingSubKey: false);
        using RegistryKey seed = CreateUserRegistration();
        seed.SetValue("Name", "Sample", RegistryValueKind.String);
        seed.SetValue("FileName", PathFor("selected/Sample.rhp"), RegistryValueKind.String);
        Registry.CurrentUser.DeleteSubKeyTree(
            $@"{MachinePluginsKeyPath}\{PluginId:D}",
            throwOnMissingSubKey: false);
    }

    public void Dispose()
    {
        foreach (IPluginNamespaceLease lease in _leases)
        {
            try
            {
                lease.Dispose();
            }
            catch (IOException)
            {
                // Teardown must not mask the assertion that ended the test.
            }
        }
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        if (_ownsTemporary)
            _temporary.Dispose();
    }
}
