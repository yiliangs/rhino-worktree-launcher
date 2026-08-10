namespace RhinoWorktreeLauncher.Tests;

public sealed class BuildOptionDiscoveryResponsivenessTests
{
    [Fact]
    public async Task Build_option_discovery_returns_control_while_the_scan_is_pending()
    {
        using ManualResetEventSlim discoveryStarted = new ManualResetEventSlim();
        using ManualResetEventSlim releaseDiscovery = new ManualResetEventSlim();
        using ManualResetEventSlim callReturned = new ManualResetEventSlim();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            ProjectBuildOptionsDiscovery = _ =>
            {
                discoveryStarted.Set();
                releaseDiscovery.Wait(TimeSpan.FromSeconds(10));
                return new ProjectBuildOptions(Array.Empty<PluginBuildOptions>());
            }
        });
        Task<CommandResult<ProjectBuildOptions>>? operation = null;
        Exception? callerException = null;
        Thread caller = new Thread(() =>
        {
            try
            {
                operation = backend.DiscoverProjectBuildOptionsAsync(
                    "unused-by-test-discovery",
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                callerException = exception;
            }
            finally
            {
                callReturned.Set();
            }
        });
        caller.Start();

        bool returnedBeforeDiscoveryFinished;
        try
        {
            Assert.True(discoveryStarted.Wait(TimeSpan.FromSeconds(5)), "Build option discovery did not start.");
            returnedBeforeDiscoveryFinished = callReturned.Wait(TimeSpan.FromSeconds(1));
        }
        finally
        {
            releaseDiscovery.Set();
        }

        Assert.True(caller.Join(TimeSpan.FromSeconds(5)), "The discovery caller thread did not finish.");
        Assert.Null(callerException);
        Assert.True(
            returnedBeforeDiscoveryFinished,
            "DiscoverProjectBuildOptionsAsync blocked its caller until the filesystem scan completed.");
        Assert.NotNull(operation);
        Assert.True((await operation).Succeeded);
    }
}
