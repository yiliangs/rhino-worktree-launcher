using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
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
                _ = PumpInputAsync(process);
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

    private static string RequiredOption(string[] args, string option)
    {
        int index = Array.FindIndex(args, value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            throw new ArgumentException($"Missing required option '{option}'.");
        return args[index + 1];
    }

    private static async Task PumpInputAsync(Process process)
    {
        try
        {
            await Console.OpenStandardInput().CopyToAsync(process.StandardInput.BaseStream);
        }
        catch (IOException) when (process.HasExited)
        {
            // The child may close stdin after a terminal response.
        }
        finally
        {
            process.StandardInput.Close();
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
