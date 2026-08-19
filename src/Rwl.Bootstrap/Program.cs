using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Rwl.Protocol;

namespace Rwl.Bootstrap;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "desktop";
        try
        {
            // Answered here rather than by the current release, so the reader shares no
            // code and no process with whoever wrote the key it is asked about, and so it
            // still answers on a half-updated installation.
            if (mode == "registry-probe" && OperatingSystem.IsWindows())
                return await RunRegistryProbeAsync(args);

            if (mode is not "desktop" and not "mcp" and not "launch-executor" &&
                !Console.IsOutputRedirected)
            {
                _ = AttachConsole(AttachParentProcess);
            }
            string dataRoot = Environment.GetEnvironmentVariable("RWL_DATA_ROOT") ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RhinoWorktreeLauncher");
            string currentPath = Path.Combine(dataRoot, "current.json");
            if (!File.Exists(currentPath))
                throw new FileNotFoundException("Rhino Worktree Launcher is not installed.", currentPath);
            CurrentRelease current = JsonSerializer.Deserialize<CurrentRelease>(
                await File.ReadAllTextAsync(currentPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
                throw new InvalidDataException($"Release pointer '{currentPath}' is empty.");

            string executable;
            string[] forwarded;
            bool wait;
            bool bridgeInput;
            switch (mode)
            {
                case "desktop":
                    executable = current.Desktop;
                    forwarded = args.Skip(1).ToArray();
                    wait = false;
                    bridgeInput = false;
                    break;
                case "mcp":
                    executable = current.Mcp;
                    forwarded = args.Skip(1).ToArray();
                    wait = true;
                    bridgeInput = true;
                    break;
                // The launch executor is the one mode that exists to be started by the
                // interactive Windows shell rather than by a launcher host. This bootstrap
                // is what the shell resolves; it hands the whole command to the current
                // release's CLI and ends, so the executor outlives it and no shell process
                // waits on a Rhino session.
                case "launch-executor":
                    executable = current.Cli;
                    forwarded = args;
                    wait = false;
                    bridgeInput = false;
                    break;
                default:
                    executable = current.Cli;
                    forwarded = args;
                    wait = true;
                    bridgeInput = mode == "session-context";
                    break;
            }

            // This bootstrap is a windowless host, so it holds no console unless the
            // AttachConsole above succeeded. Starting a console executable such as
            // rwl-cli.exe from a console-less process makes Windows allocate a fresh
            // console window, which flashes on every hook invocation. Redirecting the
            // standard streams does not suppress that; only CreateNoWindow does.
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (wait)
            {
                startInfo.RedirectStandardInput = bridgeInput;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
            }
            foreach (string argument in forwarded)
                startInfo.ArgumentList.Add(argument);
            startInfo.Environment["RWL_BOOTSTRAP_PATH"] = Environment.ProcessPath ?? string.Empty;
            using Process process = Process.Start(startInfo) ??
                throw new InvalidOperationException($"Could not start '{executable}'.");
            if (!wait)
                return 0;

            Task output = process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
            Task error = process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
            if (bridgeInput)
                _ = EndWithInputAsync(process);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            return process.ExitCode;
        }
        catch (Exception exception)
        {
            // A launcher host is waiting on the executor's pipe, and a bootstrap that
            // cannot reach the current release would otherwise leave it waiting for a
            // process that will never connect.
            if (mode == "launch-executor")
                await ReportExecutorStartFailureAsync(args, exception);
            await Console.Error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private const uint AttachParentProcess = 0xffffffff;

    private static async Task ReportExecutorStartFailureAsync(string[] args, Exception failure)
    {
        try
        {
            string pipeName = RequiredOption(args, "--pipe");
            using NamedPipeClientStream pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await pipe.ConnectAsync(timeout.Token);
            using StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
            await writer.WriteLineAsync(LaunchExecutorProtocol.SerializeEvent(new LaunchExecutorEvent
            {
                Kind = LaunchExecutorEventKind.Result,
                Code = LaunchExecutorCodes.ExecutorBootstrapFailed,
                Message = $"The RWL bootstrap could not start a launch executor: {failure.Message}",
                Severity = "error"
            })).WaitAsync(timeout.Token);
        }
        // The host names this same failure as executor_start_timeout when it cannot be
        // reached, so a pipe that is already gone still ends in a named condition.
        catch (Exception)
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunRegistryProbeAsync(string[] args)
    {
        string pipeName = RequiredOption(args, "--pipe");
        using NamedPipeClientStream pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await pipe.ConnectAsync(timeout.Token);
        using StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
        using StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };

        RegistryProbeResult result;
        try
        {
            string requestLine = await reader.ReadLineAsync(timeout.Token) ??
                throw new InvalidDataException("The caller closed the pipe without sending a probe request.");
            RegistryProbeRequest request = RegistryProbeProtocol.DeserializeRequest(requestLine) ??
                throw new InvalidDataException("The registry probe request was empty.");
            result = Read(request);
        }
        // The caller decides what an unreadable key means; this process only reports it,
        // and reports it in the same shape as a successful read.
        catch (Exception exception)
        {
            result = new RegistryProbeResult { Error = exception.Message };
        }

        await writer.WriteLineAsync(RegistryProbeProtocol.SerializeResult(result)).WaitAsync(timeout.Token);
        return result.Error is null ? 0 : 1;
    }

    [SupportedOSPlatform("windows")]
    private static RegistryProbeResult Read(RegistryProbeRequest request)
    {
        RegistryKey hive = request.Hive switch
        {
            RegistryHives.CurrentUser => Registry.CurrentUser,
            RegistryHives.LocalMachine => Registry.LocalMachine,
            _ => throw new ArgumentException($"'{request.Hive}' is not a registry hive this probe reads.")
        };
        using RegistryKey? key = hive.OpenSubKey(request.KeyPath, writable: false);
        Dictionary<string, string?> values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in request.Values)
            values[name] = key?.GetValue(name)?.ToString();
        return new RegistryProbeResult
        {
            Exists = key is not null,
            Values = values
        };
    }

    private static string RequiredOption(string[] args, string option)
    {
        int index = Array.FindIndex(args, value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            throw new ArgumentException($"Missing required option '{option}'.");
        return args[index + 1];
    }

    // How long a child is given to notice that its input has ended. It matches the grace the
    // server gives its own host, so one number decides how long any RWL process may take to
    // end after its session does.
    private static readonly TimeSpan ChildExitGrace = TimeSpan.FromSeconds(10);

    // This bootstrap is the whole session for the server it forwards to: the client's streams
    // reach that server only through here. When this end of the bridge closes, the session is
    // over, and a child that stays running is an orphan nobody can reach and nobody notices.
    private static async Task EndWithInputAsync(Process process)
    {
        await PumpInputAsync(process);
        if (process.HasExited)
            return;

        using CancellationTokenSource grace = new CancellationTokenSource(ChildExitGrace);
        try
        {
            await process.WaitForExitAsync(grace.Token);
            return;
        }
        catch (OperationCanceledException)
        {
        }

        await Console.Error.WriteLineAsync(
            $"[stdio_child_did_not_end] Process {process.Id} did not exit within " +
            $"{ChildExitGrace.TotalSeconds:0.###} seconds of its session ending, so it is being " +
            "ended here. It would otherwise keep running with no client able to reach it.");
        try
        {
            process.Kill(entireProcessTree: true);
        }
        // The child exited on its own between the wait and the kill, which is the outcome
        // this path wanted.
        catch (InvalidOperationException)
        {
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(
                $"[stdio_child_unendable] Process {process.Id} could not be ended: " +
                $"{exception.Message} It is now an orphan that no client can reach. " +
                "Run 'rwl doctor' to list it, and end it from Task Manager.");
        }
    }

    private static async Task PumpInputAsync(Process process)
    {
        try
        {
            await Console.OpenStandardInput().CopyToAsync(process.StandardInput.BaseStream);
        }
        // Either end of the bridge breaking ends the bridge. Which end broke is decided by
        // the caller, from whether the child is still running.
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            // Closing flushes, and a child that has already exited leaves nothing to flush
            // into. The child is gone either way, which is what closing was for.
            catch (IOException)
            {
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    private sealed class CurrentRelease
    {
        public string Desktop { get; init; } = string.Empty;
        public string Cli { get; init; } = string.Empty;
        public string Mcp { get; init; } = string.Empty;
    }

}
