using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class RhinoProcessBrokerTests
{
    private const string ChildThatExitsImmediately = "exit /b 0";

    [Fact]
    public async Task Broker_returns_the_real_child_and_sends_only_private_environment_overrides()
    {
        string variable = "RWL_TEST_" + Guid.NewGuid().ToString("N");
        ProcessStartInfo configured = CreateConfiguredStart(variable, "selected-worktree");

        Task<BrokerObservation>? brokerTask = null;
        using Process child = RhinoProcessBroker.Start(
            configured,
            "test-bootstrap.exe",
            (_, pipeName) =>
            {
                brokerTask = RunFakeBrokerAsync(pipeName);
                return NoopDisposable.Instance;
            });
        Assert.NotNull(brokerTask);
        BrokerObservation observation = await brokerTask;
        await child.WaitForExitAsync();

        Assert.Equal(0, child.ExitCode);
        Assert.Equal(child.Id, observation.ProcessId);
        Assert.Equal(configured.FileName, observation.Request.Executable);
        Assert.Equal(configured.ArgumentList, observation.Request.Arguments);
        Assert.Equal(
            new Dictionary<string, string?> { [variable] = "selected-worktree" },
            observation.Request.Environment);
        Assert.Null(Environment.GetEnvironmentVariable(variable));
    }

    [Fact]
    public async Task Concurrent_brokers_keep_their_private_environments_isolated()
    {
        string variable = "RWL_TEST_" + Guid.NewGuid().ToString("N");

        async Task<BrokerObservation> StartObservedAsync(string value)
        {
            Task<BrokerObservation>? brokerTask = null;
            using Process child = await Task.Run(() => RhinoProcessBroker.Start(
                CreateConfiguredStart(variable, value),
                "test-bootstrap.exe",
                (_, pipeName) =>
                {
                    brokerTask = RunFakeBrokerAsync(pipeName);
                    return NoopDisposable.Instance;
                }));
            Assert.NotNull(brokerTask);
            BrokerObservation observation = await brokerTask;
            await child.WaitForExitAsync();
            Assert.Equal(0, child.ExitCode);
            Assert.Equal(child.Id, observation.ProcessId);
            return observation;
        }

        BrokerObservation[] observations = await Task.WhenAll(
            StartObservedAsync("first"),
            StartObservedAsync("second"));

        Assert.Equal(
            new[] { "first", "second" },
            observations.Select(result => result.Request.Environment[variable]).Order().ToArray());
        Assert.Null(Environment.GetEnvironmentVariable(variable));
    }

    [Fact]
    public async Task A_child_that_is_already_gone_names_the_process_instead_of_failing_the_handle_lookup()
    {
        string variable = "RWL_TEST_" + Guid.NewGuid().ToString("N");
        Task<BrokerObservation>? brokerTask = null;

        RhinoExitedBeforeVerificationException exited =
            Assert.Throws<RhinoExitedBeforeVerificationException>(() => RhinoProcessBroker.Start(
                CreateConfiguredStart(variable, "already-gone", ChildThatExitsImmediately),
                "test-bootstrap.exe",
                (_, pipeName) =>
                {
                    // The fake broker reports the PID only after the child has fully
                    // exited, so the handle lookup deterministically has nothing to open.
                    brokerTask = RunFakeBrokerAsync(pipeName, waitForChildExit: true);
                    return NoopDisposable.Instance;
                }));

        Assert.NotNull(brokerTask);
        BrokerObservation observation = await brokerTask;
        Assert.Equal(observation.ProcessId, exited.ProcessId);
        Assert.Contains(observation.ProcessId.ToString(), exited.Message);
        Assert.IsType<ArgumentException>(exited.InnerException);
    }

    [Fact]
    public async Task Broker_does_not_capture_the_callers_synchronization_context()
    {
        string variable = "RWL_TEST_" + Guid.NewGuid().ToString("N");
        TaskCompletionSource<Process> completion = new TaskCompletionSource<Process>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Thread caller = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            try
            {
                Process child = RhinoProcessBroker.Start(
                    CreateConfiguredStart(variable, "ui-thread"),
                    "test-bootstrap.exe",
                    (_bootstrapPath, pipeName) =>
                    {
                        _ = Task.Run(() => RunFakeBrokerAsync(pipeName));
                        return NoopDisposable.Instance;
                    });
                completion.SetResult(child);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true
        };
        caller.Start();

        Task finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(3)));

        Assert.Same(completion.Task, finished);
        using Process child = await completion.Task;
        await child.WaitForExitAsync();
        Assert.Equal(0, child.ExitCode);
    }

    private static ProcessStartInfo CreateConfiguredStart(string variable, string value) =>
        CreateConfiguredStart(variable, value, "ping 127.0.0.1 -n 2 > nul");

    private static ProcessStartInfo CreateConfiguredStart(string variable, string value, string command)
    {
        ProcessStartInfo configured = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false
        };
        configured.ArgumentList.Add("/d");
        configured.ArgumentList.Add("/c");
        configured.ArgumentList.Add(command);
        configured.Environment[variable] = value;
        return configured;
    }

    private static Task<BrokerObservation> RunFakeBrokerAsync(string pipeName) =>
        RunFakeBrokerAsync(pipeName, waitForChildExit: false);

    private static async Task<BrokerObservation> RunFakeBrokerAsync(string pipeName, bool waitForChildExit)
    {
        using NamedPipeClientStream pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        using StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
        using StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        BrokerRequest request = JsonSerializer.Deserialize<BrokerRequest>(
            await reader.ReadLineAsync() ?? string.Empty,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
            throw new InvalidDataException("Missing test broker request.");
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
        Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start test child.");
        int processId = process.Id;
        if (waitForChildExit)
            await process.WaitForExitAsync();
        process.Dispose();
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { processId }));
        return new BrokerObservation(request, processId);
    }

    private sealed record BrokerObservation(BrokerRequest Request, int ProcessId);

    private sealed class BrokerRequest
    {
        public string Executable { get; init; } = string.Empty;
        public string WorkingDirectory { get; init; } = string.Empty;
        public string[] Arguments { get; init; } = Array.Empty<string>();
        public Dictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new NoopDisposable();

        public void Dispose()
        {
        }
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
        }
    }
}
