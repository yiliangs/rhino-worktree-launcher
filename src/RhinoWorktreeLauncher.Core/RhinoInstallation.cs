using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Security;

namespace RhinoWorktreeLauncher;

/// <summary>
/// How RWL knows where Rhino is. A diagnostic that names a path nobody can launch has to say
/// which of the two answers it is showing, because the remedies differ: a wrong recorded
/// location means the installation moved, and a wrong default location means Rhino is
/// installed somewhere the installer did not record.
/// </summary>
public enum RhinoExecutableSource
{
    /// <summary>The location the Rhino installer recorded for this major version.</summary>
    InstallerRecord,

    /// <summary>The conventional Program Files location, used when no record answered.</summary>
    DefaultLocation
}

/// <summary>
/// Where the Rhino executable for one major version is, and how RWL knows. The Rhino
/// installer records its own location per major version, so a Rhino installed outside
/// Program Files is found rather than guessed at, and every version RWL supports is
/// discovered by the same read.
/// </summary>
/// <remarks>
/// Reading the installer's registration in a launcher host process is allowed. Only registry
/// mutation belongs to the launch executor (ADR 0015).
/// </remarks>
public sealed record RhinoInstallation(string ExecutablePath, RhinoExecutableSource Source)
{
    public string SourceDescription => Source == RhinoExecutableSource.InstallerRecord
        ? "the location the Rhino installer recorded"
        : "the default installation location";

    /// <summary>
    /// The one phrasing of a Rhino that is not where RWL expected it, so the doctor check
    /// and worktree inspection cannot drift into two accounts of the same fact.
    /// </summary>
    public string DescribeMissing() => $"Rhino was not found at '{ExecutablePath}', {SourceDescription}.";

    public string DescribeFound() => $"{ExecutablePath}, {SourceDescription}.";

    public static RhinoInstallation FromInstallerRecord(string executablePath) =>
        new RhinoInstallation(executablePath, RhinoExecutableSource.InstallerRecord);

    public static RhinoInstallation AtDefaultLocation(string executablePath) =>
        new RhinoInstallation(executablePath, RhinoExecutableSource.DefaultLocation);

    public static RhinoInstallation Resolve(int rhinoVersion) => Resolve(
        rhinoVersion,
        RhinoInstallerRecord.Read,
        DefaultExecutablePath(rhinoVersion));

    public static string DefaultExecutablePath(int rhinoVersion) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        $"Rhino {rhinoVersion}",
        "System",
        "Rhino.exe");

    // The installer's record wins whenever the file it names is there, because an
    // installation outside Program Files is discoverable no other way. The default location
    // answers next, which covers a machine whose record was never written or was written for
    // a different edition. When neither file exists there is nothing to launch, so the answer
    // is whichever path a diagnostic should put in front of the reader: the one the installer
    // recorded, if it recorded one, and the default location otherwise.
    internal static RhinoInstallation Resolve(
        int rhinoVersion,
        Func<int, RhinoInstallerRecord?> installerRecordReader,
        string defaultExecutablePath)
    {
        string? recorded = installerRecordReader(rhinoVersion)?.ExecutablePath;
        if (recorded is not null && File.Exists(recorded))
            return FromInstallerRecord(recorded);
        if (File.Exists(defaultExecutablePath))
            return AtDefaultLocation(defaultExecutablePath);
        return recorded is null
            ? AtDefaultLocation(defaultExecutablePath)
            : FromInstallerRecord(recorded);
    }
}

/// <summary>
/// What the Rhino installer recorded for one major version, read once so the fallback among
/// its value names costs one key open rather than one per candidate. Rhino 7 and Rhino 8
/// write all three; the same key shape is assumed for Rhino 9.
/// </summary>
internal sealed record RhinoInstallerRecord(string? ExePath, string? SystemDirectory, string? InstallDirectory)
{
    /// <summary>
    /// <c>ExePath</c> names the executable outright, <c>Path</c> names the System directory
    /// that holds it, and <c>InstallPath</c> names the installation root above that
    /// directory. Null means the installer recorded no usable location at all.
    /// </summary>
    public string? ExecutablePath =>
        ExePath ??
        (SystemDirectory is null ? null : Path.Combine(SystemDirectory, "Rhino.exe")) ??
        (InstallDirectory is null ? null : Path.Combine(InstallDirectory, "System", "Rhino.exe"));

    public static RhinoInstallerRecord? Read(int rhinoVersion) =>
        OperatingSystem.IsWindows() ? ReadMachineKey(rhinoVersion) : null;

    // The 64-bit view explicitly, so a host that ever runs 32-bit reads the same key the
    // 64-bit Rhino installer wrote rather than the WOW6432Node redirection of it.
    [SupportedOSPlatform("windows")]
    private static RhinoInstallerRecord? ReadMachineKey(int rhinoVersion)
    {
        try
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using RegistryKey? install = machine.OpenSubKey(
                $@"SOFTWARE\McNeel\Rhinoceros\{rhinoVersion}.0\Install",
                writable: false);
            if (install is null)
                return null;
            return new RhinoInstallerRecord(
                Text(install, "ExePath"),
                Text(install, "Path"),
                Text(install, "InstallPath"));
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
    private static string? Text(RegistryKey key, string valueName)
    {
        string? value = key.GetValue(valueName) as string;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
