using Microsoft.Win32;
using System.Runtime.Versioning;

namespace RhinoWorktreeLauncher;

// Rhino loads a registered plug-in from the path named by its registration. A key
// holding only a file name is registered but not loadable, and Rhino then rejects the
// same plug-in offered on its command line as an ID already in use. The lease
// therefore writes the whole registration Rhino needs to load the selected artifact at
// startup, and restores every value it touched when the launch ends.
internal sealed class PluginRegistrationLease : IDisposable
{
    public const string Mode = "windows-registry-lease";
    private const int LoadAtStartup = 1;

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
        string pluginName,
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

            string selectedPath = Path.GetFullPath(pluginPath);
            List<CapturedValue> captured = new List<CapturedValue>();
            using (RegistryKey root = Registry.CurrentUser.CreateSubKey(pluginRootPath, writable: true) ??
                throw new UnauthorizedAccessException($"The launcher cannot create HKCU\\{pluginRootPath}."))
            {
                Write(root, pluginRootPath, "Name", pluginName, RegistryValueKind.String, captured);
                Write(root, pluginRootPath, "EnglishName", pluginName, RegistryValueKind.String, captured);
                Write(root, pluginRootPath, "LoadMode", LoadAtStartup, RegistryValueKind.DWord, captured);
                Write(root, pluginRootPath, "IsDotNETPlugIn", 1, RegistryValueKind.DWord, captured);
            }
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(pluginKeyPath, writable: true) ??
                throw new UnauthorizedAccessException($"The launcher cannot create HKCU\\{pluginKeyPath}."))
            {
                Write(key, pluginKeyPath, "FileName", selectedPath, RegistryValueKind.String, captured);
            }

            return new PluginRegistrationLease(
                registrationLock,
                new Registration(pluginRootPath, pluginKeyPath, rootExisted, pluginKeyExisted, captured));
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
    private static void Write(
        RegistryKey key,
        string keyPath,
        string valueName,
        object value,
        RegistryValueKind kind,
        List<CapturedValue> captured)
    {
        bool existed = key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase);
        captured.Add(new CapturedValue(
            keyPath,
            valueName,
            existed,
            existed ? key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null,
            existed ? key.GetValueKind(valueName) : null));
        key.SetValue(valueName, value, kind);
    }

    // Rhino writes its own values into a loaded plug-in's key, so a key RWL created is
    // removed wholesale and a key that already existed keeps everything RWL did not write.
    [SupportedOSPlatform("windows")]
    private static void Restore(Registration registration)
    {
        if (!registration.RootExisted)
        {
            Registry.CurrentUser.DeleteSubKeyTree(registration.RootPath, throwOnMissingSubKey: false);
            return;
        }

        if (!registration.PluginKeyExisted)
            Registry.CurrentUser.DeleteSubKeyTree(registration.PluginKeyPath, throwOnMissingSubKey: false);

        foreach (IGrouping<string, CapturedValue> group in registration.Captured
                     .Where(value => registration.PluginKeyExisted || value.KeyPath != registration.PluginKeyPath)
                     .GroupBy(value => value.KeyPath))
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(group.Key, writable: true);
            if (key is null)
                continue;
            foreach (CapturedValue value in group)
            {
                if (value.Existed)
                    key.SetValue(value.Name, value.Value ?? string.Empty, value.Kind ?? RegistryValueKind.String);
                else
                    key.DeleteValue(value.Name, throwOnMissingValue: false);
            }
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

    private sealed record CapturedValue(
        string KeyPath,
        string Name,
        bool Existed,
        object? Value,
        RegistryValueKind? Kind);

    private sealed record Registration(
        string RootPath,
        string PluginKeyPath,
        bool RootExisted,
        bool PluginKeyExisted,
        IReadOnlyList<CapturedValue> Captured);
}
