namespace RhinoWorktreeLauncher;

internal static class ProcessLaunchGate
{
    private static readonly object SyncRoot = new object();

    public static T Start<T>(Func<T> start)
    {
        lock (SyncRoot)
            return start();
    }
}
