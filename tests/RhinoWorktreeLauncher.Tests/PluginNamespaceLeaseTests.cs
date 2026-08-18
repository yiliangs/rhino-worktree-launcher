using Microsoft.Win32;
using System.Runtime.Versioning;

namespace RhinoWorktreeLauncher.Tests;

// The lease normally spans HKCU and HKLM. These tests run its full journal, displace and
// restore cycle against two isolated current-user sandbox keys, one standing in for each
// hive, so the machine registry is never written.
public sealed class PluginNamespaceLeaseTests
{
    [Fact]
    public async Task Lease_seeds_an_unseen_plugin_with_the_documented_install_registration()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using LeaseSandbox sandbox = new LeaseSandbox();
        string selected = sandbox.PathFor("selected/Sample.rhp");

        PluginNamespaceLeaseResult result = await sandbox.AcquireAsync(selected);

        Assert.Null(result.Refusal);
        Assert.Null(result.DisplacedUserRegistration);
        Assert.Null(result.DisplacedMachineRegistration);
        using (RegistryKey seed = sandbox.OpenUserRegistration()!)
        {
            Assert.Equal("Sample", seed.GetValue("Name"));
            Assert.Equal(Path.GetFullPath(selected), seed.GetValue("FileName"));
            // Nothing was displaced, so there is no recorded load mode to carry and the
            // seed stays exactly Name and FileName.
            Assert.Equal(2, seed.GetValueNames().Length);
            Assert.Empty(seed.GetSubKeyNames());
        }
        Assert.True(File.Exists(sandbox.JournalPath));

        result.Lease!.Dispose();

