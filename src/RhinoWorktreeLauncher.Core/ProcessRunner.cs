using System.Diagnostics;

namespace RhinoWorktreeLauncher;

internal static class ProcessRunner
{
    public static async Task<string> RunAsync(
        string fileName,
        string workingDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = ProcessLaunchGate.Start(() => Process.Start(startInfo)) ??
            throw new InvalidOperationException($"Could not start {fileName}.");
        try
        {
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            string output = await outputTask;
            string error = await errorTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"{fileName} exited with code {process.ExitCode}: {error.Trim()}");
            }
            return output;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
    }

    public static async Task RunLinesAsync(
        string fileName,
        string workingDirectory,
        IEnumerable<string> arguments,
        Func<string, Task> onOutputLine,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = ProcessLaunchGate.Start(() => Process.Start(startInfo)) ??
            throw new InvalidOperationException($"Could not start {fileName}.");
        try
        {
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
                await onOutputLine(line);
            await process.WaitForExitAsync(cancellationToken);
            string error = await errorTask;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"{fileName} exited with code {process.ExitCode}: {error.Trim()}");
            }
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
