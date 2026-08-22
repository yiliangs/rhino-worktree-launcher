using Rwl.Protocol;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace RhinoWorktreeLauncher.Tests;

public sealed class LaunchExecutorProtocolTests
{
    [Fact]
    public void A_request_survives_the_wire_unchanged()
    {
        LaunchExecutorRequest request = new LaunchExecutorRequest
        {
            LaunchId = "abc",
            HostKind = "mcp",
            ReleaseId = "1.2.3",
            RhinoVersion = 8,
            PluginId = Guid.NewGuid().ToString("D"),
            PluginName = "Sample",
            PluginPath = @"C:\worktree\Sample.rhp",
            RhinoExecutable = @"C:\Program Files\Rhino 8\System\Rhino.exe",
            RhinoRuntime = "netcore",
            WorkingDirectory = @"C:\worktree",
            LocksDirectory = @"C:\data\locks",
            LogsDirectory = @"C:\data\logs",
            TimeoutSeconds = 180
        };

        string json = LaunchExecutorProtocol.SerializeRequest(request);

        Assert.Contains("\"pluginPath\"", json, StringComparison.Ordinal);
        Assert.Equal(request, LaunchExecutorProtocol.DeserializeRequest(json));
    }

    [Fact]
    public void An_event_survives_the_wire_unchanged()
    {
        LaunchExecutorEvent value = new LaunchExecutorEvent
        {
            Kind = LaunchExecutorEventKind.Result,
            LaunchId = "abc",
            Stage = "verify",
            Code = LaunchExecutorCodes.LaunchVerified,
            Message = "Rhino holds the artifact.",
            Severity = "info",
            Succeeded = true,
            RhinoProcessId = 4242,
            ExecutorLogPath = @"C:\data\logs\executor.jsonl"
        };

        LaunchExecutorEvent round = Assert.IsType<LaunchExecutorEvent>(
            LaunchExecutorProtocol.DeserializeEvent(LaunchExecutorProtocol.SerializeEvent(value)));

        Assert.Equal(value, round);
        Assert.True(round.IsResult);
    }

    // Dictionary equality is referential on the record, so the environment's survival is
    // asserted by content rather than by the record's own Equals.
    [Fact]
    public void A_request_environment_survives_the_wire_unchanged()
    {
        LaunchExecutorRequest request = new LaunchExecutorRequest
        {
            LaunchId = "abc",
            Environment = new Dictionary<string, string> { ["NATALIE_SUITE_REPRO"] = "1" }
        };

        LaunchExecutorRequest? round = LaunchExecutorProtocol.DeserializeRequest(
            LaunchExecutorProtocol.SerializeRequest(request));

        Assert.NotNull(round);
        Assert.Equal("1", round!.Environment!["NATALIE_SUITE_REPRO"]);
        // A request without one stays without one: null must not round-trip into empty.
        Assert.Null(LaunchExecutorProtocol.DeserializeRequest(
            LaunchExecutorProtocol.SerializeRequest(new LaunchExecutorRequest()))!.Environment);
    }

    [Fact]
    public void A_reserved_environment_name_is_described_by_the_shared_rule() =>
        Assert.Contains(
            LaunchEnvironment.ReservedPrefix,
            LaunchEnvironment.Describe(new Dictionary<string, string> { ["rwl_launch_id"] = "x" }));

    [Fact]
    public void A_request_carries_the_current_protocol_version_by_default() =>
        Assert.Equal(LaunchExecutorProtocol.Version, new LaunchExecutorRequest().ProtocolVersion);

    // The executor is reached through the built bootstrap, so the mode name, the pipe
    // option, and the event wire are one contract across three executables.
    [Fact]
    public async Task The_built_executor_answers_a_ping_over_the_pipe()
    {
        using PipeHarness harness = new PipeHarness();
        using Process executor = harness.StartExecutor();

        LaunchExecutorEvent result = await harness.ExchangeAsync(new LaunchExecutorRequest
        {
            Mode = LaunchExecutorMode.Ping,
            LaunchId = "ping"
        });

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(LaunchExecutorCodes.InteractiveSpawnReady, result.Code);
        await executor.WaitForExitAsync(harness.Token);
        Assert.Equal(0, executor.ExitCode);
    }

