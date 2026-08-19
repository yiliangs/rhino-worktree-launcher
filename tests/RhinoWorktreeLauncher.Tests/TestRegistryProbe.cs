using Microsoft.Win32;
using Rwl.Protocol;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;

namespace RhinoWorktreeLauncher.Tests;

// Stand-ins for the independent reader. The shipped one spawns a separate process; these
// answer in process, which is enough for tests about what the launch does with the answer.
// The reader's independence has its own test, against the built probe.
[SupportedOSPlatform("windows")]
internal static class TestRegistryProbe
{
    // A machine where writes land: the reader sees what the writer wrote.
    public static RegistryProbeRunner Truthful { get; } = (request, _, _) =>
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(request.KeyPath, writable: false);
        Dictionary<string, string?> values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in request.Values)
            values[name] = key?.GetValue(name)?.ToString();
        return Task.FromResult(new RegistryProbeResult { Exists = key is not null, Values = values });
    };

    // The proven failure: the writer reads its own key back and sees it, while no other
    // process ever does.
    public static RegistryProbeRunner Blind { get; } = (request, _, _) => Task.FromResult(
        new RegistryProbeResult
        {
            Exists = false,
            Values = request.Values.ToDictionary(name => name, _ => (string?)null)
        });

    public static RegistryProbeRunner Failing(string error) => (_, _, _) => Task.FromResult(
        new RegistryProbeResult { Error = error });

    // The shipped probe, run as a real separate process from the built bootstrap.
    public static async Task<RegistryProbeResult> BootstrapAsync(
        RegistryProbeRequest request,
        bool spawnInteractively,
        CancellationToken cancellationToken)
    {
        string pipeName = $"rwl-probe-test-{Guid.NewGuid():N}";
        using NamedPipeServerStream pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(BootstrapOutputPath());
        startInfo.ArgumentList.Add("registry-probe");
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);

        using Process probe = Process.Start(startInfo)!;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await pipe.WaitForConnectionAsync(timeout.Token);
        using StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
        using StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        await writer.WriteLineAsync(RegistryProbeProtocol.SerializeRequest(request)).WaitAsync(timeout.Token);
        string line = await reader.ReadLineAsync(timeout.Token) ??
            throw new InvalidDataException("The registry probe ended without answering.");
        await probe.WaitForExitAsync(timeout.Token);
        return RegistryProbeProtocol.DeserializeResult(line) ??
            throw new InvalidDataException($"The registry probe answered '{line}'.");
    }

    private static string BootstrapOutputPath() => Path.Combine(
        RepositoryRoot(),
        "src",
        "Rwl.Bootstrap",
        "bin",
        new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ??
            throw new DirectoryNotFoundException("The test build configuration was not found."),
        "net8.0",
        "win-x64",
        "rwl.dll");

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RhinoWorktreeLauncher.slnx")))
            directory = directory.Parent;
        return directory?.FullName ??
            throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }
}
