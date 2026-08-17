using Microsoft.Win32;
using System.Runtime.Versioning;

namespace RhinoWorktreeLauncher;

// Rhino resolves a plug-in by ID across both registry hives, and a machine-wide
// registration wins over a current-user one for the same ID (verified live on
// 2026-08-17: with both present, Rhino 8 loaded the HKLM file, not the HKCU overlay).
// A machine registration naming a different file is therefore suspended for the
// launch where the user granted write access (ADR 0013), and otherwise refuses the
// launch with the exact key named. A current-user registration is overlaid and
// restored by the lease.
internal static class PluginRegistrationScan
{
    public static IReadOnlyList<PluginRegistrationConflict> FindConflicts(
        int rhinoVersion,
        Guid pluginId,
        string selectedPluginPath)
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<PluginRegistrationConflict>();

        string selected = Path.GetFullPath(selectedPluginPath);
        List<PluginRegistrationConflict> conflicts = new List<PluginRegistrationConflict>();
        foreach ((RegistryKey hive, string scope, string hiveName) in new[]
                 {
                     (Registry.LocalMachine, "machine", "HKEY_LOCAL_MACHINE"),
                     (Registry.CurrentUser, "user", "HKEY_CURRENT_USER")
                 })
        {
            string? registered = ReadRegisteredPath(hive, rhinoVersion, pluginId);
            if (string.IsNullOrWhiteSpace(registered))
                continue;
            if (NamesSelectedArtifact(registered, selected))
                continue;
            conflicts.Add(new PluginRegistrationConflict(
                scope,
                registered,
                $@"{hiveName}\{RegistrationRootPath(rhinoVersion, pluginId)}"));
        }
        return conflicts;
    }

    // A registration RWL cannot even parse is reported rather than assumed harmless.
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

    private static string RegistrationRootPath(int rhinoVersion, Guid pluginId) =>
        $@"Software\McNeel\Rhinoceros\{rhinoVersion}.0\Plug-ins\{pluginId:D}";

    [SupportedOSPlatform("windows")]
    private static string? ReadRegisteredPath(RegistryKey hive, int rhinoVersion, Guid pluginId)
    {
        string keyPath = RegistrationRootPath(rhinoVersion, pluginId) + @"\PlugIn";
        try
        {
            using RegistryKey? key = hive.OpenSubKey(keyPath, writable: false);
            return key?.GetValue("FileName") as string;
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
}
