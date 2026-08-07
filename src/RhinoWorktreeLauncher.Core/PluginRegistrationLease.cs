using Microsoft.Win32;
using System.Runtime.Versioning;

namespace RhinoWorktreeLauncher;

internal sealed class PluginRegistrationLease : IDisposable
{
    public const string Mode = "windows-registry-lease";

    private readonly FileStream _lock;
    private readonly IReadOnlyList<Registration> _registrations;
    private bool _disposed;

    private PluginRegistrationLease(FileStream registrationLock, IReadOnlyList<Registration> registrations)
    {
        _lock = registrationLock;
        _registrations = registrations;
    }

    public static async Task<PluginRegistrationLease> AcquireAsync(
        string locksDirectory,
        int rhinoVersion,
        string pluginId,
        string pluginPath,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Rhino registry launch leases require Windows.");
        if (!Guid.TryParse(pluginId, out Guid parsedPluginId))
            throw new InvalidDataException($"Driver plug-in ID '{pluginId}' is not a valid GUID.");

        Directory.CreateDirectory(locksDirectory);
        string normalizedPluginId = parsedPluginId.ToString("D");
        string lockPath = Path.Combine(
            locksDirectory,
            $"rhino-{rhinoVersion}-{normalizedPluginId}.registration.lock");
        FileStream registrationLock = await AcquireFileLockAsync(lockPath, cancellationToken);
        List<Registration> registrations = new List<Registration>();

        try
        {
            string pluginKeyPath =
                $@"Software\McNeel\Rhinoceros\{rhinoVersion}.0\Plug-ins\{normalizedPluginId}\PlugIn";
            AddRegistrationIfPresent(Registry.LocalMachine, "HKLM", pluginKeyPath, registrations);
            AddRegistrationIfPresent(Registry.CurrentUser, "HKCU", pluginKeyPath, registrations);
            if (registrations.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No Rhino {rhinoVersion} registration exists for plug-in {normalizedPluginId}.");
            }

            string selectedPath = Path.GetFullPath(pluginPath);
            foreach (Registration registration in registrations)
                registration.Key.SetValue("FileName", selectedPath, RegistryValueKind.String);
            return new PluginRegistrationLease(registrationLock, registrations);
        }
        catch
        {
            try
            {
                RestoreAndDispose(registrations);
            }
            finally
            {
                registrationLock.Dispose();
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Rhino registry launch leases require Windows.");

        try
        {
            RestoreAndDispose(_registrations);
        }
        finally
        {
            _disposed = true;
            _lock.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AddRegistrationIfPresent(
        RegistryKey root,
        string rootName,
        string keyPath,
        ICollection<Registration> registrations)
    {
        using RegistryKey? existing = root.OpenSubKey(keyPath, writable: false);
        if (existing is null)
            return;

        RegistryKey writable = root.OpenSubKey(keyPath, writable: true) ??
            throw new UnauthorizedAccessException($"The launcher cannot update {rootName}\\{keyPath}.");
        bool hadFileName = writable.GetValueNames().Contains("FileName", StringComparer.OrdinalIgnoreCase);
        object? fileName = hadFileName
            ? writable.GetValue("FileName", null, RegistryValueOptions.DoNotExpandEnvironmentNames)
            : null;
        registrations.Add(new Registration(writable, hadFileName, fileName));
    }

    [SupportedOSPlatform("windows")]
    private static void RestoreAndDispose(IEnumerable<Registration> registrations)
    {
        Exception? restoreFailure = null;
        foreach (Registration registration in registrations)
        {
            try
            {
                if (registration.HadFileName)
                    registration.Key.SetValue("FileName", registration.FileName ?? string.Empty, RegistryValueKind.String);
                else
                    registration.Key.DeleteValue("FileName", throwOnMissingValue: false);
            }
            catch (Exception exception)
            {
                restoreFailure ??= exception;
            }
            finally
            {
                registration.Key.Dispose();
            }
        }

        if (restoreFailure is not null)
            throw new InvalidOperationException("Rhino plug-in registration restoration failed.", restoreFailure);
    }

    private static async Task<FileStream> AcquireFileLockAsync(
        string path,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private sealed record Registration(RegistryKey Key, bool HadFileName, object? FileName);
}
