using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Rwl.Protocol;

namespace RhinoWorktreeLauncher.Tests;

public sealed class RhinoBrokerProtocolTests
{
    [Fact]
    public async Task Built_bootstrap_broker_preserves_the_wire_contract_and_environment_removals()
    {
        string pipeName = $"rwl-bootstrap-test-{Guid.NewGuid():N}";
        string removedVariable = $"RWL_TEST_REMOVE_{Guid.NewGuid():N}";
        using NamedPipeServerStream pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        ProcessStartInfo brokerStart = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        brokerStart.ArgumentList.Add(BootstrapOutputPath());
        brokerStart.ArgumentList.Add("rhino-broker");
        brokerStart.ArgumentList.Add("--pipe");
        brokerStart.ArgumentList.Add(pipeName);
        brokerStart.Environment[removedVariable] = "remove-me";

        using Process broker = Process.Start(brokerStart) ??
            throw new InvalidOperationException("Could not start the built RWL bootstrap.");
        await pipe.WaitForConnectionAsync(timeout.Token);
        using StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
        using StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };

        string command = $"if defined {removedVariable} (exit /b 7) else (ping 127.0.0.1 -n 2 > nul & exit /b 0)";
        RhinoLaunchRequest request = new RhinoLaunchRequest
        {
            Executable = Environment.GetEnvironmentVariable("ComSpec") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            WorkingDirectory = Environment.CurrentDirectory,
            Arguments = new[] { "/d", "/c", command },
            Environment = new Dictionary<string, string?> { [removedVariable] = null }
        };
        string requestJson = RhinoBrokerProtocol.SerializeRequest(request);
        Assert.Contains("\"executable\"", requestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Executable\"", requestJson, StringComparison.Ordinal);
        await writer.WriteLineAsync(requestJson).WaitAsync(timeout.Token);

        string responseJson = await reader.ReadLineAsync(timeout.Token) ??
            throw new InvalidDataException("The built bootstrap closed without a broker response.");
        Assert.Contains("\"ProcessId\"", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"processId\"", responseJson, StringComparison.Ordinal);
        RhinoLaunchResponse response = RhinoBrokerProtocol.DeserializeResponse(responseJson) ??
            throw new InvalidDataException("The built bootstrap returned an empty broker response.");
        Assert.Null(response.Error);
        Assert.True(response.ProcessId > 0);

        using Process child = Process.GetProcessById(response.ProcessId);
        await child.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, child.ExitCode);

        await broker.WaitForExitAsync(timeout.Token);
        string brokerError = await broker.StandardError.ReadToEndAsync(timeout.Token);
        Assert.True(broker.ExitCode == 0, brokerError);
    }

    private static string BootstrapOutputPath()
    {
        DirectoryInfo testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        string configuration = testOutput.Parent?.Name ??
            throw new DirectoryNotFoundException("The test build configuration was not found.");
        return Path.Combine(
            RepositoryRoot(),
            "src",
            "Rwl.Bootstrap",
            "bin",
            configuration,
            "net8.0",
            "win-x64",
            "rwl.dll");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RhinoWorktreeLauncher.slnx")))
            directory = directory.Parent;
        return directory?.FullName ??
            throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }
}