        Assert.Null(sandbox.OpenUserRegistration());
        Assert.False(File.Exists(sandbox.JournalPath));
    }

    [Fact]
    public async Task Lease_reseeds_over_an_existing_registration_and_restores_it_exactly()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using LeaseSandbox sandbox = new LeaseSandbox();
        const string original = @"%TEMP%\Sample\Sample.rhp";
        using (RegistryKey existing = sandbox.CreateUserRegistration())
        {
            existing.SetValue("Name", "Installed", RegistryValueKind.String);
            existing.SetValue("LoadMode", 2, RegistryValueKind.DWord);
            using RegistryKey plugin = existing.CreateSubKey("PlugIn", writable: true)!;
            plugin.SetValue("FileName", original, RegistryValueKind.ExpandString);
        }

        PluginNamespaceLeaseResult result = await sandbox.AcquireAsync(
            sandbox.PathFor("selected/Sample.rhp"));

        // The displaced path is reported as Rhino would resolve it, expanded.
        Assert.Equal(Environment.ExpandEnvironmentVariables(original), result.DisplacedUserRegistration);
        using (RegistryKey seed = sandbox.OpenUserRegistration()!)
        {
            // Every launch is a fresh install-load, so the earlier occupant's values are
            // gone for the duration rather than merged with the seed. Only the recorded
            // load mode is deliberately carried forward.
            Assert.Equal("Sample", seed.GetValue("Name"));
            Assert.Empty(seed.GetSubKeyNames());
            Assert.Equal(3, seed.GetValueNames().Length);
        }

        result.Lease!.Dispose();

        using RegistryKey restored = sandbox.OpenUserRegistration()!;
        Assert.Equal("Installed", restored.GetValue("Name"));
        Assert.Equal(2, restored.GetValue("LoadMode"));
        Assert.Null(restored.GetValue("FileName"));
        using RegistryKey restoredPlugin = restored.OpenSubKey("PlugIn")!;
        Assert.Equal(RegistryValueKind.ExpandString, restoredPlugin.GetValueKind("FileName"));
        Assert.Equal(
            original,
            restoredPlugin.GetValue("FileName", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
    }

    [Fact]
    public async Task Seed_carries_the_load_mode_the_displaced_user_registration_recorded()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using LeaseSandbox sandbox = new LeaseSandbox();
        using (RegistryKey installed = sandbox.CreateUserRegistration())
        {
            installed.SetValue("LoadMode", 1, RegistryValueKind.DWord);
            using RegistryKey plugin = installed.CreateSubKey("PlugIn", writable: true)!;
            plugin.SetValue("FileName", @"C:\primary\Sample.rhp", RegistryValueKind.String);
        }

        PluginNamespaceLeaseResult result = await sandbox.AcquireAsync(
            sandbox.PathFor("selected/Sample.rhp"));

        using (RegistryKey seed = sandbox.OpenUserRegistration()!)
        {
            Assert.Equal(1, seed.GetValue("LoadMode"));
            Assert.Equal(RegistryValueKind.DWord, seed.GetValueKind("LoadMode"));
        }

        result.Lease!.Dispose();
    }

    [Fact]
    public async Task Seed_carries_the_load_mode_a_displaced_machine_registration_recorded()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using LeaseSandbox sandbox = new LeaseSandbox();
        using (RegistryKey competing = sandbox.CreateMachineRegistration())
        {
            competing.SetValue("LoadMode", 1, RegistryValueKind.DWord);
            using RegistryKey plugin = competing.CreateSubKey("PlugIn", writable: true)!;
            plugin.SetValue("FileName", @"C:\primary\Sample.rhp", RegistryValueKind.String);
        }

        PluginNamespaceLeaseResult result = await sandbox.AcquireAsync(
            sandbox.PathFor("selected/Sample.rhp"));

        using (RegistryKey seed = sandbox.OpenUserRegistration()!)
            Assert.Equal(1, seed.GetValue("LoadMode"));

        result.Lease!.Dispose();
    }

    // Rhino resolves a duplicate plug-in ID to the machine registration, so the machine
    // hive holds the load mode Rhino was actually using for this ID.
    [Fact]
    public async Task A_displaced_machine_load_mode_wins_over_the_current_user_one()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using LeaseSandbox sandbox = new LeaseSandbox();
        using (RegistryKey user = sandbox.CreateUserRegistration())
        {
            user.SetValue("LoadMode", 2, RegistryValueKind.DWord);
            using RegistryKey plugin = user.CreateSubKey("PlugIn", writable: true)!;
            plugin.SetValue("FileName", @"C:\other\Sample.rhp", RegistryValueKind.String);
        }
        using (RegistryKey competing = sandbox.CreateMachineRegistration())
        {
            competing.SetValue("LoadMode", 1, RegistryValueKind.DWord);
            using RegistryKey plugin = competing.CreateSubKey("PlugIn", writable: true)!;
            plugin.SetValue("FileName", @"C:\primary\Sample.rhp", RegistryValueKind.String);
        }

        PluginNamespaceLeaseResult result = await sandbox.AcquireAsync(
            sandbox.PathFor("selected/Sample.rhp"));

        using (RegistryKey seed = sandbox.OpenUserRegistration()!)
            Assert.Equal(1, seed.GetValue("LoadMode"));

        result.Lease!.Dispose();
    }

    // A disabled registration is the one load mode the launch must not reproduce: the
    // launch exists to load the selected artifact, and verification waits on it.
    [Fact]
    public async Task A_disabled_load_mode_is_not_carried_into_the_seed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using LeaseSandbox sandbox = new LeaseSandbox();
        using (RegistryKey disabled = sandbox.CreateUserRegistration())
        {
            disabled.SetValue("LoadMode", 0, RegistryValueKind.DWord);
            using RegistryKey plugin = disabled.CreateSubKey("PlugIn", writable: true)!;
            plugin.SetValue("FileName", @"C:\primary\Sample.rhp", RegistryValueKind.String);
        }

        PluginNamespaceLeaseResult result = await sandbox.AcquireAsync(
            sandbox.PathFor("selected/Sample.rhp"));

        using (RegistryKey seed = sandbox.OpenUserRegistration()!)
        {
            Assert.Null(seed.GetValue("LoadMode"));
            Assert.Equal(2, seed.GetValueNames().Length);
        }

        result.Lease!.Dispose();
    }

    [Fact]
    public async Task Lease_displaces_a_competing_machine_registration_and_restores_it()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using LeaseSandbox sandbox = new LeaseSandbox();
        using (RegistryKey competing = sandbox.CreateMachineRegistration())
        {
            using RegistryKey plugin = competing.CreateSubKey("PlugIn", writable: true)!;
            plugin.SetValue("FileName", @"C:\primary\Sample.rhp", RegistryValueKind.String);
        }

        PluginNamespaceLeaseResult result = await sandbox.AcquireAsync(
            sandbox.PathFor("selected/Sample.rhp"));

        Assert.Null(result.Refusal);
        Assert.Equal(@"C:\primary\Sample.rhp", result.DisplacedMachineRegistration);
        Assert.Null(sandbox.OpenMachineRegistration());

        result.Lease!.Dispose();

        using RegistryKey restored = sandbox.OpenMachineRegistration()!;
        using RegistryKey restoredPlugin = restored.OpenSubKey("PlugIn")!;
        Assert.Equal(@"C:\primary\Sample.rhp", restoredPlugin.GetValue("FileName"));
        Assert.False(File.Exists(sandbox.JournalPath));
    }

    [Fact]
    public async Task A_seed_form_machine_registration_competes_for_the_same_plugin_id()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using LeaseSandbox sandbox = new LeaseSandbox();
        // A seed carries FileName at the root and no PlugIn subkey, and still claims the
        // ID: Rhino installs it at its next startup.
        using (RegistryKey competing = sandbox.CreateMachineRegistration())
            competing.SetValue("FileName", @"C:\primary\Sample.rhp", RegistryValueKind.String);

        PluginNamespaceLeaseResult result = await sandbox.AcquireAsync(
            sandbox.PathFor("selected/Sample.rhp"));

        Assert.Equal(@"C:\primary\Sample.rhp", result.DisplacedMachineRegistration);
        Assert.Null(sandbox.OpenMachineRegistration());
        result.Lease!.Dispose();
        Assert.NotNull(sandbox.OpenMachineRegistration());
    }

    [Fact]
    public async Task A_machine_registration_naming_the_selected_artifact_is_not_displaced()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using LeaseSandbox sandbox = new LeaseSandbox();
        string selected = sandbox.PathFor("selected/Sample.rhp");
        using (RegistryKey installed = sandbox.CreateMachineRegistration())
        {
            using RegistryKey plugin = installed.CreateSubKey("PlugIn", writable: true)!;
            plugin.SetValue("FileName", Path.GetFullPath(selected), RegistryValueKind.String);
        }

        PluginNamespaceLeaseResult result = await sandbox.AcquireAsync(selected);

        Assert.Null(result.Refusal);
        Assert.Null(result.DisplacedMachineRegistration);
        Assert.NotNull(sandbox.OpenMachineRegistration());
        result.Lease!.Dispose();
    }

    // Rhino already holds a complete registration for exactly the file the launch wants
    // loaded, so a seed beside it only asks Rhino to install an ID that is already
    // registered. The launch needs no current-user shape at all here.
    [Fact]
    public async Task No_seed_is_written_when_the_machine_registration_already_names_the_selected_artifact()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using LeaseSandbox sandbox = new LeaseSandbox();
        string selected = sandbox.PathFor("selected/Sample.rhp");
        using (RegistryKey installed = sandbox.CreateMachineRegistration())
        {
            installed.SetValue("LoadMode", 1, RegistryValueKind.DWord);
            using RegistryKey plugin = installed.CreateSubKey("PlugIn", writable: true)!;
            plugin.SetValue("FileName", Path.GetFullPath(selected), RegistryValueKind.String);
        }

        PluginNamespaceLeaseResult result = await sandbox.AcquireAsync(selected);

        Assert.Null(result.Refusal);
        Assert.Null(sandbox.OpenUserRegistration());
        Assert.NotNull(sandbox.OpenMachineRegistration());

        result.Lease!.Dispose();

        Assert.Null(sandbox.OpenUserRegistration());
    }

    // The current-user hive is still cleared: a registration naming another file would
    // otherwise stay live for the launch.
    [Fact]
    public async Task A_current_user_registration_is_cleared_even_when_no_seed_is_written()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using LeaseSandbox sandbox = new LeaseSandbox();
        string selected = sandbox.PathFor("selected/Sample.rhp");
        using (RegistryKey stale = sandbox.CreateUserRegistration())
        {
            using RegistryKey plugin = stale.CreateSubKey("PlugIn", writable: true)!;
            plugin.SetValue("FileName", @"C:\other\Sample.rhp", RegistryValueKind.String);
        }
        using (RegistryKey installed = sandbox.CreateMachineRegistration())
        {
            using RegistryKey plugin = installed.CreateSubKey("PlugIn", writable: true)!;
            plugin.SetValue("FileName", Path.GetFullPath(selected), RegistryValueKind.String);
        }

        PluginNamespaceLeaseResult result = await sandbox.AcquireAsync(selected);

        Assert.Equal(@"C:\other\Sample.rhp", result.DisplacedUserRegistration);
        Assert.Null(sandbox.OpenUserRegistration());

        result.Lease!.Dispose();

        using RegistryKey restored = sandbox.OpenUserRegistration()!;
        using RegistryKey restoredPlugin = restored.OpenSubKey("PlugIn")!;
        Assert.Equal(@"C:\other\Sample.rhp", restoredPlugin.GetValue("FileName"));
    }

    [Fact]
    public async Task Recovery_deletes_the_install_seed_a_killed_launch_left_behind()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using LeaseSandbox sandbox = new LeaseSandbox();
        PluginNamespaceLeaseResult result = await sandbox.AcquireAsync(
            sandbox.PathFor("selected/Sample.rhp"));
        Assert.NotNull(result.Lease);
        // A killed launch is a lease whose process died with the seed written and the
        // journal on disk. The seed is a live instruction: an ordinary Rhino session would
        // install the worktree artifact permanently.
        sandbox.Abandon(result.Lease!);
        Assert.NotNull(sandbox.OpenUserRegistration());

        await sandbox.RecoverAsync();

        Assert.Null(sandbox.OpenUserRegistration());
        Assert.False(File.Exists(sandbox.JournalPath));
    }

    [Fact]
    public async Task Recovery_restores_a_machine_registration_a_killed_launch_displaced()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using LeaseSandbox sandbox = new LeaseSandbox();
        using (RegistryKey competing = sandbox.CreateMachineRegistration())
        {
            using RegistryKey plugin = competing.CreateSubKey("PlugIn", writable: true)!;
            plugin.SetValue("FileName", @"C:\primary\Sample.rhp", RegistryValueKind.String);
        }
        PluginNamespaceLeaseResult result = await sandbox.AcquireAsync(
            sandbox.PathFor("selected/Sample.rhp"));
        sandbox.Abandon(result.Lease!);

        await sandbox.RecoverAsync();

        using RegistryKey restored = sandbox.OpenMachineRegistration()!;
        using RegistryKey restoredPlugin = restored.OpenSubKey("PlugIn")!;
        Assert.Equal(@"C:\primary\Sample.rhp", restoredPlugin.GetValue("FileName"));
        Assert.Null(sandbox.OpenUserRegistration());
        Assert.False(File.Exists(sandbox.JournalPath));
    }

    [Fact]
    public async Task Recovery_without_a_journal_is_a_no_op()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using LeaseSandbox sandbox = new LeaseSandbox();
        using (RegistryKey untouched = sandbox.CreateUserRegistration())
            untouched.SetValue("FileName", @"C:\primary\Sample.rhp", RegistryValueKind.String);

        await sandbox.RecoverAsync();

        using RegistryKey survived = sandbox.OpenUserRegistration()!;
        Assert.Equal(@"C:\primary\Sample.rhp", survived.GetValue("FileName"));
    }

    [Fact]
    public async Task Leases_for_the_same_plugin_serialize_across_callers()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using LeaseSandbox sandbox = new LeaseSandbox();
        PluginNamespaceLeaseResult first = await sandbox.AcquireAsync(sandbox.PathFor("first/Sample.rhp"));
        Task<PluginNamespaceLeaseResult> secondTask = sandbox.AcquireAsync(
            sandbox.PathFor("second/Sample.rhp"));

        await Task.Delay(250);
        Assert.False(secondTask.IsCompleted);

        first.Lease!.Dispose();
        PluginNamespaceLeaseResult second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        second.Lease!.Dispose();
    }

    // Two isolated current-user keys stand in for the two hives, so the full cycle runs
    // without ever writing the machine registry.
    [SupportedOSPlatform("windows")]
    private sealed class LeaseSandbox : IDisposable
    {
        private readonly TemporaryDirectory _temporary = new TemporaryDirectory();
        private readonly string _root = $@"Software\RhinoWorktreeLauncherTests\{Guid.NewGuid():N}";
        private readonly Guid _pluginId = Guid.NewGuid();
        private readonly List<IDisposable> _leases = new List<IDisposable>();

        public LeaseSandbox() => Directory.CreateDirectory(_temporary.PathFor("locks"));

        public string JournalPath => _temporary.PathFor("locks/namespace.json");

        private string LockPath => _temporary.PathFor("locks/namespace.lock");

        private string UserPluginsKeyPath => $@"{_root}\user";

        private string MachinePluginsKeyPath => $@"{_root}\machine";

        public string PathFor(string relativePath) => _temporary.PathFor(relativePath);

        public async Task<PluginNamespaceLeaseResult> AcquireAsync(string pluginPath)
        {
            PluginNamespaceLeaseResult result = await PluginNamespaceLease.AcquireAsync(
                Registry.CurrentUser,
                UserPluginsKeyPath,
                Registry.CurrentUser,
                MachinePluginsKeyPath,
                JournalPath,
                LockPath,
                _pluginId,
                "Sample",
                pluginPath,
                CancellationToken.None);
            // Tracked so a failed assertion releases the lock file instead of masking
            // itself behind a locked-directory teardown error.
            if (result.Lease is not null)
                _leases.Add(result.Lease);
            return result;
        }

        public Task RecoverAsync() => PluginNamespaceLease.RecoverAsync(
            Registry.CurrentUser,
            UserPluginsKeyPath,
            Registry.CurrentUser,
            MachinePluginsKeyPath,
            JournalPath,
            LockPath,
            CancellationToken.None);

        public RegistryKey CreateUserRegistration() =>
            Registry.CurrentUser.CreateSubKey($@"{UserPluginsKeyPath}\{_pluginId:D}", writable: true)!;

        public RegistryKey CreateMachineRegistration() =>
            Registry.CurrentUser.CreateSubKey($@"{MachinePluginsKeyPath}\{_pluginId:D}", writable: true)!;

        public RegistryKey? OpenUserRegistration() =>
            Registry.CurrentUser.OpenSubKey($@"{UserPluginsKeyPath}\{_pluginId:D}", writable: false);

        public RegistryKey? OpenMachineRegistration() =>
            Registry.CurrentUser.OpenSubKey($@"{MachinePluginsKeyPath}\{_pluginId:D}", writable: false);

        // Simulates the killed process: the lock is released without restoring, so the
        // journal and the mutated hives stay as the launch left them.
        public void Abandon(IDisposable lease)
        {
            string journal = File.ReadAllText(JournalPath);
            lease.Dispose();
            File.WriteAllText(JournalPath, journal);
            Registry.CurrentUser.DeleteSubKeyTree($@"{UserPluginsKeyPath}\{_pluginId:D}", throwOnMissingSubKey: false);
            using RegistryKey seed = CreateUserRegistration();
            seed.SetValue("Name", "Sample", RegistryValueKind.String);
            seed.SetValue("FileName", PathFor("selected/Sample.rhp"), RegistryValueKind.String);
            Registry.CurrentUser.DeleteSubKeyTree(
                $@"{MachinePluginsKeyPath}\{_pluginId:D}",
                throwOnMissingSubKey: false);
        }

        public void Dispose()
        {
            foreach (IDisposable lease in _leases)
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
            _temporary.Dispose();
        }
    }
}
