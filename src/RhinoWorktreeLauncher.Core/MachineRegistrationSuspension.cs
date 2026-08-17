using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Text.Json;

namespace RhinoWorktreeLauncher;

// A machine-wide registration for the selected plug-in ID beats the launch's
// current-user registration, and RWL never elevates (ADR 0013). Where the user has
// granted this account write access to the machine Plug-ins key, the launch suspends
// the competing registration instead: the key tree is journaled to disk, removed, and
// restored when the launch ends. The journal survives a crash and is restored by the
// next launch of the same plug-in.
internal sealed class MachineRegistrationSuspension : IDisposable
{
    private readonly FileStream _lock;
    private readonly RegistryKey _hive;
    private readonly string _pluginsKeyPath;
    private readonly string _journalPath;
    private readonly RegistryKeySnapshot? _snapshot;
    private bool _disposed;

    private MachineRegistrationSuspension(
        FileStream suspensionLock,
        RegistryKey hive,
        string pluginsKeyPath,
        string journalPath,
        RegistryKeySnapshot? snapshot)
    {
        _lock = suspensionLock;
        _hive = hive;
        _pluginsKeyPath = pluginsKeyPath;
        _journalPath = journalPath;
        _snapshot = snapshot;
    }

    public static Task<IDisposable?> TryAcquireAsync(
        string locksDirectory,
        int rhinoVersion,
        Guid pluginId,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Machine registration suspension requires Windows.");
        return TryAcquireAsync(
            Registry.LocalMachine,
            PluginsKeyPath(rhinoVersion),
            JournalPath(locksDirectory, rhinoVersion, pluginId),
            LockPath(locksDirectory, rhinoVersion, pluginId),
            pluginId,
            cancellationToken);
    }

    // A pending journal means an earlier suspension never restored. Restoring it is a
    // correctness requirement even when no launch will suspend, so this runs on every
    // launch before the registration scan reads the machine hive.
    public static Task RecoverAsync(
        string locksDirectory,
        int rhinoVersion,
        Guid pluginId,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return Task.CompletedTask;
        return RecoverAsync(
            Registry.LocalMachine,
            PluginsKeyPath(rhinoVersion),
            JournalPath(locksDirectory, rhinoVersion, pluginId),
            LockPath(locksDirectory, rhinoVersion, pluginId),
            cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    internal static async Task<IDisposable?> TryAcquireAsync(
        RegistryKey hive,
        string pluginsKeyPath,
        string journalPath,
        string lockPath,
        Guid pluginId,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        FileStream suspensionLock = await FileLock.AcquireAsync(lockPath, cancellationToken);
        try
        {
            using RegistryKey? pluginsKey = OpenWritable(hive, pluginsKeyPath);
            if (pluginsKey is null)
            {
                suspensionLock.Dispose();
                return null;
            }

            RestorePendingJournal(pluginsKey, journalPath);
            string registrationName = pluginId.ToString("D");
            RegistryKeySnapshot? snapshot = null;
            using (RegistryKey? registration = pluginsKey.OpenSubKey(registrationName, writable: false))
            {
                if (registration is not null)
                    snapshot = RegistryKeySnapshot.Capture(registration);
            }
            if (snapshot is not null)
            {
                await File.WriteAllTextAsync(
                    journalPath,
                    JsonSerializer.Serialize(snapshot, JsonDefaults.Write),
                    cancellationToken);
                try
                {
                    pluginsKey.DeleteSubKeyTree(registrationName, throwOnMissingSubKey: false);
                }
                catch
                {
                    // A partial delete must not outlive this attempt; the journal is the
                    // authoritative pre-suspension state.
                    pluginsKey.DeleteSubKeyTree(registrationName, throwOnMissingSubKey: false);
                    snapshot.RestoreUnder(pluginsKey);
                    File.Delete(journalPath);
                    suspensionLock.Dispose();
                    return null;
                }
            }
            return new MachineRegistrationSuspension(
                suspensionLock,
                hive,
                pluginsKeyPath,
                journalPath,
                snapshot);
        }
        catch
        {
            suspensionLock.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static async Task RecoverAsync(
        RegistryKey hive,
        string pluginsKeyPath,
        string journalPath,
        string lockPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(journalPath))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        using FileStream suspensionLock = await FileLock.AcquireAsync(lockPath, cancellationToken);
        if (!File.Exists(journalPath))
            return;
        using RegistryKey? pluginsKey = OpenWritable(hive, pluginsKeyPath);
        if (pluginsKey is null)
            throw new UnauthorizedAccessException(
                $"A suspended machine registration journal exists at '{journalPath}' but this " +
                "account can no longer write the machine Plug-ins key to restore it. " +
                "Re-grant write access or restore the registration manually.");
        RestorePendingJournal(pluginsKey, journalPath);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Machine registration suspension requires Windows.");

        try
        {
            if (_snapshot is not null)
            {
                using RegistryKey pluginsKey = OpenWritable(_hive, _pluginsKeyPath) ??
                    throw new UnauthorizedAccessException(
                        $"Cannot reopen '{_pluginsKeyPath}' to restore the suspended registration; " +
                        $"the journal remains at '{_journalPath}' and is restored by the next launch.");
                // Restore is delete-then-recreate so a partially restored key can never
                // merge with stale remains.
                pluginsKey.DeleteSubKeyTree(_snapshot.Name, throwOnMissingSubKey: false);
                _snapshot.RestoreUnder(pluginsKey);
                File.Delete(_journalPath);
            }
        }
        finally
        {
            _disposed = true;
            _lock.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestorePendingJournal(RegistryKey pluginsKey, string journalPath)
    {
        if (!File.Exists(journalPath))
            return;
        RegistryKeySnapshot snapshot = JsonSerializer.Deserialize<RegistryKeySnapshot>(
                File.ReadAllText(journalPath),
                JsonDefaults.Read) ??
            throw new InvalidDataException($"The suspension journal '{journalPath}' is unreadable.");
        // A crash can leave the journal beside a partially deleted key; the journal is
        // the authoritative pre-suspension state.
        pluginsKey.DeleteSubKeyTree(snapshot.Name, throwOnMissingSubKey: false);
        snapshot.RestoreUnder(pluginsKey);
        File.Delete(journalPath);
    }

    [SupportedOSPlatform("windows")]
    private static RegistryKey? OpenWritable(RegistryKey hive, string keyPath)
    {
        try
        {
            return hive.OpenSubKey(keyPath, writable: true);
        }
        catch (System.Security.SecurityException)
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
        Path.Combine(locksDirectory, $"rhino-{rhinoVersion}-{pluginId:D}.machine-registration.json");

    private static string LockPath(string locksDirectory, int rhinoVersion, Guid pluginId) =>
        Path.Combine(locksDirectory, $"rhino-{rhinoVersion}-{pluginId:D}.machine-registration.lock");
}
