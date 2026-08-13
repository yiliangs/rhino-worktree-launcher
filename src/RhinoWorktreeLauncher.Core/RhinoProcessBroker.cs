using System.Collections;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Rwl.Protocol;

namespace RhinoWorktreeLauncher;

internal static class RhinoProcessBroker
{
    public static Process Start(ProcessStartInfo configured)
    {
        string bootstrapPath = Environment.GetEnvironmentVariable("RWL_BOOTSTRAP_PATH") ?? string.Empty;
        if (!File.Exists(bootstrapPath))
        {
            throw new InvalidOperationException(
                "The Rhino launch broker is unavailable. Start RWL through its installed rwl.exe bootstrap.");
        }

        return Start(configured, bootstrapPath, LaunchThroughExplorer);
    }

    internal static Process Start(
        ProcessStartInfo configured,
        string bootstrapPath,
        Func<string, string, IDisposable> interactiveLaunch) =>
        StartAsync(configured, bootstrapPath, interactiveLaunch).GetAwaiter().GetResult();

    private static async Task<Process> StartAsync(
        ProcessStartInfo configured,
        string bootstrapPath,
        Func<string, string, IDisposable> interactiveLaunch)
    {
        string pipeName = $"rwl-rhino-{Guid.NewGuid():N}";
        using NamedPipeServerStream pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using IDisposable launchArtifact = interactiveLaunch(bootstrapPath, pipeName);
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            using StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
            using StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
            RhinoLaunchRequest request = new RhinoLaunchRequest
            {
                Executable = configured.FileName,
                WorkingDirectory = configured.WorkingDirectory,
                Arguments = configured.ArgumentList.ToArray(),
                Environment = GetEnvironmentOverrides(configured)
            };
            await writer.WriteLineAsync(RhinoBrokerProtocol.SerializeRequest(request))
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);

            string? responseLine = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            RhinoLaunchResponse response = responseLine is null
                ? throw new InvalidOperationException("The Rhino launch broker closed without returning a process.")
                : RhinoBrokerProtocol.DeserializeResponse(responseLine) ??
                    throw new InvalidDataException("The Rhino launch broker returned an empty response.");
            if (!string.IsNullOrWhiteSpace(response.Error))
                throw new InvalidOperationException(response.Error);
            if (response.ProcessId <= 0)
                throw new InvalidDataException("The Rhino launch broker returned an invalid process ID.");

            return Process.GetProcessById(response.ProcessId);
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException("The interactive Windows shell did not start the Rhino launch broker.", exception);
        }
    }

    private static Dictionary<string, string?> GetEnvironmentOverrides(ProcessStartInfo configured)
    {
        Dictionary<string, string?> inherited = Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .ToDictionary(
                entry => (string)entry.Key,
                entry => entry.Value?.ToString(),
                StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string?> overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string?> variable in configured.Environment)
        {
            if (!inherited.TryGetValue(variable.Key, out string? inheritedValue) ||
                !string.Equals(variable.Value, inheritedValue, StringComparison.Ordinal))
            {
                overrides[variable.Key] = variable.Value;
            }
            inherited.Remove(variable.Key);
        }
        foreach (string removed in inherited.Keys)
            overrides[removed] = null;

        return overrides;
    }

    private static IDisposable LaunchThroughExplorer(string bootstrapPath, string pipeName)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Rhino launch brokering requires Windows.");

        string shortcutPath = Path.Combine(Path.GetTempPath(), $"rwl-rhino-{Guid.NewGuid():N}.lnk");
        CreateShortcut(shortcutPath, bootstrapPath, $"rhino-broker --pipe {pipeName}");
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
                throw new InvalidOperationException("Windows could not create the Rhino launch shortcut.");

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
