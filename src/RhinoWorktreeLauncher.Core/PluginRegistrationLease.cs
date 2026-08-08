using Microsoft.Win32;
using System.Runtime.Versioning;

namespace RhinoWorktreeLauncher;

internal sealed class PluginRegistrationLease : IDisposable
{
    public const string Mode = "windows-registry-lease";

    private readonly FileStream _lock;
    private readonly Registration _registration;
    private bool _disposed;

    private PluginRegistrationLease(FileStream registrationLock, Registration registration)
    {
        _lock = registrationLock;
        _registration = registration;
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
        try
        {
            string pluginRootPath =
                $@"Software\McNeel\Rhinoceros\{rhinoVersion}.0\Plug-ins\{normalizedPluginId}";
            string pluginKeyPath = pluginRootPath + @"\PlugIn";
            bool rootExisted;
            bool pluginKeyExisted;
            using (RegistryKey? root = Registry.CurrentUser.OpenSubKey(pluginRootPath, writable: false))
                rootExisted = root is not null;
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(pluginKeyPath, writable: false))
                pluginKeyExisted = key is not null;
            RegistryKey writable = Registry.CurrentUser.CreateSubKey(pluginKeyPath, writable: true) ??
                throw new UnauthorizedAccessException($"The launcher cannot create HKCU\\{pluginKeyPath}.");
            bool hadFileName = writable.GetValueNames().Contains("FileName", StringComparer.OrdinalIgnoreCase);
            object? fileName = hadFileName
                ? writable.GetValue("FileName", null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                : null;
            RegistryValueKind? fileNameKind = hadFileName ? writable.GetValueKind("FileName") : null;
            string selectedPath = Path.GetFullPath(pluginPath);
            writable.SetValue("FileName", selectedPath, RegistryValueKind.String);
            writable.Dispose();
            return new PluginRegistrationLease(
                registrationLock,
                new Registration(
                    pluginRootPath,
                    pluginKeyPath,
                    rootExisted,
                    pluginKeyExisted,
                    hadFileName,
                    fileName,
                    fileNameKind));
        }
        catch
        {
            registrationLock.Dispose();
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
            Restore(_registration);
        }
        finally
        {
            _disposed = true;
            _lock.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Restore(Registration registration)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Rhino registry launch leases require Windows.");

        if (!registration.RootExisted)
        {
            Registry.CurrentUser.DeleteSubKeyTree(registration.RootPath, throwOnMissingSubKey: false);
            return;
        }
        if (!registration.PluginKeyExisted)
        {
            Registry.CurrentUser.DeleteSubKeyTree(registration.PluginKeyPath, throwOnMissingSubKey: false);
            return;
        }

        using RegistryKey key = Registry.CurrentUser.OpenSubKey(registration.PluginKeyPath, writable: true) ??
            throw new InvalidOperationException("The temporary current-user plug-in registration disappeared before restoration.");
        if (registration.HadFileName)
        {
            key.SetValue(
                "FileName",
                registration.FileName ?? string.Empty,
                registration.FileNameKind ?? RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue("FileName", throwOnMissingValue: false);
        }
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

    private sealed record Registration(
        string RootPath,
        string PluginKeyPath,
        bool RootExisted,
        bool PluginKeyExisted,
        bool HadFileName,
        object? FileName,
        RegistryValueKind? FileNameKind);
}
