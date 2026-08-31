using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Security;
using System.Text.Json;
using Rwl.Protocol;

namespace RhinoWorktreeLauncher;

// One temporary displacement of everything registered for a single (Rhino version,
// plug-in ID) pair, across both registry hives, under one lock and one disk journal
// (ADR 0014).
//
// Current user: the key is cleared and reseeded with Rhino's documented install seed, the
// root values Name and FileName plus the load mode the displaced registration recorded.
// Rhino installs and loads that file at its next startup and fills in the rest of the
// registration itself; a hand-built complete registration is silently ignored (verified
// live on 2026-08-17). Every launch is a fresh install-load, so a previous occupant is
// displaced rather than edited in place. The key is cleared but not reseeded when a
// machine registration already names the selected artifact: Rhino loads the file from
// there, and a seed beside it claims an ID Rhino has already registered.
//
// Local machine: a registration for the same ID naming a different file wins over the
// current-user seed, so it is removed for the launch where the user granted write
// access to the machine Plug-ins key (ADR 0013), and refuses the launch otherwise.
// RWL never elevates.
//
// The journal holds both hives' pre-state and is written before any mutation, so a
// killed launch cannot leave a live install seed behind: the next launch of the same
// plug-in restores the journal first. It also outlives the launch's own restore, because
// a Rhino that loaded the artifact writes the path it loaded back into the registration
// after the launch has already returned (ADR 0015).
internal sealed class PluginNamespaceLease : IPluginNamespaceLease
{
    private const string LoadModeValue = "LoadMode";
    private const int DisabledLoadMode = 0;

    private readonly FileLockHandle _lock;
    private readonly RegistryKey _userHive;
    private readonly string _userPluginsKeyPath;
    private readonly RegistryKey _machineHive;
    private readonly string _machinePluginsKeyPath;
    private readonly string _journalPath;
    private readonly PluginNamespaceJournal _journal;
    private bool _released;

    private PluginNamespaceLease(
        FileLockHandle namespaceLock,
        RegistryKey userHive,
        string userPluginsKeyPath,
        RegistryKey machineHive,
        string machinePluginsKeyPath,
        string journalPath,
        PluginNamespaceJournal journal)
    {
        _lock = namespaceLock;
        _userHive = userHive;
        _userPluginsKeyPath = userPluginsKeyPath;
        _machineHive = machineHive;
        _machinePluginsKeyPath = machinePluginsKeyPath;
        _journalPath = journalPath;
        _journal = journal;
    }

    public static Task<PluginNamespaceLeaseResult> AcquireAsync(
        PluginNamespaceLeaseRequest request,
        IProgress<FileLockWait>? waiting,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Rhino plug-in namespace leases require Windows.");
        return AcquireAsync(
            Registry.CurrentUser,
            PluginsKeyPath(request.RhinoVersion),
            Registry.LocalMachine,
            PluginsKeyPath(request.RhinoVersion),
            JournalPath(request.LocksDirectory, request.RhinoVersion, request.PluginId),
            LockPath(request.LocksDirectory, request.RhinoVersion, request.PluginId),
            request.PluginId,
            request.PluginName,
            request.PluginPath,
            request.Holder,
            request.VisibilityNonce,
            waiting,
            cancellationToken);
    }

    // Rewrites the standing registration in place so an ordinary Rhino start loads the
    // requested artifact (ADR 0016). It takes the same lock the lease takes, because one
    // component owns everything registered for a (Rhino version, plug-in ID) pair, but it
    // displaces nothing and therefore writes no journal: there is nothing to restore.
    public static Task<RegistrationSwitchResult> SwitchAsync(
        PluginNamespaceLeaseRequest request,
        IProgress<FileLockWait>? waiting,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Rhino plug-in registrations require Windows.");
        return SwitchAsync(
            Registry.CurrentUser,
            PluginsKeyPath(request.RhinoVersion),
            Registry.LocalMachine,
            PluginsKeyPath(request.RhinoVersion),
            JournalPath(request.LocksDirectory, request.RhinoVersion, request.PluginId),
            LockPath(request.LocksDirectory, request.RhinoVersion, request.PluginId),
            request.PluginId,
            request.PluginName,
            request.PluginPath,
            request.Holder,
            waiting,
            cancellationToken);
    }

