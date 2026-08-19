using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RhinoWorktreeLauncher;

/// <summary>
/// The observable ends of a stdio session. Two are watched, because either one alone has a
/// hole: standard input can be bridged by an intermediate process that is itself killed, and
/// a parent can outlive the client whose streams it forwards.
/// </summary>
internal static class SessionEndSignals
{
    public const string StandardInputClosed = "session_standard_input_closed";
    public const string ParentExited = "session_parent_exited";

    private static readonly TimeSpan InputPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Every signal that can be established for this process. One that cannot be established
    /// is reported and left out, so a missing signal is visible rather than silent.
    /// </summary>
    public static IReadOnlyList<Task<SessionEnd>> ForCurrentProcess(Action<string> report)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Session-end signals require Windows.");

        List<Task<SessionEnd>> signals = new List<Task<SessionEnd>>();
        Task<SessionEnd>? input = WatchStandardInputAsync(report);
        if (input is not null)
            signals.Add(input);
        Task<SessionEnd>? parent = WatchParentAsync(report);
        if (parent is not null)
            signals.Add(parent);
        if (signals.Count == 0)
        {
            report(
                "[session_watch_unavailable] Neither this server's standard input nor its parent " +
                "process can be watched, so nothing will end it when its session ends. Close it " +
                "from Task Manager if it outlives its client, and run 'rwl doctor' to list the " +
                "RWL processes that are running.");
        }
        return signals;
    }

    /// <summary>
    /// Standard input, watched without reading it. The transport owns the stream, so this
    /// peeks at the pipe instead: a peek that fails with a broken pipe means every writer has
    /// closed, which is exactly the end of stream the transport is waiting for and is
    /// observable even if the transport never acts on it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static Task<SessionEnd>? WatchStandardInputAsync(Action<string> report)
    {
        IntPtr handle = GetStdHandle(StandardInputHandle);
        if (handle == IntPtr.Zero || handle == InvalidHandleValue)
        {
            report(
                "[session_standard_input_unwatchable] This server has no standard input handle, " +
                "so the end of its client's stream cannot be observed.");
            return null;
        }
        if (GetFileType(handle) != FileTypePipe)
        {
            // A console or a file has no writer whose disappearance means anything. The
            // parent signal is the one that decides such a session.
            report(
                "[session_standard_input_not_a_pipe] This server's standard input is not a pipe, " +
                "so only its parent process is watched for the end of the session.");
            return null;
        }
        return Task.Run(() => PollStandardInputAsync(handle, report));
    }

    [SupportedOSPlatform("windows")]
    private static async Task<SessionEnd> PollStandardInputAsync(IntPtr handle, Action<string> report)
    {
        bool reportedUnknownError = false;
        while (true)
        {
            if (!PeekNamedPipe(handle, IntPtr.Zero, 0, IntPtr.Zero, out _, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                if (error is BrokenPipe or InvalidHandle or PipeNotConnected)
                {
                    return new SessionEnd(
                        StandardInputClosed,
                        "The client that started this server closed its standard input, so the " +
                        "session is over and the server is shutting down.");
                }
                if (!reportedUnknownError)
                {
                    // Reported once, then watched on: an unrecognised error is not proof the
                    // session ended, and ending a live server on one would be worse than
                    // leaving the parent signal to decide.
                    reportedUnknownError = true;
                    report(
                        $"[session_standard_input_unreadable] Peeking at this server's standard " +
                        $"input failed with Windows error {error}. The session end is now decided " +
                        "by the parent process alone.");
                }
            }
            await Task.Delay(InputPollInterval).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The parent process. This is what the 2026-08-18 orphan needed: its bridging parent was
    /// gone while the server itself kept running, and nothing was watching for that.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static Task<SessionEnd>? WatchParentAsync(Action<string> report)
    {
        RunningProcess? parent;
        try
        {
            parent = ProcessSnapshot.ParentOf(ProcessSnapshot.Read(), Environment.ProcessId);
        }
        catch (Exception exception)
        {
            report(
                $"[session_parent_unresolvable] This server's parent process could not be " +
                $"resolved ({exception.Message}), so only its standard input is watched for the " +
                "end of the session.");
            return null;
        }
        if (parent is null)
        {
            return Task.FromResult(new SessionEnd(
                ParentExited,
                "The process that started this server is already gone, so the session it was " +
                "started for is over and the server is shutting down."));
        }

        try
        {
            return WaitAsync(Process.GetProcessById(parent.ProcessId), parent.ProcessId);
        }
        // The parent exited between the snapshot and the open, which is the condition this
        // signal exists to report.
        catch (ArgumentException)
        {
            return Task.FromResult(new SessionEnd(ParentExited, ParentEndedMessage(parent.ProcessId)));
        }
        catch (Exception exception)
        {
            report(
                $"[session_parent_unwatchable] Process {parent.ProcessId}, which started this " +
                $"server, could not be watched ({exception.Message}), so only standard input is " +
                "watched for the end of the session.");
            return null;
        }
    }

    private static async Task<SessionEnd> WaitAsync(Process parent, int parentProcessId)
    {
        using (parent)
        {
            await parent.WaitForExitAsync().ConfigureAwait(false);
            return new SessionEnd(ParentExited, ParentEndedMessage(parentProcessId));
        }
    }

    private static string ParentEndedMessage(int parentProcessId) =>
        $"Process {parentProcessId}, which started this server and bridged its standard " +
        "streams, has exited, so the session is over and the server is shutting down.";

    private const int StandardInputHandle = -10;
    private const uint FileTypePipe = 0x0003;
    private const int BrokenPipe = 109;
    private const int InvalidHandle = 6;
    private const int PipeNotConnected = 233;
    private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PeekNamedPipe(
        IntPtr pipe,
        IntPtr buffer,
        uint bufferSize,
        IntPtr bytesRead,
        out uint bytesAvailable,
        IntPtr bytesLeft);
}
