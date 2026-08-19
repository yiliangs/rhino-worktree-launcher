using Microsoft.Win32;
using Rwl.Protocol;
using System.Runtime.Versioning;

namespace RhinoWorktreeLauncher.Tests;

// The canary and the shipped probe that answers it. Everything here writes only under RWL's
// own key, never under a McNeel one.
[SupportedOSPlatform("windows")]
public sealed class RegistryVisibilityTests
{
    [Fact]
    public async Task A_write_an_independent_reader_confirms_is_visible()
    {
        if (!OperatingSystem.IsWindows())
            return;

        RegistryVisibility visibility = await RegistryVisibilityCanary.VerifyAsync(
            TestRegistryProbe.Truthful,
            spawnInteractively: false,
            CancellationToken.None);

        Assert.True(visibility.Visible, visibility.Describe());
        Assert.Equal(visibility.Expected, visibility.Observed);
    }

    [Fact]
    public async Task A_write_no_independent_reader_can_see_is_not_visible_and_says_why()
    {
        if (!OperatingSystem.IsWindows())
            return;

        RegistryVisibility visibility = await RegistryVisibilityCanary.VerifyAsync(
            TestRegistryProbe.Blind,
            spawnInteractively: false,
            CancellationToken.None);

        Assert.False(visibility.Visible);
        Assert.Null(visibility.Observed);
        Assert.Contains("intercepted", visibility.Describe(), StringComparison.Ordinal);
    }

    // The canary key is RWL's own and never survives the check.
    [Fact]
    public async Task The_canary_removes_the_key_it_wrote()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string? probedKeyPath = null;
        RegistryProbeRunner recording = (request, _, _) =>
        {
            probedKeyPath = request.KeyPath;
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(request.KeyPath, writable: false);
            return Task.FromResult(new RegistryProbeResult
            {
                Exists = key is not null,
                Values = request.Values.ToDictionary(
                    name => name,
                    name => key?.GetValue(name)?.ToString())
            });
        };

        RegistryVisibility visibility = await RegistryVisibilityCanary.VerifyAsync(
            recording,
            spawnInteractively: false,
            CancellationToken.None);

        Assert.True(visibility.Visible);
        Assert.StartsWith(@"Software\RhinoWorktreeLauncher\canary\", probedKeyPath!, StringComparison.Ordinal);
        Assert.Null(Registry.CurrentUser.OpenSubKey(probedKeyPath!, writable: false));
    }

    // The shipped probe, in its own process, reading a key this process wrote. This is the
    // property the whole check rests on: the reader shares nothing with the writer.
    [Fact]
    public async Task The_built_probe_reads_a_key_another_process_wrote()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string keyPath = $@"Software\RhinoWorktreeLauncherTests\probe\{Guid.NewGuid():N}";
        string nonce = Guid.NewGuid().ToString("N");
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)!)
            key.SetValue("Nonce", nonce, RegistryValueKind.String);
        try
        {
            RegistryProbeResult observed = await TestRegistryProbe.BootstrapAsync(
                new RegistryProbeRequest
                {
                    Hive = RegistryHives.CurrentUser,
                    KeyPath = keyPath,
                    Values = new[] { "Nonce", "Absent" }
                },
                spawnInteractively: false,
                CancellationToken.None);

            Assert.Null(observed.Error);
            Assert.True(observed.Exists);
            Assert.Equal(nonce, observed.Value("Nonce"));
            Assert.Null(observed.Value("Absent"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task The_built_probe_reports_a_key_that_is_not_there()
    {
        if (!OperatingSystem.IsWindows())
            return;

        RegistryProbeResult observed = await TestRegistryProbe.BootstrapAsync(
            new RegistryProbeRequest
            {
                Hive = RegistryHives.CurrentUser,
                KeyPath = $@"Software\RhinoWorktreeLauncherTests\absent\{Guid.NewGuid():N}",
                Values = new[] { "Nonce" }
            },
            spawnInteractively: false,
            CancellationToken.None);

        Assert.Null(observed.Error);
        Assert.False(observed.Exists);
        Assert.Null(observed.Value("Nonce"));
    }

    [Fact]
    public async Task Doctor_passes_when_a_current_user_write_is_visible_to_another_process()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        LauncherBackend backend = Backend(temporary, TestRegistryProbe.Truthful);

        CommandResult<DoctorReport> result = await backend.RunDoctorAsync(CancellationToken.None);

        DoctorCheck check = Assert.Single(
            result.Value!.Checks,
            candidate => candidate.Name == "registry-visibility");
        Assert.True(check.Passed, check.Message);
    }

    // The condition that made a launch hang for three minutes with nothing to read is a
    // failed doctor check with the reason in it.
    [Fact]
    public async Task Doctor_fails_when_a_current_user_write_reaches_nobody_else()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        LauncherBackend backend = Backend(temporary, TestRegistryProbe.Blind);

        CommandResult<DoctorReport> result = await backend.RunDoctorAsync(CancellationToken.None);

        DoctorCheck check = Assert.Single(
            result.Value!.Checks,
            candidate => candidate.Name == "registry-visibility");
        Assert.False(check.Passed);
        Assert.Equal(DiagnosticSeverity.Error, check.Severity);
        Assert.False(result.Value.Healthy);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "doctor_registry-visibility_failed");
    }

    // Without an installed bootstrap there is no independent reader to ask. That is a
    // warning that the check could not run, not a claim that it failed.
    [Fact]
    public async Task Doctor_says_it_could_not_check_when_no_independent_reader_can_be_started()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        LauncherBackend backend = Backend(
            temporary,
            (_, _, _) => throw new LaunchDiagnosticException(
                LaunchExecutorCodes.InteractiveSpawnUnavailable,
                "no bootstrap"));

        CommandResult<DoctorReport> result = await backend.RunDoctorAsync(CancellationToken.None);

        DoctorCheck check = Assert.Single(
            result.Value!.Checks,
            candidate => candidate.Name == "registry-visibility");
        Assert.False(check.Passed);
        Assert.Equal(DiagnosticSeverity.Warning, check.Severity);
        Assert.Contains(LaunchExecutorCodes.InteractiveSpawnUnavailable, check.Message, StringComparison.Ordinal);
    }

    private static LauncherBackend Backend(TemporaryDirectory temporary, RegistryProbeRunner probe) =>
        new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LocksDirectory = temporary.PathFor("launcher/locks"),
            RegistryProbeRunner = probe
        });
}