    [Fact]
    public async Task An_executor_speaking_another_protocol_version_says_so_by_name()
    {
        using PipeHarness harness = new PipeHarness();
        using Process executor = harness.StartExecutor();

        LaunchExecutorEvent result = await harness.ExchangeAsync(new LaunchExecutorRequest
        {
            ProtocolVersion = LaunchExecutorProtocol.Version + 1,
            Mode = LaunchExecutorMode.Ping,
            LaunchId = "mismatch"
        });

        Assert.False(result.Succeeded);
        Assert.Equal(LaunchExecutorCodes.ExecutorProtocolMismatch, result.Code);
        await executor.WaitForExitAsync(harness.Token);
        Assert.Equal(1, executor.ExitCode);
    }

    [Fact]
    public async Task An_unknown_mode_ends_the_executor_as_an_invalid_request()
    {
        using PipeHarness harness = new PipeHarness();
        using Process executor = harness.StartExecutor();

        LaunchExecutorEvent result = await harness.ExchangeAsync(new LaunchExecutorRequest
        {
            Mode = "sabotage",
            LaunchId = "unknown-mode"
        });

        Assert.False(result.Succeeded);
        Assert.Equal(LaunchExecutorCodes.ExecutorRequestInvalid, result.Code);
        await executor.WaitForExitAsync(harness.Token);
    }

    // A bootstrap that cannot reach the current release leaves a host waiting on a pipe, so
    // it reports that by name rather than exiting silently.
    [Fact]
    public async Task The_bootstrap_reports_by_name_when_it_cannot_start_an_executor()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        using PipeHarness harness = new PipeHarness();
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(BootstrapOutputPath());
        startInfo.ArgumentList.Add("launch-executor");
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(harness.PipeName);
        // No current.json exists under this data root, which is what a half-removed or
        // half-updated installation looks like.
        startInfo.Environment["RWL_DATA_ROOT"] = temporary.PathFor("data");

        using Process bootstrap = Process.Start(startInfo)!;
        LaunchExecutorEvent result = await harness.ReadResultAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(LaunchExecutorCodes.ExecutorBootstrapFailed, result.Code);
        await bootstrap.WaitForExitAsync(harness.Token);
        Assert.Equal(1, bootstrap.ExitCode);
    }

    private static string BootstrapOutputPath() => Path.Combine(
        RepositoryRoot(),
        "src",
        "Rwl.Bootstrap",
        "bin",
        BuildConfiguration(),
        "net8.0",
        "win-x64",
        "rwl.dll");

    private static string BuildConfiguration() => new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ??
        throw new DirectoryNotFoundException("The test build configuration was not found.");

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RhinoWorktreeLauncher.slnx")))
            directory = directory.Parent;
        return directory?.FullName ??
            throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }

    // Stands in for the launcher host's half of the contract: it owns the pipe and speaks
    // the shipped protocol, but starts the executable directly instead of through the
    // interactive shell, which is what makes it runnable in a test.
    private sealed class PipeHarness : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly CancellationTokenSource _timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        private StreamReader? _reader;

        public PipeHarness()
        {
            PipeName = $"rwl-executor-test-{Guid.NewGuid():N}";
            _pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }

        public string PipeName { get; }

        public CancellationToken Token => _timeout.Token;

        public Process StartExecutor()
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(AppContext.BaseDirectory, "rwl-cli.exe"),
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("launch-executor");
            startInfo.ArgumentList.Add("--pipe");
            startInfo.ArgumentList.Add(PipeName);
            return Process.Start(startInfo)!;
        }

        public async Task<LaunchExecutorEvent> ExchangeAsync(LaunchExecutorRequest request)
        {
            await _pipe.WaitForConnectionAsync(Token);
            _reader = new StreamReader(_pipe, Encoding.UTF8, false, leaveOpen: true);
            StreamWriter writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
            await writer.WriteLineAsync(LaunchExecutorProtocol.SerializeRequest(request)).WaitAsync(Token);
            return await ReadEventAsync();
        }

        public async Task<LaunchExecutorEvent> ReadResultAsync()
        {
            await _pipe.WaitForConnectionAsync(Token);
            _reader = new StreamReader(_pipe, Encoding.UTF8, false, leaveOpen: true);
            return await ReadEventAsync();
        }

        private async Task<LaunchExecutorEvent> ReadEventAsync()
        {
            while (true)
            {
                string line = await _reader!.ReadLineAsync(Token) ??
                    throw new InvalidDataException("The executor closed the pipe without a result.");
                LaunchExecutorEvent value = LaunchExecutorProtocol.DeserializeEvent(line) ??
                    throw new InvalidDataException($"'{line}' is not a launch event.");
                if (value.IsResult)
                    return value;
            }
        }

        public void Dispose()
        {
            _reader?.Dispose();
            _pipe.Dispose();
            _timeout.Dispose();
        }
    }
}
