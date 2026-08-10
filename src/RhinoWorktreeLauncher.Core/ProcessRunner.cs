using System.Diagnostics;

namespace RhinoWorktreeLauncher;

internal static class ProcessRunner
{
    public static async Task<string> RunAsync(
        string fileName,
        string workingDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken) => await RunAsync(
        fileName,
        workingDirectory,
        arguments,
        null,
        cancellationToken);

    public static async Task<string> RunAsync(
        string fileName,
        string workingDirectory,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach ((string name, string value) in environment)
                startInfo.Environment[name] = value;
        }

        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException($"Could not start {fileName}.");
        process.StandardInput.Close();
        try
        {
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            string output = await outputTask;
            string error = await errorTask;
            if (process.ExitCode != 0)
                throw CreateFailure(fileName, process.ExitCode, error, TailLines(output));
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
        CancellationToken cancellationToken) => await RunLinesAsync(
        fileName,
        workingDirectory,
        arguments,
        null,
        onOutputLine,
        cancellationToken);

    public static async Task RunLinesAsync(
        string fileName,
        string workingDirectory,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        Func<string, Task> onOutputLine,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach ((string name, string value) in environment)
                startInfo.Environment[name] = value;
        }

        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException($"Could not start {fileName}.");
        process.StandardInput.Close();
        try
        {
            Queue<string> outputTail = new Queue<string>();
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                RememberLine(outputTail, line);
                await onOutputLine(line);
            }
            await process.WaitForExitAsync(cancellationToken);
            string error = await errorTask;
            if (process.ExitCode != 0)
                throw CreateFailure(fileName, process.ExitCode, error, outputTail);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static InvalidOperationException CreateFailure(
        string fileName,
        int exitCode,
        string error,
        IEnumerable<string> outputTail)
    {
        string detail = string.IsNullOrWhiteSpace(error)
            ? string.Join(Environment.NewLine, outputTail).Trim()
            : error.Trim();
        string message = string.IsNullOrWhiteSpace(detail)
            ? $"{fileName} exited with code {exitCode}."
            : $"{fileName} exited with code {exitCode}: {detail}";
        return new InvalidOperationException(message);
    }

    private static IEnumerable<string> TailLines(string output)
    {
        Queue<string> lines = new Queue<string>();
        using StringReader reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
            RememberLine(lines, line);
        return lines;
    }

    private static void RememberLine(Queue<string> lines, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        lines.Enqueue(line);
        while (lines.Count > 20)
            _ = lines.Dequeue();
    }
}