    // A pending journal means an earlier lease never restored, or restored while the Rhino
    // it started was still able to write a registration back. Restoring it is a correctness
    // requirement even for a launch that displaces nothing, so this runs on every launch
    // before any component reads a registration.
    public static Task RecoverAsync(
        string locksDirectory,
        int rhinoVersion,
        Guid pluginId,
        FileLockHolder holder,
        IProgress<FileLockWait>? waiting,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return Task.CompletedTask;
        return RecoverAsync(
            Registry.CurrentUser,
            PluginsKeyPath(rhinoVersion),
            Registry.LocalMachine,
            PluginsKeyPath(rhinoVersion),
            JournalPath(locksDirectory, rhinoVersion, pluginId),
            LockPath(locksDirectory, rhinoVersion, pluginId),
            holder,
            waiting,
            cancellationToken);
    }

    // The launch's own restore runs while the Rhino it started is still alive, and that
    // Rhino writes the artifact it loaded back into its registration. This runs after that
    // Rhino exits and puts the journaled pre-state back once more (ADR 0015).
    public static Task<RegistrationDrift> CorrectAfterExitAsync(
        string locksDirectory,
        int rhinoVersion,
        Guid pluginId,
        FileLockHolder holder,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Rhino plug-in namespace leases require Windows.");
        return CorrectAfterExitAsync(
            Registry.CurrentUser,
            PluginsKeyPath(rhinoVersion),
            Registry.LocalMachine,
            PluginsKeyPath(rhinoVersion),
            JournalPath(locksDirectory, rhinoVersion, pluginId),
            LockPath(locksDirectory, rhinoVersion, pluginId),
            holder,
            cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    internal static async Task<PluginNamespaceLeaseResult> AcquireAsync(
        RegistryKey userHive,
        string userPluginsKeyPath,
        RegistryKey machineHive,
        string machinePluginsKeyPath,
        string journalPath,
        string lockPath,
        Guid pluginId,
        string pluginName,
        string pluginPath,
        FileLockHolder holder,
        string visibilityNonce,
        IProgress<FileLockWait>? waiting,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        string registration = pluginId.ToString("D");
        string selectedPath = Path.GetFullPath(pluginPath);
        FileLockHandle namespaceLock = await FileLock.AcquireAsync(
            lockPath,
            holder,
            waiting,
            cancellationToken);
        try
        {
            RestorePendingJournal(
                userHive,
                userPluginsKeyPath,
                machineHive,
                machinePluginsKeyPath,
                journalPath);

            // The machine hive decides whether the launch can run at all, so it is
            // resolved before anything is written: a refusal must precede every mutation
            // and therefore reaches the caller before Rhino starts.
            string? competing = StandingRegistration.ReadRegisteredPath(machineHive, machinePluginsKeyPath, registration);
            bool machineNamesSelected = competing is not null && NamesSelectedArtifact(competing, selectedPath);
            bool machineCompetes = competing is not null && !machineNamesSelected;
            using RegistryKey? machinePluginsKey = machineCompetes
                ? OpenWritable(machineHive, machinePluginsKeyPath)
                : null;
            if (machineCompetes && machinePluginsKey is null)
            {
                namespaceLock.Dispose();
                return new PluginNamespaceLeaseResult(
                    Lease: null,
                    new PluginRegistrationConflict(
                        competing!,
                        $@"{machineHive.Name}\{machinePluginsKeyPath}\{registration}"),
                    DisplacedMachineRegistration: null,
                    DisplacedUserRegistration: null,
                    Seed: null);
            }

            using RegistryKey userPluginsKey = userHive.CreateSubKey(userPluginsKeyPath, writable: true) ??
                throw new UnauthorizedAccessException(
                    $@"The launcher cannot open '{userHive.Name}\{userPluginsKeyPath}'.");
            string? displacedUser = StandingRegistration.ReadRegisteredPath(userHive, userPluginsKeyPath, registration);
            RegistryKeySnapshot? userSnapshot = Capture(userPluginsKey, registration);
            // The machine pre-state is captured whether or not this launch displaces it,
            // because the post-exit correction compares against it. Only MachineDisplaced
            // decides whether the launch's own restore writes that hive.
            RegistryKeySnapshot? machineSnapshot = Capture(machineHive, machinePluginsKeyPath, registration);
            int? carriedLoadMode = CarriedLoadMode(machineSnapshot, userSnapshot);

            PluginNamespaceJournal journal = new PluginNamespaceJournal(
                registration,
                userSnapshot,
                machineSnapshot,
                machineCompetes);
            await File.WriteAllTextAsync(
                journalPath,
                JsonSerializer.Serialize(journal, JsonDefaults.Write),
                cancellationToken);
            PluginSeed? seed = null;
            try
            {
                if (machineCompetes)
                    machinePluginsKey!.DeleteSubKeyTree(registration, throwOnMissingSubKey: false);
                // Delete-then-seed: an install seed left beside an earlier occupant's
                // values reads as an already installed registration, which Rhino ignores.
                userPluginsKey.DeleteSubKeyTree(registration, throwOnMissingSubKey: false);
                // A machine registration that already names the selected artifact leaves
                // the launch nothing to write here. Rhino holds a complete registration for
                // exactly the file the launch wants loaded, and a seed beside it asks Rhino
                // to install an ID that is already registered, which it rejects. The
                // current-user key is still cleared above, because a registration naming
                // another file would otherwise win the ID.
                if (!machineNamesSelected)
                {
                    seed = new PluginSeed(
                        pluginName,
                        selectedPath,
                        carriedLoadMode,
                        HiveToken(userHive),
                        $@"{userPluginsKeyPath}\{registration}",
                        visibilityNonce);
                    using RegistryKey seedKey = userPluginsKey.CreateSubKey(registration, writable: true) ??
                        throw new UnauthorizedAccessException(
                            $@"The launcher cannot create '{userHive.Name}\{userPluginsKeyPath}\{registration}'.");
                    seedKey.SetValue("Name", seed.Name, RegistryValueKind.String);
                    seedKey.SetValue("FileName", seed.FileName, RegistryValueKind.String);
                    if (seed.LoadMode is int loadMode)
                        seedKey.SetValue(LoadModeValue, loadMode, RegistryValueKind.DWord);
                    // Written with the seed and removed once an independent reader has
                    // confirmed both, so Rhino never sees it. It is what tells this
                    // launch's seed apart from an identical one an earlier launch left
                    // behind, which a file name alone cannot do (ADR 0015).
                    seedKey.SetValue(RegistryVisibilityCanary.NonceValue, visibilityNonce, RegistryValueKind.String);
                }
            }
            catch
            {
                Restore(
                    userHive,
                    userPluginsKeyPath,
                    machineHive,
                    machinePluginsKeyPath,
                    journalPath,
                    journal,
                    deleteJournal: true);
                throw;
            }

            return new PluginNamespaceLeaseResult(
                new PluginNamespaceLease(
                    namespaceLock,
                    userHive,
                    userPluginsKeyPath,
                    machineHive,
                    machinePluginsKeyPath,
                    journalPath,
                    journal),
                Refusal: null,
                machineCompetes ? competing : null,
                displacedUser,
                seed);
        }
        catch
        {
            namespaceLock.Dispose();
            throw;
        }
    }

    // The target is decided once, the way Rhino resolves the ID: a machine registration wins,
    // otherwise the current-user one, otherwise a new current-user install seed. The write is
    // in place, into whichever value the existing registration already names its file with,
    // so no key is ever deleted or recreated and the other hive is never touched.
    [SupportedOSPlatform("windows")]
    internal static async Task<RegistrationSwitchResult> SwitchAsync(
        RegistryKey userHive,
        string userPluginsKeyPath,
        RegistryKey machineHive,
        string machinePluginsKeyPath,
        string journalPath,
        string lockPath,
        Guid pluginId,
        string pluginName,
        string pluginPath,
        FileLockHolder holder,
        IProgress<FileLockWait>? waiting,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        string registration = pluginId.ToString("D");
        string selectedPath = Path.GetFullPath(pluginPath);
        using FileLockHandle namespaceLock = await FileLock.AcquireAsync(
            lockPath,
            holder,
            waiting,
            cancellationToken);

        // A pending journal is not restored here. It may belong to an executor still
        // lingering behind a live Rhino, whose post-exit correction would put the old path
        // back over this write. The switch refuses and says how to end that launch instead.
        if (File.Exists(journalPath))
            return RegistrationSwitchResult.Pending;

        string? machine = StandingRegistration.ReadRegisteredPath(
            machineHive,
            machinePluginsKeyPath,
            registration);
        if (machine is not null)
        {
            using RegistryKey? machinePluginsKey = OpenWritable(machineHive, machinePluginsKeyPath);
            return machinePluginsKey is null
                ? RegistrationSwitchResult.Refused(new PluginRegistrationConflict(
                    machine,
                    $@"{machineHive.Name}\{machinePluginsKeyPath}\{registration}"))
                : Write(
                    machinePluginsKey,
                    HiveToken(machineHive),
                    machinePluginsKeyPath,
                    registration,
                    pluginName,
                    selectedPath,
                    machine);
        }

        string? user = StandingRegistration.ReadRegisteredPath(
            userHive,
            userPluginsKeyPath,
            registration);
        using RegistryKey userPluginsKey = userHive.CreateSubKey(userPluginsKeyPath, writable: true) ??
            throw new UnauthorizedAccessException(
                $@"The launcher cannot open '{userHive.Name}\{userPluginsKeyPath}'.");
        return Write(
            userPluginsKey,
            HiveToken(userHive),
            userPluginsKeyPath,
            registration,
            pluginName,
            selectedPath,
            user);
    }

    [SupportedOSPlatform("windows")]
    internal static async Task RecoverAsync(
        RegistryKey userHive,
        string userPluginsKeyPath,
        RegistryKey machineHive,
        string machinePluginsKeyPath,
        string journalPath,
        string lockPath,
        FileLockHolder holder,
        IProgress<FileLockWait>? waiting,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(journalPath))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        using FileLockHandle namespaceLock = await FileLock.AcquireAsync(
            lockPath,
            holder,
            waiting,
            cancellationToken);
        RestorePendingJournal(userHive, userPluginsKeyPath, machineHive, machinePluginsKeyPath, journalPath);
    }

    [SupportedOSPlatform("windows")]
    internal static async Task<RegistrationDrift> CorrectAfterExitAsync(
        RegistryKey userHive,
        string userPluginsKeyPath,
        RegistryKey machineHive,
        string machinePluginsKeyPath,
        string journalPath,
        string lockPath,
        FileLockHolder holder,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(journalPath))
            return RegistrationDrift.NoJournal;
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        using FileLockHandle namespaceLock = await FileLock.AcquireAsync(
            lockPath,
            holder,
            waiting: null,
            cancellationToken);
        // Another launch of the same plug-in restores and deletes a pending journal before
        // it reads anything, so the journal can be gone by the time this acquires the lock.
        // That launch already put the pre-state back; there is nothing left to correct.
        if (!File.Exists(journalPath))
            return RegistrationDrift.NoJournal;

        PluginNamespaceJournal journal = ReadJournal(journalPath);
        string? expectedUser = RegisteredPathOf(journal.User);
        string? expectedMachine = RegisteredPathOf(journal.Machine);
        string? observedUser = StandingRegistration.ReadRegisteredPath(userHive, userPluginsKeyPath, journal.Registration);
        string? observedMachine = StandingRegistration.ReadRegisteredPath(machineHive, machinePluginsKeyPath, journal.Registration);
        bool userDrifted = !SameRegisteredPath(observedUser, expectedUser);
        bool machineDrifted = !SameRegisteredPath(observedMachine, expectedMachine);

        if (userDrifted)
        {
            using RegistryKey userPluginsKey = userHive.CreateSubKey(userPluginsKeyPath, writable: true) ??
                throw new UnauthorizedAccessException(
                    $@"The launcher cannot open '{userHive.Name}\{userPluginsKeyPath}' to correct " +
                    "the registration Rhino wrote back.");
            userPluginsKey.DeleteSubKeyTree(journal.Registration, throwOnMissingSubKey: false);
            journal.User?.RestoreUnder(userPluginsKey);
        }
        if (machineDrifted)
        {
            using RegistryKey machinePluginsKey = OpenWritable(machineHive, machinePluginsKeyPath) ??
                throw new UnauthorizedAccessException(
                    $@"Rhino wrote '{observedMachine}' into '{machineHive.Name}\{machinePluginsKeyPath}\" +
                    $"{journal.Registration}' and this account cannot write that key to put " +
                    $"'{expectedMachine ?? "no registration"}' back. Grant write access with an " +
                    "elevated account, or correct the key manually.");
            machinePluginsKey.DeleteSubKeyTree(journal.Registration, throwOnMissingSubKey: false);
            journal.Machine?.RestoreUnder(machinePluginsKey);
        }

        File.Delete(journalPath);
        return new RegistrationDrift(
            JournalFound: true,
            userDrifted,
            machineDrifted,
            observedUser,
            observedMachine,
            expectedUser,
            expectedMachine);
    }

