using Microsoft.Win32;
using Rwl.Protocol;

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

        using RegistrySandbox sandbox = new RegistrySandbox();
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
            // seed stays Name and FileName beside the visibility nonce.
            Assert.Equal(result.Seed!.Nonce, seed.GetValue(RegistryVisibilityCanary.NonceValue));
            Assert.Equal(3, seed.GetValueNames().Length);
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

        using RegistrySandbox sandbox = new RegistrySandbox();
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
            Assert.Equal(4, seed.GetValueNames().Length);
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

        using RegistrySandbox sandbox = new RegistrySandbox();
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

        using RegistrySandbox sandbox = new RegistrySandbox();
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

        using RegistrySandbox sandbox = new RegistrySandbox();
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

        using RegistrySandbox sandbox = new RegistrySandbox();
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
            Assert.Equal(3, seed.GetValueNames().Length);
        }

        result.Lease!.Dispose();
    }

    [Fact]
    public async Task Lease_displaces_a_competing_machine_registration_and_restores_it()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
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

        using RegistrySandbox sandbox = new RegistrySandbox();
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

        using RegistrySandbox sandbox = new RegistrySandbox();
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

        using RegistrySandbox sandbox = new RegistrySandbox();
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

        using RegistrySandbox sandbox = new RegistrySandbox();
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

        using RegistrySandbox sandbox = new RegistrySandbox();
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

        using RegistrySandbox sandbox = new RegistrySandbox();
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

        using RegistrySandbox sandbox = new RegistrySandbox();
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

        using RegistrySandbox sandbox = new RegistrySandbox();
        PluginNamespaceLeaseResult first = await sandbox.AcquireAsync(sandbox.PathFor("first/Sample.rhp"));
        Task<PluginNamespaceLeaseResult> secondTask = sandbox.AcquireAsync(
            sandbox.PathFor("second/Sample.rhp"));

        await Task.Delay(250);
        Assert.False(secondTask.IsCompleted);

        first.Lease!.Dispose();
        PluginNamespaceLeaseResult second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        second.Lease!.Dispose();
    }

    // The nonce is what tells this launch's seed from an identical one left in the real
    // hive by an earlier launch. It is removed once an independent reader has confirmed the
    // seed, so Rhino reads exactly the documented install shape.
    [Fact]
    public async Task The_visibility_nonce_is_removed_once_the_seed_is_confirmed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        PluginNamespaceLeaseResult result = await sandbox.AcquireAsync(
            sandbox.PathFor("selected/Sample.rhp"));

        PluginSeed seed = Assert.IsType<PluginSeed>(result.Seed);
        Assert.Equal(RegistryHives.CurrentUser, seed.Hive);
        Assert.Equal($@"{sandbox.UserPluginsKeyPath}\{sandbox.PluginId:D}", seed.KeyPath);
        Assert.NotEmpty(seed.Nonce);

        result.Lease!.ClearVisibilityNonce();

        using RegistryKey confirmed = sandbox.OpenUserRegistration()!;
        Assert.Null(confirmed.GetValue(RegistryVisibilityCanary.NonceValue));
        Assert.Equal(2, confirmed.GetValueNames().Length);
        result.Lease.Dispose();
    }

    // A launch that queues behind another must be able to say who it is waiting for, so the
    // holder records itself beside the lock and removes that record when it releases.
    [Fact]
    public async Task A_held_lock_names_its_holder_beside_it_and_stops_naming_one_when_released()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        PluginNamespaceLeaseResult held = await sandbox.AcquireAsync(sandbox.PathFor("first/Sample.rhp"));

        FileLockHolder holder = Assert.IsType<FileLockHolder>(
            FileLock.ReadHolder(FileLock.HolderPath(sandbox.LockPath)));
        Assert.Equal("test-launch", holder.LaunchId);
        Assert.Equal(Environment.ProcessId, holder.ProcessId);
        Assert.Equal("test", holder.HostKind);
        Assert.Contains(holder.LaunchId, holder.Describe(), StringComparison.Ordinal);

        held.Lease!.Dispose();

        Assert.Null(FileLock.ReadHolder(FileLock.HolderPath(sandbox.LockPath)));
    }

    [Fact]
    public async Task A_waiting_caller_is_told_which_launch_holds_the_lock()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        PluginNamespaceLeaseResult held = await sandbox.AcquireAsync(sandbox.PathFor("first/Sample.rhp"));
        List<FileLockWait> waits = new List<FileLockWait>();
        using CancellationTokenSource abandon = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        Task<PluginNamespaceLeaseResult> queued = sandbox.AcquireAsync(
            new PluginNamespaceLeaseRequest(
                sandbox.LocksDirectory,
                RegistrySandbox.RhinoVersion,
                sandbox.PluginId,
                "Sample",
                sandbox.PathFor("second/Sample.rhp"),
                RegistrySandbox.Holder("queued-launch"),
                Guid.NewGuid().ToString("N")),
            new ImmediateProgress<FileLockWait>(waits.Add),
            abandon.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        FileLockWait wait = waits[0];
        Assert.Equal("test-launch", wait.Holder!.LaunchId);
        Assert.Contains("test-launch", wait.HolderDescription, StringComparison.Ordinal);
        held.Lease!.Dispose();
    }

    // Rhino writes the artifact it loaded back into its registration, and it does that while
    // the launch that started it has already restored and returned. The correction runs
    // after Rhino exits and puts the pre-launch state back once more.
    [Fact]
    public async Task The_post_exit_correction_puts_back_a_machine_registration_rhino_rewrote()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        string selected = sandbox.PathFor("selected/Sample.rhp");
        using (RegistryKey installed = sandbox.CreateMachineRegistration())
        {
            using RegistryKey plugin = installed.CreateSubKey("PlugIn", writable: true)!;
            plugin.SetValue("FileName", @"C:\primary\Sample.rhp", RegistryValueKind.String);
        }

        PluginNamespaceLeaseResult result = await sandbox.AcquireAsync(selected);
        result.Lease!.RestoreRetainingJournal();

        // The journal survives the launch's own restore, because that restore ran while
        // Rhino was still able to write.
        Assert.True(File.Exists(sandbox.JournalPath));
        using (RegistryKey rewritten = sandbox.CreateMachineRegistration())
        {
            using RegistryKey plugin = rewritten.CreateSubKey("PlugIn", writable: true)!;
            plugin.SetValue("FileName", Path.GetFullPath(selected), RegistryValueKind.String);
        }

        RegistrationDrift drift = await sandbox.CorrectAfterExitAsync(
            new PluginNamespaceLeaseRequest(
                sandbox.LocksDirectory,
                RegistrySandbox.RhinoVersion,
                sandbox.PluginId,
                "Sample",
                selected,
                RegistrySandbox.Holder("post-exit"),
                Guid.NewGuid().ToString("N")),
            CancellationToken.None);

        Assert.True(drift.MachineDrifted);
        Assert.Equal(Path.GetFullPath(selected), drift.ObservedMachineRegistration);
        Assert.Equal(@"C:\primary\Sample.rhp", drift.ExpectedMachineRegistration);
        using RegistryKey corrected = sandbox.OpenMachineRegistration()!;
        using RegistryKey correctedPlugin = corrected.OpenSubKey("PlugIn")!;
        Assert.Equal(@"C:\primary\Sample.rhp", correctedPlugin.GetValue("FileName"));
        Assert.False(File.Exists(sandbox.JournalPath));
    }

    // The dangerous case the journal alone could not describe: no machine registration
    // existed, so a written-back one has to be removed rather than restored.
    [Fact]
    public async Task The_post_exit_correction_removes_a_machine_registration_rhino_created()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        string selected = sandbox.PathFor("selected/Sample.rhp");
        PluginNamespaceLeaseResult result = await sandbox.AcquireAsync(selected);
        result.Lease!.RestoreRetainingJournal();
        using (RegistryKey created = sandbox.CreateMachineRegistration())
        {
            using RegistryKey plugin = created.CreateSubKey("PlugIn", writable: true)!;
            plugin.SetValue("FileName", Path.GetFullPath(selected), RegistryValueKind.String);
        }

        RegistrationDrift drift = await sandbox.CorrectAfterExitAsync(
            new PluginNamespaceLeaseRequest(
                sandbox.LocksDirectory,
                RegistrySandbox.RhinoVersion,
                sandbox.PluginId,
                "Sample",
                selected,
                RegistrySandbox.Holder("post-exit"),
                Guid.NewGuid().ToString("N")),
            CancellationToken.None);

        Assert.True(drift.MachineDrifted);
        Assert.Null(drift.ExpectedMachineRegistration);
        Assert.Null(sandbox.OpenMachineRegistration());
        Assert.False(File.Exists(sandbox.JournalPath));
    }

    [Fact]
    public async Task The_post_exit_correction_leaves_an_unchanged_registration_alone()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        string selected = sandbox.PathFor("selected/Sample.rhp");
        using (RegistryKey installed = sandbox.CreateUserRegistration())
        {
            installed.SetValue("Name", "Installed", RegistryValueKind.String);
            using RegistryKey plugin = installed.CreateSubKey("PlugIn", writable: true)!;
            plugin.SetValue("FileName", @"C:\primary\Sample.rhp", RegistryValueKind.String);
        }
        PluginNamespaceLeaseResult result = await sandbox.AcquireAsync(selected);
        result.Lease!.RestoreRetainingJournal();

        RegistrationDrift drift = await sandbox.CorrectAfterExitAsync(
            new PluginNamespaceLeaseRequest(
                sandbox.LocksDirectory,
                RegistrySandbox.RhinoVersion,
                sandbox.PluginId,
                "Sample",
                selected,
                RegistrySandbox.Holder("post-exit"),
                Guid.NewGuid().ToString("N")),
            CancellationToken.None);

        Assert.True(drift.JournalFound);
        Assert.False(drift.Drifted);
        using RegistryKey untouched = sandbox.OpenUserRegistration()!;
        Assert.Equal("Installed", untouched.GetValue("Name"));
        Assert.False(File.Exists(sandbox.JournalPath));
    }

    // Another launch of the same plug-in restores and deletes a pending journal before it
    // reads anything, so a correction that arrives after it has nothing left to do.
    [Fact]
    public async Task The_post_exit_correction_is_a_no_op_once_another_launch_restored_the_journal()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();

        RegistrationDrift drift = await sandbox.CorrectAfterExitAsync(
            new PluginNamespaceLeaseRequest(
                sandbox.LocksDirectory,
                RegistrySandbox.RhinoVersion,
                sandbox.PluginId,
                "Sample",
                sandbox.PathFor("selected/Sample.rhp"),
                RegistrySandbox.Holder("post-exit"),
                Guid.NewGuid().ToString("N")),
            CancellationToken.None);

        Assert.False(drift.JournalFound);
        Assert.False(drift.Drifted);
    }

    // Switching the standing registration is not a lease: it displaces nothing, writes no
    // journal, and leaves the registration Rhino resolves naming the requested file.
    [Fact]
    public async Task Switching_rewrites_a_complete_machine_registration_in_place()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        using (RegistryKey installed = sandbox.CreateMachineRegistration())
        {
            installed.SetValue("Name", "Installed", RegistryValueKind.String);
            using RegistryKey plugin = installed.CreateSubKey("PlugIn", writable: true)!;
            plugin.SetValue("FileName", @"C:\primary\Sample.rhp", RegistryValueKind.String);
        }
        string selected = sandbox.PathFor("selected/Sample.rhp");

        RegistrationSwitchResult result = await sandbox.SwitchAsync(selected);

        Assert.Null(result.Refusal);
        Assert.False(result.JournalPending);
        Assert.Equal(@"C:\primary\Sample.rhp", result.PreviousPath);
        Assert.Equal(Path.GetFullPath(selected), result.NewPath);
        Assert.EndsWith(@"\PlugIn", result.KeyPath, StringComparison.Ordinal);
        using (RegistryKey machine = sandbox.OpenMachineRegistration()!)
        {
            // The registration is edited, never recreated, so everything Rhino recorded
            // beside the file name survives.
            Assert.Equal("Installed", machine.GetValue("Name"));
            using RegistryKey plugin = machine.OpenSubKey("PlugIn")!;
            Assert.Equal(Path.GetFullPath(selected), plugin.GetValue("FileName"));
        }
        Assert.Null(sandbox.OpenUserRegistration());
        Assert.False(File.Exists(sandbox.JournalPath));
        Assert.Null(FileLock.ReadHolder(FileLock.HolderPath(sandbox.LockPath)));
    }

    [Fact]
    public async Task Switching_rewrites_a_current_user_registration_when_no_machine_one_exists()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        using (RegistryKey installed = sandbox.CreateUserRegistration())
        {
            using RegistryKey plugin = installed.CreateSubKey("PlugIn", writable: true)!;
            plugin.SetValue("FileName", @"C:\primary\Sample.rhp", RegistryValueKind.String);
        }
        string selected = sandbox.PathFor("selected/Sample.rhp");

        RegistrationSwitchResult result = await sandbox.SwitchAsync(selected);

        Assert.Null(result.Refusal);
        Assert.Equal(@"C:\primary\Sample.rhp", result.PreviousPath);
        using RegistryKey user = sandbox.OpenUserRegistration()!;
        using RegistryKey rewritten = user.OpenSubKey("PlugIn")!;
        Assert.Equal(Path.GetFullPath(selected), rewritten.GetValue("FileName"));
        Assert.Null(sandbox.OpenMachineRegistration());
        Assert.False(File.Exists(sandbox.JournalPath));
    }

    [Fact]
    public async Task Switching_an_unregistered_plugin_writes_the_documented_install_seed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        string selected = sandbox.PathFor("selected/Sample.rhp");

        RegistrationSwitchResult result = await sandbox.SwitchAsync(selected);

        Assert.Null(result.PreviousPath);
        using RegistryKey seed = sandbox.OpenUserRegistration()!;
        Assert.Equal("Sample", seed.GetValue("Name"));
        Assert.Equal(Path.GetFullPath(selected), seed.GetValue("FileName"));
        // Nothing was displaced, so there is no recorded load mode to carry and no nonce:
        // exactly the shape Rhino installs from.
        Assert.Equal(2, seed.GetValueNames().Length);
        Assert.Empty(seed.GetSubKeyNames());
        Assert.False(File.Exists(sandbox.JournalPath));
    }

    // A pending journal belongs to a launch that has not finished restoring. Its post-exit
    // correction would put the old path back over this write, so the switch refuses instead
    // of restoring a journal it does not own.
    [Fact]
    public async Task Switching_refuses_while_a_launch_journal_is_pending()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        using (RegistryKey installed = sandbox.CreateMachineRegistration())
        {
            using RegistryKey plugin = installed.CreateSubKey("PlugIn", writable: true)!;
            plugin.SetValue("FileName", @"C:\primary\Sample.rhp", RegistryValueKind.String);
        }
        await File.WriteAllTextAsync(sandbox.JournalPath, "{}");

        RegistrationSwitchResult result = await sandbox.SwitchAsync(
            sandbox.PathFor("selected/Sample.rhp"));

        Assert.True(result.JournalPending);
        Assert.Null(result.Refusal);
        using RegistryKey machine = sandbox.OpenMachineRegistration()!;
        using RegistryKey untouched = machine.OpenSubKey("PlugIn")!;
        Assert.Equal(@"C:\primary\Sample.rhp", untouched.GetValue("FileName"));
        Assert.Null(sandbox.OpenUserRegistration());
        Assert.True(File.Exists(sandbox.JournalPath));
    }

    [Fact]
    public async Task Switching_rewrites_a_seed_form_registration_at_its_root()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        using (RegistryKey seeded = sandbox.CreateMachineRegistration())
            seeded.SetValue("FileName", @"C:\primary\Sample.rhp", RegistryValueKind.String);
        string selected = sandbox.PathFor("selected/Sample.rhp");

        RegistrationSwitchResult result = await sandbox.SwitchAsync(selected);

        Assert.Equal(@"C:\primary\Sample.rhp", result.PreviousPath);
        Assert.DoesNotContain(@"\PlugIn", result.KeyPath, StringComparison.Ordinal);
        using RegistryKey machine = sandbox.OpenMachineRegistration()!;
        Assert.Equal(Path.GetFullPath(selected), machine.GetValue("FileName"));
        Assert.Empty(machine.GetSubKeyNames());
        Assert.Single(machine.GetValueNames());
    }
}
