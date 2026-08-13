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
        try
        {
            string mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "desktop";
            if (mode == "rhino-broker")
                return await RunRhinoBrokerAsync(args);

            if (mode is not "desktop" and not "mcp" &&
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
                default:
                    executable = current.Cli;
                    forwarded = args;
                    wait = true;
                    bridgeInput = mode == "session-context";
                    break;
            }

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false
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
            await Console.Error.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private const uint AttachParentProcess = 0xffffffff;

    private static async Task<int> RunRhinoBrokerAsync(string[] args)
    {
        string pipeName = RequiredOption(args, "--pipe");
        using NamedPipeClientStream pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await pipe.ConnectAsync(timeout.Token);

        using StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
        using StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        try
        {
            string? requestLine = await reader.ReadLineAsync(timeout.Token);
            RhinoLaunchRequest request = requestLine is null
                ? throw new InvalidDataException("The launcher closed without providing a Rhino launch request.")
                : RhinoBrokerProtocol.DeserializeRequest(requestLine) ??
                    throw new InvalidDataException("The Rhino launch request was empty.");
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = request.Executable,
                WorkingDirectory = request.WorkingDirectory,
                UseShellExecute = false
            };
            foreach (string argument in request.Arguments)
                startInfo.ArgumentList.Add(argument);
            foreach (KeyValuePair<string, string?> variable in request.Environment)
            {
                if (variable.Value is null)
                    startInfo.Environment.Remove(variable.Key);
                else
                    startInfo.Environment[variable.Key] = variable.Value;
            }

            Process process = Process.Start(startInfo) ??
                throw new InvalidOperationException($"Could not start '{request.Executable}'.");
            int processId = process.Id;
            process.Dispose();
            await writer.WriteLineAsync(RhinoBrokerProtocol.SerializeResponse(new RhinoLaunchResponse
            {
                ProcessId = processId
            })).WaitAsync(timeout.Token);
            return 0;
        }
        catch (Exception exception)
        {
            await writer.WriteLineAsync(RhinoBrokerProtocol.SerializeResponse(new RhinoLaunchResponse
            {
                Error = exception.Message
            }));
            return 1;
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
