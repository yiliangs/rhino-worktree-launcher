using System.Collections.Concurrent;
using System.Diagnostics;
using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class ShellRhinoProcessStarterTests
{
    [Fact]
    public void Shell_start_inherits_overrides_and_restores_the_launcher_environment()
    {
        string variable = "RWL_TEST_" + Guid.NewGuid().ToString("N");
        ProcessStartInfo configured = CreateConfiguredStart(variable, "selected-worktree");
        string? inheritedValue = null;
        ProcessStartInfo? shellStart = null;

        using Process process = ShellRhinoProcessStarter.Start(configured, startInfo =>
        {
            shellStart = startInfo;
            inheritedValue = Environment.GetEnvironmentVariable(variable);
            return StartCompletedProcess();
        });

        Assert.True(shellStart!.UseShellExecute);
        Assert.Equal("selected-worktree", inheritedValue);
        Assert.Null(Environment.GetEnvironmentVariable(variable));
    }

    [Fact]
    public async Task Concurrent_shell_starts_do_not_cross_contaminate_environment()
    {
        string variable = "RWL_TEST_" + Guid.NewGuid().ToString("N");
        ConcurrentBag<string?> inheritedValues = new ConcurrentBag<string?>();
        int activeStarts = 0;
        int maximumActiveStarts = 0;

        Task StartAsync(string value) => Task.Run(() =>
        {
            using Process process = ShellRhinoProcessStarter.Start(
                CreateConfiguredStart(variable, value),
                startInfo =>
                {
                    _ = startInfo;
                    int active = Interlocked.Increment(ref activeStarts);
                    InterlockedExtensions.Max(ref maximumActiveStarts, active);
                    inheritedValues.Add(Environment.GetEnvironmentVariable(variable));
                    Thread.Sleep(100);
                    Interlocked.Decrement(ref activeStarts);
                    return StartCompletedProcess();
                });
        });

        await Task.WhenAll(StartAsync("first"), StartAsync("second"));

        Assert.Equal(1, maximumActiveStarts);
        Assert.Equal(new[] { "first", "second" }, inheritedValues.OrderBy(value => value).ToArray());
        Assert.Null(Environment.GetEnvironmentVariable(variable));
    }

    private static ProcessStartInfo CreateConfiguredStart(string variable, string value)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "fake-rhino.exe",
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("/nosplash");
        startInfo.Environment[variable] = value;
        return startInfo;
    }

    private static Process StartCompletedProcess() => Process.Start(new ProcessStartInfo
    {
        FileName = "cmd.exe",
        ArgumentList = { "/c", "exit", "0" },
        UseShellExecute = false,
        CreateNoWindow = true
    })!;
}

internal static class InterlockedExtensions
{
    public static void Max(ref int location, int value)
    {
        int current;
        do
        {
            current = location;
            if (current >= value)
                return;
        }
        while (Interlocked.CompareExchange(ref location, value, current) != current);
    }
}
