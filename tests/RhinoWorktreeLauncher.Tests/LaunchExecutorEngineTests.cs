using Microsoft.Win32;
using Rwl.Protocol;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace RhinoWorktreeLauncher.Tests;

// The executor's own choreography, run in process against isolated registry roots and a
// stand-in for Rhino: a process that holds the artifact mapped in its address space, which
// is exactly the state file-use attribution looks for (ADR 0002).
[SupportedOSPlatform("windows")]
public sealed class LaunchExecutorEngineTests
{
    [Fact]
    public async Task A_verified_launch_restores_the_registration_and_keeps_the_journal()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        using StandInRhino rhino = new StandInRhino(sandbox);
        LaunchExecutorRequest request = Request(sandbox);

        LaunchExecutorEvent result = await RunAsync(sandbox, rhino, request);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(LaunchExecutorCodes.LaunchVerified, result.Code);
        Assert.Equal(rhino.ProcessId, result.RhinoProcessId);
        Assert.NotNull(result.ExecutorLogPath);
        Assert.Contains(LaunchExecutorCodes.PluginRegistrationSeeded, ReadWhileOpen(result.ExecutorLogPath!));
        // The seed is gone the moment the launch is verified, and the journal stays behind
        // for the correction that follows Rhino's exit.
        Assert.Null(sandbox.OpenUserRegistration(request.PluginGuid()));
        Assert.True(File.Exists(sandbox.JournalPathFor(request.PluginGuid())));
    }

    [Fact]
    public async Task A_verified_launch_seeds_the_registration_while_rhino_is_starting()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        using StandInRhino rhino = new StandInRhino(sandbox);
        LaunchExecutorRequest request = Request(sandbox);
        string? seededFileName = null;

        LaunchExecutorEvent result = await RunAsync(
            sandbox,
            rhino,
            request,
            observe: value =>
            {
                if (value.Code != LaunchExecutorCodes.RhinoStarted)
                    return;
                using RegistryKey? seed = sandbox.OpenUserRegistration(request.PluginGuid());
                seededFileName = seed?.GetValue("FileName") as string;
            });

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(request.PluginPath, seededFileName);
    }

    // Concurrent launches leave several Rhino processes running, each a different build, so
    // the launched process carries its own launch identity. Nothing reads it back from
    // outside: this is what lets code inside Rhino answer the question for itself.
    [Fact]
    public async Task A_launch_stamps_its_identity_on_the_rhino_it_starts_and_records_that()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        using StandInRhino rhino = new StandInRhino(sandbox);
        LaunchExecutorRequest request = Request(sandbox);
        string? identityInsideTheProcess = null;

        LaunchExecutorEvent result = await RunAsync(
            sandbox,
            rhino,
            request,
            observe: value =>
            {
                if (value.Code == LaunchExecutorCodes.RhinoStarted)
                    identityInsideTheProcess = rhino.ReadFirstOutputLine();
            });

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal($"launch-id={request.LaunchId}", identityInsideTheProcess);
        Assert.Equal(
            request.LaunchId,
            rhino.Requested!.Environment[LaunchIdentity.LaunchIdVariable]);
        Assert.Equal(
            request.PluginPath,
            rhino.Requested.Environment[LaunchIdentity.ArtifactVariable]);
        Assert.Contains(
            LaunchExecutorCodes.RhinoIdentityStamped,
            ReadWhileOpen(result.ExecutorLogPath!));
    }

    // The caller's variables ride the same start request as the identity variables. This is
    // the whole mechanism behind entering an in-Rhino harness through an ordinary launch:
    // the harness arms on one environment read inside the launched process.
    [Fact]
    public async Task A_launch_injects_the_requested_environment_into_the_rhino_it_starts()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        using StandInRhino rhino = new StandInRhino(sandbox);
        LaunchExecutorRequest request = Request(sandbox) with
        {
            Environment = new Dictionary<string, string> { ["NATALIE_SUITE_REPRO"] = "1" }
        };

        LaunchExecutorEvent result = await RunAsync(sandbox, rhino, request);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("1", rhino.Requested!.Environment["NATALIE_SUITE_REPRO"]);
        // The identity variables still ride beside the caller's.
        Assert.Equal(request.LaunchId, rhino.Requested.Environment[LaunchIdentity.LaunchIdVariable]);
        Assert.Contains("NATALIE_SUITE_REPRO", ReadWhileOpen(result.ExecutorLogPath!));
    }

    // A caller must not be able to spoof the launch identity, and the refusal happens
    // before Rhino starts, by name, on both sides of the pipe.
    [Fact]
    public async Task A_reserved_environment_name_ends_the_launch_as_an_invalid_request()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        using StandInRhino rhino = new StandInRhino(sandbox);
        LaunchExecutorRequest request = Request(sandbox) with
        {
            Environment = new Dictionary<string, string> { ["RWL_ARTIFACT"] = @"C:\spoof.rhp" }
        };

        LaunchExecutorEvent result = await RunAsync(sandbox, rhino, request);

        Assert.False(result.Succeeded);
        Assert.Equal(LaunchExecutorCodes.ExecutorRequestInvalid, result.Code);
        Assert.Null(rhino.ProcessId);
    }

    [Fact]
    public async Task A_competing_machine_registration_ends_the_launch_before_rhino_starts()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        using StandInRhino rhino = new StandInRhino(sandbox);
        LaunchExecutorRequest request = Request(sandbox);
        LaunchExecutorEvent result = await RunAsync(
            sandbox,
            rhino,
            request,
            pluginNamespace: new RefusingPluginNamespace(new PluginRegistrationConflict(
                @"C:\primary\Sample.rhp",
                @"HKEY_LOCAL_MACHINE\Software\McNeel\Rhinoceros\8.0\Plug-ins\{id}")));

        Assert.False(result.Succeeded);
        Assert.Equal(LaunchExecutorCodes.PluginRegistrationConflict, result.Code);
        Assert.Contains(@"C:\primary\Sample.rhp", result.Message, StringComparison.Ordinal);
        Assert.Null(rhino.ProcessId);
    }

    // The host dying mid-launch is the case the on-disk journal alone recovers late. The
    // executor sees the broken pipe, restores immediately, and takes the unverified Rhino
    // with it.
    [Fact]
    public async Task A_disconnected_client_restores_the_registration_and_ends_the_unverified_rhino()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        using StandInRhino rhino = new StandInRhino(sandbox, mapArtifact: false);
        LaunchExecutorRequest request = Request(sandbox);
        using CancellationTokenSource disconnected = new CancellationTokenSource();

        LaunchExecutorEvent result = await RunAsync(
            sandbox,
            rhino,
            request,
            clientDisconnected: disconnected.Token,
            observe: value =>
            {
                if (value.Code == LaunchExecutorCodes.RhinoStarted)
                    disconnected.Cancel();
            });

        Assert.False(result.Succeeded);
        Assert.Equal(LaunchExecutorCodes.ExecutorClientDisconnected, result.Code);
        Assert.True(StandInRhino.HasExited(rhino.ProcessId!.Value));
        Assert.Null(sandbox.OpenUserRegistration(request.PluginGuid()));
        Assert.False(File.Exists(sandbox.JournalPathFor(request.PluginGuid())));
    }

    [Fact]
    public async Task A_rhino_that_exits_before_verification_fails_with_its_own_diagnostic()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        using StandInRhino rhino = new StandInRhino(sandbox, mapArtifact: false, exitImmediately: true);
        LaunchExecutorRequest request = Request(sandbox);

        LaunchExecutorEvent result = await RunAsync(sandbox, rhino, request);

        Assert.False(result.Succeeded);
        Assert.Equal(LaunchExecutorCodes.RhinoExitedBeforeVerification, result.Code);
        Assert.Null(sandbox.OpenUserRegistration(request.PluginGuid()));
        Assert.False(File.Exists(sandbox.JournalPathFor(request.PluginGuid())));
    }

    // A launch queued behind another session ends by name, stating who held the lock, never
    // as an unexplained timeout.
    [Fact]
    public async Task A_launch_queued_behind_another_ends_as_a_lease_wait_timeout_naming_the_holder()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        using StandInRhino rhino = new StandInRhino(sandbox);
        LaunchExecutorRequest request = Request(sandbox) with { TimeoutSeconds = 2 };
        PluginNamespaceLeaseResult held = await sandbox.AcquireAsync(
            request.PluginPath,
            request.PluginGuid(),
            CancellationToken.None);

        LaunchExecutorEvent result = await RunAsync(sandbox, rhino, request);

        Assert.False(result.Succeeded);
        Assert.Equal(LaunchExecutorCodes.LeaseWaitTimeout, result.Code);
        Assert.Contains("test-launch", result.Message, StringComparison.Ordinal);
        Assert.Null(rhino.ProcessId);
        held.Lease!.Dispose();
    }

    // The proven failure this whole architecture exists for: the writer reads its own seed
    // back and sees it, while the hive Rhino reads never received it. The launch stops
    // before Rhino starts instead of running to the verification timeout.
    [Fact]
    public async Task A_seed_no_independent_reader_can_see_stops_the_launch_before_rhino_starts()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        using StandInRhino rhino = new StandInRhino(sandbox);
        LaunchExecutorRequest request = Request(sandbox);

        LaunchExecutorEvent result = await RunAsync(sandbox, rhino, request, probe: TestRegistryProbe.Blind);

        Assert.False(result.Succeeded);
        Assert.Equal(LaunchExecutorCodes.RegistrySeedNotVisible, result.Code);
        Assert.Contains("intercepted", result.Message, StringComparison.Ordinal);
        Assert.Null(rhino.ProcessId);
        Assert.Null(sandbox.OpenUserRegistration(request.PluginGuid()));
        Assert.False(File.Exists(sandbox.JournalPathFor(request.PluginGuid())));
    }

    [Fact]
    public async Task A_probe_that_cannot_answer_stops_the_launch_before_rhino_starts()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        using StandInRhino rhino = new StandInRhino(sandbox);
        LaunchExecutorRequest request = Request(sandbox);

        LaunchExecutorEvent result = await RunAsync(
            sandbox,
            rhino,
            request,
            probe: TestRegistryProbe.Failing("no probe answered"));

        Assert.False(result.Succeeded);
        Assert.Equal(LaunchExecutorCodes.RegistrySeedNotVisible, result.Code);
        Assert.Contains("no probe answered", result.Message, StringComparison.Ordinal);
        Assert.Null(rhino.ProcessId);
    }

    // A verified launch confirms the seed before Rhino starts and removes the nonce it
    // checked, so Rhino reads exactly the documented install seed.
    [Fact]
    public async Task A_confirmed_seed_is_recorded_and_leaves_no_nonce_behind()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        using StandInRhino rhino = new StandInRhino(sandbox);
        LaunchExecutorRequest request = Request(sandbox);
        string? nonceWhenRhinoStarted = "unread";

        LaunchExecutorEvent result = await RunAsync(
            sandbox,
            rhino,
            request,
            observe: value =>
            {
                if (value.Code != LaunchExecutorCodes.RhinoStarted)
                    return;
                using RegistryKey? seed = sandbox.OpenUserRegistration(request.PluginGuid());
                nonceWhenRhinoStarted = seed?.GetValue(RegistryVisibilityCanary.NonceValue) as string;
            });

        Assert.True(result.Succeeded, result.Message);
        Assert.Null(nonceWhenRhinoStarted);
        Assert.Contains(
            LaunchExecutorCodes.RegistrySeedVerified,
            ReadWhileOpen(result.ExecutorLogPath!));
    }

    [Fact]
    public async Task An_unreadable_plugin_id_ends_the_launch_as_an_invalid_request()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        using StandInRhino rhino = new StandInRhino(sandbox);
        LaunchExecutorRequest request = Request(sandbox) with { PluginId = "not-a-guid" };

        LaunchExecutorEvent result = await RunAsync(sandbox, rhino, request);

        Assert.False(result.Succeeded);
        Assert.Equal(LaunchExecutorCodes.ExecutorRequestInvalid, result.Code);
        Assert.Null(rhino.ProcessId);
    }

    // The whole reason the executor lingers: Rhino writes the artifact it loaded back into
    // its registration after the launch has already restored and answered.
    [Fact]
    public async Task The_executor_corrects_a_registration_rhino_rewrote_after_the_launch_returned()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        using StandInRhino rhino = new StandInRhino(sandbox);
        LaunchExecutorRequest request = Request(sandbox);
        LaunchExecutorEngine engine = new LaunchExecutorEngine(new LaunchExecutorOptions
        {
            PluginNamespace = sandbox,
            RegistryProbeRunner = TestRegistryProbe.Truthful,
            RhinoProcessStarter = rhino.Start,
            FileUsePollDelay = TimeSpan.FromMilliseconds(50)
        });
        using ExecutorLog log = new ExecutorLog(ExecutorLog.PathFor(request));
        LaunchExecutorEvent result = await engine.RunAsync(
            request,
            new ImmediateProgress<LaunchExecutorEvent>(_ => { }),
            log,
            CancellationToken.None,
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);

        // Rhino, running elevated, records the file it actually loaded.
        using (RegistryKey rewritten = sandbox.CreateMachineRegistration(request.PluginGuid()))
        {
            using RegistryKey plugin = rewritten.CreateSubKey("PlugIn", writable: true)!;
            plugin.SetValue("FileName", request.PluginPath, RegistryValueKind.String);
        }
        rhino.Kill();

        RegistrationDrift drift = await engine.CorrectAfterExitAsync(
            request,
            result.RhinoProcessId,
            log,
            CancellationToken.None);

        Assert.True(drift.MachineDrifted);
        Assert.Null(sandbox.OpenMachineRegistration(request.PluginGuid()));
        Assert.False(File.Exists(sandbox.JournalPathFor(request.PluginGuid())));
        Assert.Contains(LaunchExecutorCodes.RegistrationWriteBackCorrected, ReadWhileOpen(log.Path));
    }

    // Switching the standing registration is a registry mutation, so it runs in the executor
    // and is proven by an independent reader before it is reported, exactly as a launch seed
    // is (ADR 0015, 0016).
    [Fact]
    public async Task A_registration_switch_an_independent_reader_confirms_is_reported_as_switched()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        LaunchExecutorRequest request = Request(sandbox) with
        {
            Mode = LaunchExecutorMode.SetRegistration
        };

        LaunchExecutorEvent result = await SwitchAsync(sandbox, request, TestRegistryProbe.Truthful);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(LaunchExecutorCodes.PluginRegistrationSwitched, result.Code);
        Assert.Contains(request.PluginPath, result.Message, StringComparison.Ordinal);
        Assert.Equal(RegistryHives.CurrentUser, result.RegistryHive);
        Assert.Null(result.PreviousRegisteredPath);
        using RegistryKey registered = sandbox.OpenUserRegistration(request.PluginGuid())!;
        Assert.Equal(request.PluginPath, registered.GetValue("FileName"));
    }

    [Fact]
    public async Task A_registration_switch_no_independent_reader_confirms_says_what_that_reader_saw()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using RegistrySandbox sandbox = new RegistrySandbox();
        LaunchExecutorRequest request = Request(sandbox) with
        {
            Mode = LaunchExecutorMode.SetRegistration
        };
        RegistryProbeRunner disagreeing = (probed, _, _) => Task.FromResult(new RegistryProbeResult
        {
            Exists = true,
            Values = probed.Values.ToDictionary(
                name => name,
                _ => (string?)@"C:\primary\Sample.rhp")
        });

        LaunchExecutorEvent result = await SwitchAsync(sandbox, request, disagreeing);

        Assert.False(result.Succeeded);
        Assert.Equal(LaunchExecutorCodes.PluginRegistrationNotVisible, result.Code);
        Assert.Contains(@"C:\primary\Sample.rhp", result.Message, StringComparison.Ordinal);
    }

    private static async Task<LaunchExecutorEvent> SwitchAsync(
        RegistrySandbox sandbox,
        LaunchExecutorRequest request,
        RegistryProbeRunner probe)
    {
        LaunchExecutorEngine engine = new LaunchExecutorEngine(new LaunchExecutorOptions
        {
            PluginNamespace = sandbox,
            RegistryProbeRunner = probe
        });
        using ExecutorLog log = new ExecutorLog(ExecutorLog.PathFor(request));
        return await engine.SwitchRegistrationAsync(
            request,
            new ImmediateProgress<LaunchExecutorEvent>(_ => { }),
            log,
            CancellationToken.None,
            CancellationToken.None);
    }

    private static async Task<LaunchExecutorEvent> RunAsync(
        RegistrySandbox sandbox,
        StandInRhino rhino,
        LaunchExecutorRequest request,
        IPluginNamespace? pluginNamespace = null,
        RegistryProbeRunner? probe = null,
        CancellationToken clientDisconnected = default,
        Action<LaunchExecutorEvent>? observe = null)
    {
        LaunchExecutorEngine engine = new LaunchExecutorEngine(new LaunchExecutorOptions
        {
            PluginNamespace = pluginNamespace ?? sandbox,
            RegistryProbeRunner = probe ?? TestRegistryProbe.Truthful,
            RhinoProcessStarter = rhino.Start,
            FileUsePollDelay = TimeSpan.FromMilliseconds(50)
        });
        using ExecutorLog log = new ExecutorLog(ExecutorLog.PathFor(request));
        return await engine.RunAsync(
            request,
            new ImmediateProgress<LaunchExecutorEvent>(value => observe?.Invoke(value)),
            log,
            clientDisconnected,
            CancellationToken.None);
    }

    // The executor keeps its log open for the life of the launch, so a reader has to share
    // it the way the writer does.
    private static string ReadWhileOpen(string path)
    {
        using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static LaunchExecutorRequest Request(RegistrySandbox sandbox)
    {
        string artifact = sandbox.PathFor("selected/Sample.rhp");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllBytes(artifact, new byte[4096]);
        return new LaunchExecutorRequest
        {
            LaunchId = Guid.NewGuid().ToString("N"),
            HostKind = "test",
            ReleaseId = "test",
            RhinoVersion = RegistrySandbox.RhinoVersion,
            PluginId = sandbox.PluginId.ToString("D"),
            PluginName = "Sample",
            PluginPath = artifact,
            RhinoExecutable = "stand-in-rhino.exe",
            RhinoRuntime = "netcore",
            WorkingDirectory = sandbox.PathFor("selected"),
            LocksDirectory = sandbox.LocksDirectory,
            LogsDirectory = sandbox.PathFor("logs"),
            TimeoutSeconds = 60
        };
    }

    private sealed class RefusingPluginNamespace : IPluginNamespace
    {
        private readonly PluginRegistrationConflict _conflict;

        public RefusingPluginNamespace(PluginRegistrationConflict conflict) => _conflict = conflict;

        public Task RecoverAsync(
            PluginNamespaceLeaseRequest request,
            IProgress<FileLockWait>? waiting,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<PluginNamespaceLeaseResult> AcquireAsync(
            PluginNamespaceLeaseRequest request,
            IProgress<FileLockWait>? waiting,
            CancellationToken cancellationToken) => Task.FromResult(new PluginNamespaceLeaseResult(
                Lease: null,
                _conflict,
                DisplacedMachineRegistration: null,
                DisplacedUserRegistration: null,
                Seed: null));

        public Task<RegistrationDrift> CorrectAfterExitAsync(
            PluginNamespaceLeaseRequest request,
            CancellationToken cancellationToken) => Task.FromResult(RegistrationDrift.NoJournal);

        public Task<RegistrationSwitchResult> SwitchAsync(
            PluginNamespaceLeaseRequest request,
            IProgress<FileLockWait>? waiting,
            CancellationToken cancellationToken) =>
            Task.FromResult(RegistrationSwitchResult.Refused(_conflict));
    }
}

// Stands in for Rhino by doing the one thing verification observes: holding the selected
// artifact mapped into its address space. Nothing here starts Rhino.
[SupportedOSPlatform("windows")]
internal sealed class StandInRhino : IDisposable
{
    private readonly RegistrySandbox _sandbox;
    private readonly bool _mapArtifact;
    private readonly bool _exitImmediately;
    private Process? _process;

    public StandInRhino(RegistrySandbox sandbox, bool mapArtifact = true, bool exitImmediately = false)
    {
        _sandbox = sandbox;
        _mapArtifact = mapArtifact;
        _exitImmediately = exitImmediately;
    }

    public int? ProcessId { get; private set; }

    /// <summary>The start request the executor built, as it was handed over.</summary>
    public ProcessStartInfo? Requested { get; private set; }

    public Process Start(ProcessStartInfo requested)
    {
        Requested = requested;
        string artifact = _sandbox.PathFor("selected/Sample.rhp");
        string hold = _mapArtifact
            ? "$file = [System.IO.MemoryMappedFiles.MemoryMappedFile]::CreateFromFile(" +
                $"'{artifact}', [System.IO.FileMode]::Open); " +
                "$view = $file.CreateViewAccessor(); Start-Sleep -Seconds 120"
            : "Start-Sleep -Seconds 120";
        // The stand-in reports the identity it was started with before holding the
        // artifact, so a test reads what the launched process's own environment carries
        // rather than what the start request asked for.
        string script = $"Write-Output \"launch-id=$env:{LaunchIdentity.LaunchIdVariable}\"; " +
            (_exitImmediately ? "exit" : hold);
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (KeyValuePair<string, string?> variable in requested.Environment)
            startInfo.Environment[variable.Key] = variable.Value;
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        _process = Process.Start(startInfo)!;
        ProcessId = _process.Id;
        return _process;
    }

    /// <summary>
    /// The first line the stand-in wrote. Read it while the launch is in flight: the engine
    /// disposes the process object it was handed once the launch ends.
    /// </summary>
    public string? ReadFirstOutputLine() => _process?.StandardOutput.ReadLine();

    public static bool HasExited(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        // An exited process is eventually not enumerable at all, which is the same answer.
        catch (ArgumentException)
        {
            return true;
        }
    }

    public void Kill()
    {
        if (ProcessId is not int processId || HasExited(processId))
            return;
        using Process process = Process.GetProcessById(processId);
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
    }

    public void Dispose()
    {
        Kill();
        _process?.Dispose();
    }
}

internal static class LaunchExecutorRequestExtensions
{
    public static Guid PluginGuid(this LaunchExecutorRequest request) =>
        Guid.TryParse(request.PluginId, out Guid pluginId) ? pluginId : Guid.Empty;
}
