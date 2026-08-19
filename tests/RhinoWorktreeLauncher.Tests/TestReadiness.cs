using Rwl.Protocol;

namespace RhinoWorktreeLauncher.Tests;

// Launch readiness for tests that are about something else. The real probe spawns an
// executor through the interactive Windows shell, which no test may do.
internal static class TestReadiness
{
    public static LaunchHostReadiness Ready { get; } = new LaunchHostReadiness(
        _ => Task.FromResult(new LaunchHostState(
            true,
            LaunchExecutorCodes.InteractiveSpawnReady,
            "A test executor answered.")));

    public static LaunchHostReadiness Degraded(string code, string message) => new LaunchHostReadiness(
        _ => Task.FromResult(new LaunchHostState(false, code, message)));
}
