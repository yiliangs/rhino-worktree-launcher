using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task Line_runner_reports_standard_output_when_standard_error_is_empty()
    {
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProcessRunner.RunLinesAsync(
                "pwsh",
                Environment.CurrentDirectory,
                new[] { "-NoProfile", "-Command", "Write-Output 'actionable build failure'; exit 1" },
                _ => Task.CompletedTask,
                CancellationToken.None));

        Assert.Contains("actionable build failure", exception.Message, StringComparison.Ordinal);
    }
}
