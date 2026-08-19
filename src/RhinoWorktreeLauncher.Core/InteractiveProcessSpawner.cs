using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Rwl.Protocol;

namespace RhinoWorktreeLauncher;

// Starts a process from the interactive Windows shell rather than as a child of this
// process. A launcher host can be spawned inside a per-process sandbox that intercepts its
// registry writes, and a child of that host inherits the interception; a process the shell
// resolves does not. Handing a shortcut to explorer.exe is what puts the new process
// outside this one's chain (ADR 0015).
internal static class InteractiveProcessSpawner
{
    public static string ResolveBootstrapPath()
    {
        string bootstrapPath = Environment.GetEnvironmentVariable("RWL_BOOTSTRAP_PATH") ?? string.Empty;
        if (!File.Exists(bootstrapPath))
        {
            throw new LaunchDiagnosticException(
                LaunchExecutorCodes.InteractiveSpawnUnavailable,
                "RWL cannot reach the interactive Windows shell because its bootstrap executable is " +
                "not resolvable from this process. Start RWL through the installed " +
                @"'%LOCALAPPDATA%\RhinoWorktreeLauncher\bootstrap\rwl.exe'.");
        }
        return bootstrapPath;
    }

    // The returned handle owns the temporary shortcut, which stays on disk until explorer
    // has resolved it.
    public static IDisposable Spawn(string bootstrapPath, string arguments)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Interactive process spawning requires Windows.");

        string shortcutPath = Path.Combine(Path.GetTempPath(), $"rwl-spawn-{Guid.NewGuid():N}.lnk");
        CreateShortcut(shortcutPath, bootstrapPath, arguments);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false,
                ArgumentList = { shortcutPath }
            })?.Dispose();
            return new TemporaryShortcut(shortcutPath);
        }
        catch
        {
            File.Delete(shortcutPath);
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CreateShortcut(string shortcutPath, string targetPath, string arguments)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell") ??
            throw new InvalidOperationException("Windows Script Host is unavailable.");
        object shell = Activator.CreateInstance(shellType) ??
            throw new InvalidOperationException("Windows Script Host could not be created.");
        object? shortcut = null;
        try
        {
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { shortcutPath });
            if (shortcut is null)
                throw new InvalidOperationException("Windows could not create the interactive launch shortcut.");

            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
            shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { arguments });
            shortcutType.InvokeMember(
                "WorkingDirectory",
                BindingFlags.SetProperty,
                null,
                shortcut,
                new object[] { Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory });
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, Array.Empty<object>());
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
                _ = Marshal.FinalReleaseComObject(shortcut);
            if (Marshal.IsComObject(shell))
                _ = Marshal.FinalReleaseComObject(shell);
        }
    }

    private sealed class TemporaryShortcut : IDisposable
    {
        private readonly string _path;

        public TemporaryShortcut(string path) => _path = path;

        public void Dispose()
        {
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
                // Explorer may briefly retain the shortcut after resolving it.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup is best-effort and confined to the per-launch temp file.
            }
        }
    }
}

// A failure that already knows the diagnostic code its condition is reported under, so no
// launch step degrades into an unnamed error on its way out.
internal sealed class LaunchDiagnosticException : Exception
{
    public LaunchDiagnosticException(string code, string message)
        : base(message) => Code = code;

    public LaunchDiagnosticException(string code, string message, Exception innerException)
        : base(message, innerException) => Code = code;

    public string Code { get; }
}
