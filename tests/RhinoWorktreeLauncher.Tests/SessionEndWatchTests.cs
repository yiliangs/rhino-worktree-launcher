namespace RhinoWorktreeLauncher.Tests;

public sealed class SessionEndWatchTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task A_session_signal_stops_the_host()
    {
        TaskCompletionSource<SessionEnd> signal = new TaskCompletionSource<SessionEnd>();
        SessionEnd? stopped = null;
        SessionEndWatch watch = SessionEndWatch.Start(
            new[] { signal.Task },
            Grace,
            (end, _) =>
            {
                stopped = end;
                return Task.CompletedTask;
            },
            (_, reason) => Assert.Fail($"The host stopped, so the process must not be abandoned: {reason}"));

        signal.SetResult(new SessionEnd("session_standard_input_closed", "The client closed its end."));
        await watch.Watching;

        Assert.Equal("session_standard_input_closed", stopped!.Code);
    }

    // The bound is the point. A launch in flight is owned by the detached executor, so a host
    // that will not stop must not keep an unreachable server alive waiting for it.
    [Fact]
    public async Task A_host_that_does_not_stop_within_the_grace_ends_the_process()
    {
        string? abandoned = null;
        SessionEndWatch watch = SessionEndWatch.Start(
            new[] { Task.FromResult(new SessionEnd("session_parent_exited", "The parent is gone.")) },
            Grace,
            (_, _) => new TaskCompletionSource().Task,
            (_, reason) => abandoned = reason);

        await watch.Watching;

        Assert.Contains("did not stop", abandoned!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_that_fails_to_stop_ends_the_process_and_names_the_failure()
    {
        string? abandoned = null;
        SessionEndWatch watch = SessionEndWatch.Start(
            new[] { Task.FromResult(new SessionEnd("session_parent_exited", "The parent is gone.")) },
            Grace,
            (_, _) => throw new InvalidOperationException("The transport is already disposed."),
            (_, reason) => abandoned = reason);

        await watch.Watching;

        Assert.Contains("The transport is already disposed.", abandoned!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_live_session_is_left_running()
    {
        SessionEndWatch watch = SessionEndWatch.Start(
            new[] { new TaskCompletionSource<SessionEnd>().Task },
            Grace,
            (_, _) => throw new InvalidOperationException("A live session must not be stopped."),
            (_, _) => Assert.Fail("A live session must not be abandoned."));

        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.False(watch.Watching.IsCompleted);
    }

    // With nothing to watch there is nothing to decide. SessionEndSignals has already
    // reported each signal it could not establish, so the watch does not report it again.
    [Fact]
    public async Task A_watch_with_no_signal_completes_without_stopping_anything()
    {
        SessionEndWatch watch = SessionEndWatch.Start(
            Array.Empty<Task<SessionEnd>>(),
            Grace,
            (_, _) => throw new InvalidOperationException("Nothing signalled the end of the session."),
            (_, _) => Assert.Fail("Nothing signalled the end of the session."));

        await watch.Watching;

        Assert.True(watch.Watching.IsCompletedSuccessfully);
    }
}
