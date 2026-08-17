namespace RhinoWorktreeLauncher;

// Process creation is not success. Rhino can already be gone by the time RWL learns its
// PID, and the .NET handle lookup reports that as a bare ArgumentException about an id
// that is not running. This names the condition instead, so the startup race and an exit
// observed during verification reach the caller as one launch diagnostic. The PID stays
// valid information in both cases; only the live handle is unavailable.
internal sealed class RhinoExitedBeforeVerificationException : Exception
{
    public RhinoExitedBeforeVerificationException(
        int processId,
        string message,
        Exception? innerException = null)
        : base(message, innerException) => ProcessId = processId;

    public int ProcessId { get; }
}
