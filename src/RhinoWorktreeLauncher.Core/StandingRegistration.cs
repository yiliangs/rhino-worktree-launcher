using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Security;
using Rwl.Protocol;

namespace RhinoWorktreeLauncher;

// The standing registration: the registration Rhino resolves for one plug-in ID outside any
// launch, which is the build an ordinary Rhino start loads. It is a registry fact, not a Git
// one, so nothing derives it from the primary checkout.
//
// The machine hive wins over the current-user hive, because Rhino resolves a duplicate
// plug-in ID there (ADR 0012, verified live). A registration counts in either shape: an
// installed one names its file under PlugIn, an install seed names it at the root.
//
// Reading is allowed in a launcher host process. Only mutation is confined to the launch
// executor (ADR 0015), so the lease shares this reader rather than keeping its own.
internal static class StandingRegistration
{
    public static RegisteredPlugin? Read(int rhinoVersion, Guid pluginId)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        string keyPath = PluginsKeyPath(rhinoVersion);
        string registration = pluginId.ToString("D");
        return Read(Registry.LocalMachine, keyPath, registration, RegistryHives.LocalMachine) ??
            Read(Registry.CurrentUser, keyPath, registration, RegistryHives.CurrentUser);
    }

    public static string PluginsKeyPath(int rhinoVersion) =>
        $@"Software\McNeel\Rhinoceros\{rhinoVersion}.0\Plug-ins";

    // An installed registration names its file under PlugIn and an install seed names it at
    // the root. Both claim the plug-in ID, so both are registrations.
    [SupportedOSPlatform("windows")]
    public static string? ReadRegisteredPath(RegistryKey hive, string pluginsKeyPath, string registration)
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

    [SupportedOSPlatform("windows")]
    private static RegisteredPlugin? Read(
        RegistryKey hive,
        string pluginsKeyPath,
        string registration,
        string hiveToken)
    {
        string? registered = ReadRegisteredPath(hive, pluginsKeyPath, registration);
        return registered is null
            ? null
            : new RegisteredPlugin(registered, hiveToken, $@"{pluginsKeyPath}\{registration}");
    }
}