    // Ends the launch's hold on the namespace. The journal deliberately survives: the Rhino
    // this launch started is still running and still able to write its registration back,
    // and the post-exit correction owns deleting the journal once it cannot (ADR 0015).
    public void RestoreRetainingJournal() => Release(deleteJournal: false);

    public void ClearVisibilityNonce()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Rhino plug-in namespace leases require Windows.");
        using RegistryKey? seed = OpenWritable(
            _userHive,
            $@"{_userPluginsKeyPath}\{_journal.Registration}");
        seed?.DeleteValue(RegistryVisibilityCanary.NonceValue, throwOnMissingValue: false);
    }

    public void Dispose() => Release(deleteJournal: true);

    private void Release(bool deleteJournal)
    {
        if (_released)
            return;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Rhino plug-in namespace leases require Windows.");

        try
        {
            Restore(
                _userHive,
                _userPluginsKeyPath,
                _machineHive,
                _machinePluginsKeyPath,
                _journalPath,
                _journal,
                deleteJournal);
        }
        finally
        {
            _released = true;
            _lock.Dispose();
        }
    }

    // Restore is delete-then-recreate in both hives, so a partially written key can never
    // merge with stale remains, and a key the lease created is removed wholesale, which
    // also erases everything Rhino filled in while installing the seed. The current-user
    // hive goes first because its seed is the live instruction: left behind, it makes the
    // next ordinary Rhino session install the worktree artifact permanently. The machine
    // hive is written only where this launch displaced it, so a launch that never needed
    // write access there never requires it to restore.
    [SupportedOSPlatform("windows")]
    private static void Restore(
        RegistryKey userHive,
        string userPluginsKeyPath,
        RegistryKey machineHive,
        string machinePluginsKeyPath,
        string journalPath,
        PluginNamespaceJournal journal,
        bool deleteJournal)
    {
        using (RegistryKey? userPluginsKey = OpenWritable(userHive, userPluginsKeyPath))
        {
            if (userPluginsKey is not null)
            {
                userPluginsKey.DeleteSubKeyTree(journal.Registration, throwOnMissingSubKey: false);
                journal.User?.RestoreUnder(userPluginsKey);
            }
        }

        if (journal.DisplacedMachine)
        {
            using RegistryKey machinePluginsKey = OpenWritable(machineHive, machinePluginsKeyPath) ??
                throw new UnauthorizedAccessException(
                    $"A displaced machine registration journal exists at '{journalPath}' but this " +
                    $@"account can no longer write '{machineHive.Name}\{machinePluginsKeyPath}' to " +
                    "restore it. Re-grant write access or restore the registration manually.");
            machinePluginsKey.DeleteSubKeyTree(journal.Registration, throwOnMissingSubKey: false);
            journal.Machine?.RestoreUnder(machinePluginsKey);
        }

        if (deleteJournal)
            File.Delete(journalPath);
    }

    // An installed registration names its file under PlugIn and a seed names it at the root.
    // The switch writes into whichever of the two the registration already uses, so Rhino
    // reads the shape it wrote itself. A key that names nothing yet is completed into the
    // documented install seed, Name and FileName and nothing else: nothing was displaced, so
    // there is no recorded load mode to carry (ADR 0014).
    [SupportedOSPlatform("windows")]
    private static RegistrationSwitchResult Write(
        RegistryKey pluginsKey,
        string hive,
        string pluginsKeyPath,
        string registration,
        string pluginName,
        string selectedPath,
        string? previousPath)
    {
        using RegistryKey key = pluginsKey.CreateSubKey(registration, writable: true) ??
            throw new UnauthorizedAccessException(
                $@"The launcher cannot open '{pluginsKeyPath}\{registration}'.");
        using RegistryKey? installed = key.OpenSubKey("PlugIn", writable: true);
        if (installed?.GetValue("FileName") is not null)
        {
            installed.SetValue("FileName", selectedPath, RegistryValueKind.String);
            return new RegistrationSwitchResult(
                Refusal: null,
                JournalPending: false,
                hive,
                $@"{pluginsKeyPath}\{registration}\PlugIn",
                "FileName",
                previousPath,
                selectedPath);
        }

        if (previousPath is null)
            key.SetValue("Name", pluginName, RegistryValueKind.String);
        key.SetValue("FileName", selectedPath, RegistryValueKind.String);
        return new RegistrationSwitchResult(
            Refusal: null,
            JournalPending: false,
            hive,
            $@"{pluginsKeyPath}\{registration}",
            "FileName",
            previousPath,
            selectedPath);
    }

    [SupportedOSPlatform("windows")]
    private static void RestorePendingJournal(
        RegistryKey userHive,
        string userPluginsKeyPath,
        RegistryKey machineHive,
        string machinePluginsKeyPath,
        string journalPath)
    {
        if (!File.Exists(journalPath))
            return;
        Restore(
            userHive,
            userPluginsKeyPath,
            machineHive,
            machinePluginsKeyPath,
            journalPath,
            ReadJournal(journalPath),
            deleteJournal: true);
    }

    [SupportedOSPlatform("windows")]
    private static PluginNamespaceJournal ReadJournal(string journalPath) =>
        JsonSerializer.Deserialize<PluginNamespaceJournal>(
            File.ReadAllText(journalPath),
            JsonDefaults.Read) ??
        throw new InvalidDataException($"The registration journal '{journalPath}' is unreadable.");

    [SupportedOSPlatform("windows")]
    private static RegistryKeySnapshot? Capture(RegistryKey pluginsKey, string registration)
    {
        using RegistryKey? key = pluginsKey.OpenSubKey(registration, writable: false);
        return key is null ? null : RegistryKeySnapshot.Capture(key);
    }

    [SupportedOSPlatform("windows")]
    private static RegistryKeySnapshot? Capture(RegistryKey hive, string pluginsKeyPath, string registration)
    {
        using RegistryKey? pluginsKey = hive.OpenSubKey(pluginsKeyPath, writable: false);
        return pluginsKey is null ? null : Capture(pluginsKey, registration);
    }

    // Rhino derives a plug-in's load mode only by instantiating the plug-in, so a seed
    // holding only Name and FileName always installs as a demand load, and a plug-in
    // declaring PlugInLoadTime.AtStartup never loads at startup under a launch. The
    // displaced registration already holds Rhino's own answer for this ID, so the seed
    // carries it forward. The machine hive wins, because Rhino resolves a duplicate ID
    // there. A disabled mode is never carried: the launch exists to load the selected
    // artifact, and verification waits on that load.
    [SupportedOSPlatform("windows")]
    private static int? CarriedLoadMode(RegistryKeySnapshot? machine, RegistryKeySnapshot? user)
    {
        int? recorded = RootLoadMode(machine) ?? RootLoadMode(user);
        return recorded == DisabledLoadMode ? null : recorded;
    }

    [SupportedOSPlatform("windows")]
    private static int? RootLoadMode(RegistryKeySnapshot? registration)
    {
        RegistryValueSnapshot? recorded = registration?.Values.FirstOrDefault(value =>
            value.Kind == RegistryValueKind.DWord &&
            string.Equals(value.Name, LoadModeValue, StringComparison.OrdinalIgnoreCase));
        return recorded?.Number is long number ? unchecked((int)number) : null;
    }

    // The same reading rule applied to a captured pre-state, so the post-exit correction
    // compares like with like. Environment strings are captured unexpanded and read back
    // expanded, so the pre-state is expanded here too.
    [SupportedOSPlatform("windows")]
    private static string? RegisteredPathOf(RegistryKeySnapshot? registration)
    {
        if (registration is null)
            return null;
        string? installed = FileNameValue(registration.Subkeys.FirstOrDefault(subkey =>
            string.Equals(subkey.Name, "PlugIn", StringComparison.OrdinalIgnoreCase)));
        string? registered = installed ?? FileNameValue(registration);
        return string.IsNullOrWhiteSpace(registered)
            ? null
            : Environment.ExpandEnvironmentVariables(registered);
    }

    [SupportedOSPlatform("windows")]
    private static string? FileNameValue(RegistryKeySnapshot? key) => key?.Values
        .FirstOrDefault(value => string.Equals(value.Name, "FileName", StringComparison.OrdinalIgnoreCase))
        ?.Text;

    private static bool SameRegisteredPath(string? observed, string? expected)
    {
        if (observed is null || expected is null)
            return observed is null && expected is null;
        return NamesSelectedArtifact(observed, expected);
    }

    // A registration RWL cannot even parse is treated as competing rather than assumed
    // harmless.
    private static bool NamesSelectedArtifact(string registered, string selected)
    {
        try
        {
            return PathIdentity.AreEquivalent(registered, selected);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static RegistryKey? OpenWritable(RegistryKey hive, string keyPath)
    {
        try
        {
            return hive.OpenSubKey(keyPath, writable: true);
        }
        catch (SecurityException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string HiveToken(RegistryKey hive) =>
        hive.Name.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.Ordinal)
            ? RegistryHives.LocalMachine
            : RegistryHives.CurrentUser;

    // One definition of the Plug-ins key path and one of the registration reader, shared
    // with the standing-registration reader so the lease and the surfaces that display a
    // registration can never disagree about what is registered.
    private static string PluginsKeyPath(int rhinoVersion) =>
        StandingRegistration.PluginsKeyPath(rhinoVersion);

    private static string JournalPath(string locksDirectory, int rhinoVersion, Guid pluginId) =>
        Path.Combine(locksDirectory, $"rhino-{rhinoVersion}-{pluginId:D}.registration.json");

    private static string LockPath(string locksDirectory, int rhinoVersion, Guid pluginId) =>
        Path.Combine(locksDirectory, $"rhino-{rhinoVersion}-{pluginId:D}.registration.lock");
}

// Two ways to end a lease. Disposing ends the launch outright; retaining the journal ends
// this process's hold while leaving the recovery record in place, because the Rhino the
// launch started can still write its registration back (ADR 0015).
internal interface IPluginNamespaceLease : IDisposable
{
    void RestoreRetainingJournal();

    // Removes the visibility nonce once an independent reader has confirmed the seed, so
    // Rhino reads exactly the documented install seed and nothing else.
    void ClearVisibilityNonce();
}

internal sealed record PluginNamespaceLeaseRequest(
    string LocksDirectory,
    int RhinoVersion,
    Guid PluginId,
    string PluginName,
    string PluginPath,
    FileLockHolder Holder,
    string VisibilityNonce);

// A machine registration the launch cannot displace, described by the only two facts the
// refusal needs: the file it names and the key holding it.
internal sealed record PluginRegistrationConflict(string RegisteredPath, string RegistryKeyPath);

// Exactly what the lease wrote into the current-user hive, so the launch log records the
// instruction Rhino was given rather than a claim that one was written. Null where a
// machine registration already names the selected artifact and no seed is needed.
internal sealed record PluginSeed(
    string Name,
    string FileName,
    int? LoadMode,
    string Hive,
    string KeyPath,
    string Nonce);

// Exactly one of Lease and Refusal is set. The displaced paths are informational: they
// carry what the lease pushed aside so the launch log can name it.
internal sealed record PluginNamespaceLeaseResult(
    IPluginNamespaceLease? Lease,
    PluginRegistrationConflict? Refusal,
    string? DisplacedMachineRegistration,
    string? DisplacedUserRegistration,
    PluginSeed? Seed);

// One rewrite of the standing registration. Exactly one of Refusal and JournalPending can be
// set, and neither carries a write: Hive, KeyPath, ValueName and NewPath describe the value
// an independent reader must confirm, and PreviousPath is what the registration named before,
// null where nothing was registered.
internal sealed record RegistrationSwitchResult(
    PluginRegistrationConflict? Refusal,
    bool JournalPending,
    string Hive,
    string KeyPath,
    string ValueName,
    string? PreviousPath,
    string NewPath)
{
    public static RegistrationSwitchResult Pending { get; } = new RegistrationSwitchResult(
        null,
        true,
        string.Empty,
        string.Empty,
        string.Empty,
        null,
        string.Empty);

    public static RegistrationSwitchResult Refused(PluginRegistrationConflict refusal) =>
        new RegistrationSwitchResult(
            refusal,
            false,
            string.Empty,
            string.Empty,
            string.Empty,
            null,
            string.Empty);
}

// What the post-exit correction found. A registration that drifted is one Rhino rewrote
// after the launch restored, which is the state that would otherwise leave a worktree
// artifact registered for ordinary sessions.
internal sealed record RegistrationDrift(
    bool JournalFound,
    bool UserDrifted,
    bool MachineDrifted,
    string? ObservedUserRegistration,
    string? ObservedMachineRegistration,
    string? ExpectedUserRegistration,
    string? ExpectedMachineRegistration)
{
    public static RegistrationDrift NoJournal { get; } = new RegistrationDrift(
        false,
        false,
        false,
        null,
        null,
        null,
        null);

    public bool Drifted => UserDrifted || MachineDrifted;
}

// Both hives' pre-state for one registration, written before any mutation. A null User
// means the lease created the current-user key, so restoring deletes it: that is what
// removes a crashed launch's install seed. Machine is captured whether or not the launch
// displaced it, because the post-exit correction compares Rhino's write-back against it;
// MachineDisplaced is what decides whether the launch's own restore writes that hive.
[SupportedOSPlatform("windows")]
internal sealed record PluginNamespaceJournal(
    string Registration,
    RegistryKeySnapshot? User,
    RegistryKeySnapshot? Machine,
    bool? MachineDisplaced)
{
    // A journal written before the machine pre-state was captured unconditionally: there,
    // a captured machine snapshot always meant a displacement to restore.
    [SupportedOSPlatform("windows")]
    public bool DisplacedMachine => MachineDisplaced ?? Machine is not null;
}
