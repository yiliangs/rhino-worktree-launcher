using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Rwl.Bootstrap;

/// <summary>
/// What an install request carries. Every path is explicit rather than read from the
/// environment here, so the same code runs under test against scratch directories and no
/// test-only switch exists on the real thing.
/// </summary>
internal sealed record InstallRequest(
    string PackageRoot,
    string DataRoot,
    string StartMenuDirectory,
    string ReleaseId)
{
    public bool CreateShortcut { get; init; } = true;
}

internal sealed record InstallResult(
    string ReleaseDirectory,
    string StableBootstrapPath,
    string PointerPath,
    string? ShortcutPath);

/// <summary>
/// Installs a produced payload. This lives in the bootstrap because the bootstrap already
/// owns <c>current.json</c>: it resolves that pointer on every forward, so writing it
/// belongs to the same component rather than to a script that has to be told where it is.
///
/// Nothing here shells out. The failures this replaces were never about installation, they
/// were about which PowerShell existed and whether it was permitted to run a file.
/// </summary>
internal static class Installer
{
    // The payload shape New-RwlPackage.ps1 produces. A missing one is a broken package,
    // not a broken machine, so it is named rather than discovered late.
    private static readonly (string Component, string Executable)[] RequiredPayload =
    {
        ("desktop", "RhinoWorktreeLauncher.exe"),
        ("cli", "rwl-cli.exe"),
        ("mcp", "rwl-mcp.exe"),
        ("bootstrap", "rwl.exe")
    };

    public static string CreateReleaseId(DateTimeOffset moment) =>
        moment.ToString("yyyyMMdd-HHmmss-fff");

    [SupportedOSPlatform("windows")]
    public static InstallResult Install(InstallRequest request)
    {
        string packageRoot = Path.GetFullPath(request.PackageRoot);
        VerifyPayload(packageRoot);

        string releaseDirectory = Path.Combine(request.DataRoot, "releases", request.ReleaseId);
        foreach ((string component, _) in RequiredPayload)
        {
            CopyTree(
                Path.Combine(packageRoot, component),
                Path.Combine(releaseDirectory, component));
        }

        string stableBootstrapRoot = Path.Combine(request.DataRoot, "bootstrap");
        Directory.CreateDirectory(stableBootstrapRoot);
        string stableBootstrapPath = Path.Combine(stableBootstrapRoot, "rwl.exe");
        ReplaceInPlace(Path.Combine(releaseDirectory, "bootstrap", "rwl.exe"), stableBootstrapPath);

        string pointerPath = Path.Combine(request.DataRoot, "current.json");
        WritePointer(pointerPath, new CurrentRelease
        {
            ReleaseId = request.ReleaseId,
            Desktop = Path.Combine(releaseDirectory, "desktop", "RhinoWorktreeLauncher.exe"),
            Cli = Path.Combine(releaseDirectory, "cli", "rwl-cli.exe"),
            Mcp = Path.Combine(releaseDirectory, "mcp", "rwl-mcp.exe")
        });

        string? shortcutPath = null;
        if (request.CreateShortcut)
        {
            Directory.CreateDirectory(request.StartMenuDirectory);
            shortcutPath = Path.Combine(request.StartMenuDirectory, "Rhino Worktree Launcher.lnk");
            ShellLink.Create(
                shortcutPath,
                stableBootstrapPath,
                "desktop",
                stableBootstrapRoot,
                "Launch Rhino from configured Git worktrees");
        }

        return new InstallResult(releaseDirectory, stableBootstrapPath, pointerPath, shortcutPath);
    }

    private static void VerifyPayload(string packageRoot)
    {
        if (!Directory.Exists(packageRoot))
            throw new DirectoryNotFoundException($"The RWL payload was not found at '{packageRoot}'.");

        foreach ((string component, string executable) in RequiredPayload)
        {
            string path = Path.Combine(packageRoot, component, executable);
            if (!File.Exists(path))
                throw new FileNotFoundException($"The RWL payload is incomplete. Missing '{path}'.", path);
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }

    /// <summary>
    /// Replaces a file that another process may be running. A running executable cannot be
    /// overwritten, but it can be renamed, so the old one is moved aside and deleted on the
    /// next install once nothing holds it.
    /// </summary>
    private static void ReplaceInPlace(string source, string destination)
    {
        RemoveDisplacedBackups(destination);
        if (File.Exists(destination))
        {
            string displaced = destination + $".{DateTime.UtcNow.Ticks:x}.old";
            File.Move(destination, displaced);
            TryDelete(displaced);
        }
        File.Copy(source, destination, overwrite: true);
    }

    private static void RemoveDisplacedBackups(string destination)
    {
        string? directory = Path.GetDirectoryName(destination);
        if (directory is null || !Directory.Exists(directory))
            return;
        foreach (string stale in Directory.EnumerateFiles(
            directory,
            Path.GetFileName(destination) + ".*.old"))
        {
            TryDelete(stale);
        }
    }

    private static void TryDelete(string path)
    {
        // A displaced executable is still held while the process using it runs. Leaving it
        // is correct: the next install removes it, and it is never what anything resolves.
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void WritePointer(string pointerPath, CurrentRelease release)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pointerPath)!);
        string temporary = pointerPath + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(release, CurrentRelease.Format));
        File.Move(temporary, pointerPath, overwrite: true);
    }
}

/// <summary>
/// The release pointer, written by the install and resolved on every forward. One type
/// rather than two, because the reader and the writer have to agree on the file exactly.
///
/// Camel case is a contract, not a preference: <c>RwlProcessInventory.ReadCurrentReleaseId</c>
/// looks the components up with a case-sensitive <c>TryGetProperty</c>, so a pointer written
/// in any other casing leaves doctor unable to name the installed release.
/// </summary>
internal sealed class CurrentRelease
{
    public static JsonSerializerOptions Format { get; } = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static JsonSerializerOptions Read { get; } = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    public string ReleaseId { get; init; } = string.Empty;
    public string Desktop { get; init; } = string.Empty;
    public string Cli { get; init; } = string.Empty;
    public string Mcp { get; init; } = string.Empty;
}

/// <summary>
/// The shell's own shortcut writer. The PowerShell installer reached this through the
/// WScript.Shell COM object, which is Windows Script Host; going straight to IShellLink
/// keeps the one remaining scripting dependency out of the install.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ShellLink
{
    public static void Create(
        string shortcutPath,
        string targetPath,
        string arguments,
        string workingDirectory,
        string description)
    {
        IShellLinkW link = (IShellLinkW)new ShellLinkCoClass();
        link.SetPath(targetPath);
        link.SetArguments(arguments);
        link.SetWorkingDirectory(workingDirectory);
        link.SetDescription(description);
        link.SetIconLocation(targetPath, 0);
        ((IPersistFile)link).Save(shortcutPath, fRemember: true);
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(
            [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file,
            int maxPath,
            IntPtr findData,
            uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription(
            [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name,
            int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(
            [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder directory,
            int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments(
            [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder arguments,
            int maxArguments);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation(
            [MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath,
            int iconPathLength,
            out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRelative, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
