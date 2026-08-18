using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Security;
using System.Text.Json;

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
// displaced rather than edited in place.
//
// Local machine: a registration for the same ID naming a different file wins over the
// current-user seed, so it is removed for the launch where the user granted write
// access to the machine Plug-ins key (ADR 0013), and refuses the launch otherwise.
// RWL never elevates.
//
// The journal holds both hives' pre-state and is written before any mutation, so a
// killed launch cannot leave a live install seed behind: the next launch of the same
// plug-in restores the journal first.
internal sealed class PluginNamespaceLease : IDisposable
{
    private const string LoadModeValue = "LoadMode";
    private const int DisabledLoadMode = 0;

    private readonly FileStream _lock;
    private readonly RegistryKey _userHive;
    private readonly string _userPluginsKeyPath;
    private readonly RegistryKey _machineHive;
    private readonly string _machinePluginsKeyPath;
    private readonly string _journalPath;
    private readonly PluginNamespaceJournal _journal;
    private bool _disposed;

    private PluginNamespaceLease(
        FileStream namespaceLock,
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
            cancellationToken);
    }

    // A pending journal means an earlier lease never restored. Restoring it is a
    // correctness requirement even for a launch that displaces nothing, so this runs on
    // every launch before any component reads a registration.
    public static Task RecoverAsync(
        string locksDirectory,
        int rhinoVersion,
        Guid pluginId,
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
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        string registration = pluginId.ToString("D");
        string selectedPath = Path.GetFullPath(pluginPath);
        FileStream namespaceLock = await FileLock.AcquireAsync(lockPath, cancellationToken);
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
            string? competing = ReadRegisteredPath(machineHive, machinePluginsKeyPath, registration);
            bool machineCompetes = competing is not null && !NamesSelectedArtifact(competing, selectedPath);
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
                    DisplacedUserRegistration: null);
            }

            using RegistryKey userPluginsKey = userHive.CreateSubKey(userPluginsKeyPath, writable: true) ??
                throw new UnauthorizedAccessException(
                    $@"The launcher cannot open '{userHive.Name}\{userPluginsKeyPath}'.");
            string? displacedUser = ReadRegisteredPath(userHive, userPluginsKeyPath, registration);
            RegistryKeySnapshot? userSnapshot = Capture(userPluginsKey, registration);
            RegistryKeySnapshot? machineSnapshot = machinePluginsKey is null
                ? null
                : Capture(machinePluginsKey, registration);
            int? carriedLoadMode = CarriedLoadMode(machineSnapshot, userSnapshot);

            PluginNamespaceJournal journal = new PluginNamespaceJournal(
                registration,
                userSnapshot,
                machineSnapshot);
            await File.WriteAllTextAsync(
                journalPath,
                JsonSerializer.Serialize(journal, JsonDefaults.Write),
                cancellationToken);
            try
            {
                if (machineSnapshot is not null)
                    machinePluginsKey!.DeleteSubKeyTree(registration, throwOnMissingSubKey: false);
                // Delete-then-seed: an install seed left beside an earlier occupant's
                // values reads as an already installed registration, which Rhino ignores.
                userPluginsKey.DeleteSubKeyTree(registration, throwOnMissingSubKey: false);
                using RegistryKey seed = userPluginsKey.CreateSubKey(registration, writable: true) ??
                    throw new UnauthorizedAccessException(
                        $@"The launcher cannot create '{userHive.Name}\{userPluginsKeyPath}\{registration}'.");
                seed.SetValue("Name", pluginName, RegistryValueKind.String);
                seed.SetValue("FileName", selectedPath, RegistryValueKind.String);
                if (carriedLoadMode is int loadMode)
                    seed.SetValue(LoadModeValue, loadMode, RegistryValueKind.DWord);
            }
            catch
            {
                Restore(
                    userHive,
                    userPluginsKeyPath,
                    machineHive,
                    machinePluginsKeyPath,
                    journalPath,
                    journal);
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
                machineSnapshot is null ? null : competing,
                displacedUser);
        }
        catch
        {
            namespaceLock.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static async Task RecoverAsync(
        RegistryKey userHive,
        string userPluginsKeyPath,
        RegistryKey machineHive,
        string machinePluginsKeyPath,
        string journalPath,
        string lockPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(journalPath))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        using FileStream namespaceLock = await FileLock.AcquireAsync(lockPath, cancellationToken);
        RestorePendingJournal(userHive, userPluginsKeyPath, machineHive, machinePluginsKeyPath, journalPath);
    }

    public void Dispose()
    {
        if (_disposed)
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
                _journal);
        }
        finally
        {
            _disposed = true;
            _lock.Dispose();
        }
    }

    // Restore is delete-then-recreate in both hives, so a partially written key can never
    // merge with stale remains, and a key the lease created is removed wholesale, which
    // also erases everything Rhino filled in while installing the seed. The current-user
    // hive goes first because its seed is the live instruction: left behind, it makes the
    // next ordinary Rhino session install the worktree artifact permanently.
    [SupportedOSPlatform("windows")]
    private static void Restore(
        RegistryKey userHive,
        string userPluginsKeyPath,
        RegistryKey machineHive,
        string machinePluginsKeyPath,
        string journalPath,
        PluginNamespaceJournal journal)
    {
        using (RegistryKey? userPluginsKey = OpenWritable(userHive, userPluginsKeyPath))
        {
            if (userPluginsKey is not null)
            {
                userPluginsKey.DeleteSubKeyTree(journal.Registration, throwOnMissingSubKey: false);
                journal.User?.RestoreUnder(userPluginsKey);
            }
        }

        if (journal.Machine is not null)
        {
            using RegistryKey machinePluginsKey = OpenWritable(machineHive, machinePluginsKeyPath) ??
                throw new UnauthorizedAccessException(
                    $"A displaced machine registration journal exists at '{journalPath}' but this " +
                    $@"account can no longer write '{machineHive.Name}\{machinePluginsKeyPath}' to " +
                    "restore it. Re-grant write access or restore the registration manually.");
            machinePluginsKey.DeleteSubKeyTree(journal.Registration, throwOnMissingSubKey: false);
            journal.Machine.RestoreUnder(machinePluginsKey);
        }

        File.Delete(journalPath);
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
        PluginNamespaceJournal journal = JsonSerializer.Deserialize<PluginNamespaceJournal>(
                File.ReadAllText(journalPath),
                JsonDefaults.Read) ??
            throw new InvalidDataException($"The registration journal '{journalPath}' is unreadable.");
        Restore(userHive, userPluginsKeyPath, machineHive, machinePluginsKeyPath, journalPath, journal);
    }

    [SupportedOSPlatform("windows")]
    private static RegistryKeySnapshot? Capture(RegistryKey pluginsKey, string registration)
    {
        using RegistryKey? key = pluginsKey.OpenSubKey(registration, writable: false);
        return key is null ? null : RegistryKeySnapshot.Capture(key);
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

    // An installed registration names its file under PlugIn and an install seed names it
    // at the root. Both claim the plug-in ID, so both are registrations that compete.
    [SupportedOSPlatform("windows")]
    private static string? ReadRegisteredPath(RegistryKey hive, string pluginsKeyPath, string registration)
    {
        try
        {
            using RegistryKey? key = hive.OpenSubKey($@"{pluginsKeyPath}\{registration}", writable: false);
            if (key is null)
                return null;
            using RegistryKey? installed = key.OpenSubKey("PlugIn", writable: false);
            string? registered = installed?.GetValue("FileName") as string ?? key.GetValue("FileName") as string;
            return string.IsNullOrWhiteSpace(registered) ? null : registered;
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

    private static string PluginsKeyPath(int rhinoVersion) =>
        $@"Software\McNeel\Rhinoceros\{rhinoVersion}.0\Plug-ins";

    private static string JournalPath(string locksDirectory, int rhinoVersion, Guid pluginId) =>
        Path.Combine(locksDirectory, $"rhino-{rhinoVersion}-{pluginId:D}.registration.json");

    private static string LockPath(string locksDirectory, int rhinoVersion, Guid pluginId) =>
        Path.Combine(locksDirectory, $"rhino-{rhinoVersion}-{pluginId:D}.registration.lock");
}

internal sealed record PluginNamespaceLeaseRequest(
    string LocksDirectory,
    int RhinoVersion,
    Guid PluginId,
    string PluginName,
    string PluginPath);

// A machine registration the launch cannot displace, described by the only two facts the
// refusal needs: the file it names and the key holding it.
internal sealed record PluginRegistrationConflict(string RegisteredPath, string RegistryKeyPath);

// Exactly one of Lease and Refusal is set. The displaced paths are informational: they
// carry what the lease pushed aside so the launch log can name it.
internal sealed record PluginNamespaceLeaseResult(
    IDisposable? Lease,
    PluginRegistrationConflict? Refusal,
    string? DisplacedMachineRegistration,
    string? DisplacedUserRegistration);

// Both hives' pre-state for one registration, written before any mutation. A null User
// means the lease created the current-user key, so restoring deletes it: that is what
// removes a crashed launch's install seed. A null Machine means the lease never touched
// the machine hive, which it does only to displace a competing registration.
[SupportedOSPlatform("windows")]
internal sealed record PluginNamespaceJournal(
    string Registration,
    RegistryKeySnapshot? User,
    RegistryKeySnapshot? Machine);
