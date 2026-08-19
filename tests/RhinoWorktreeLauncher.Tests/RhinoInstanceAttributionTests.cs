using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace RhinoWorktreeLauncher.Tests;

/// <summary>
/// Which live Rhino process runs which build. The process table is injected, so these decide
/// what attribution concludes rather than whatever the machine running the tests happens to
/// have open; the one test that reads a real address space uses a process this test started,
/// holding a real artifact mapped, which is the state ADR 0002 verification observes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RhinoInstanceAttributionTests
{
    // The ambiguity this whole query exists for: two sessions launched at once, both
    // verified, each Rhino holding its own worktree's build.
    [Fact]
    public async Task Concurrent_launches_are_told_apart_by_the_artifact_each_rhino_holds()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        LauncherBackend backend = Backend(
            temporary,
            new[] { Rhino(2000), Rhino(1000), Shell(900) },
            processId => new[] { $@"C:\worktrees\branch-{processId}\Sample.rhp" });

        CommandResult<RhinoInstanceAttribution> result =
            await backend.DescribeRhinoInstancesAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        RhinoInstance[] instances = result.Value!.Instances.ToArray();
        Assert.Equal(new[] { 1000, 2000 }, instances.Select(instance => instance.ProcessId));
        Assert.Equal(@"C:\worktrees\branch-1000\Sample.rhp", Assert.Single(instances[0].PlugInPaths));
        Assert.Equal(@"C:\worktrees\branch-2000\Sample.rhp", Assert.Single(instances[1].PlugInPaths));
        Assert.All(instances, instance => Assert.True(instance.IsAttributed));
    }

    [Fact]
    public async Task A_rhino_holding_no_plugin_is_listed_as_holding_none()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        LauncherBackend backend = Backend(
            temporary,
            new[] { Rhino(1000) },
            _ => Array.Empty<string>());

        CommandResult<RhinoInstanceAttribution> result =
            await backend.DescribeRhinoInstancesAsync(CancellationToken.None);

        RhinoInstance instance = Assert.Single(result.Value!.Instances);
        Assert.True(instance.IsAttributed);
        Assert.Empty(instance.PlugInPaths);
        Assert.Contains("holding no plug-in artifact", instance.Describe(), StringComparison.Ordinal);
    }

    // A Rhino this account may not read is still a live Rhino. Leaving it out would produce
    // exactly the mistake this query exists to prevent: a caller concluding the only Rhino
    // it can see is the one it launched.
    [Fact]
    public async Task A_rhino_this_account_cannot_read_is_reported_with_the_reason()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        LauncherBackend backend = Backend(
            temporary,
            new[] { Rhino(1000), Rhino(2000) },
            processId => processId == 1000
                ? throw new Win32Exception(5, "Windows refused to open process 1000 for reading its mapped files.")
                : new[] { @"C:\worktrees\branch\Sample.rhp" });

        CommandResult<RhinoInstanceAttribution> result =
            await backend.DescribeRhinoInstancesAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        RhinoInstance refused = Assert.Single(result.Value!.Instances, instance => instance.ProcessId == 1000);
        Assert.False(refused.IsAttributed);
        Assert.Contains("refused to open process 1000", refused.UnattributableReason!, StringComparison.Ordinal);
        Assert.Contains("not attributable", refused.Describe(), StringComparison.Ordinal);
        Assert.True(Assert.Single(result.Value.Instances, instance => instance.ProcessId == 2000).IsAttributed);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("rhino_instance_unattributable", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("refused to open process 1000", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_rhino_processes_have_their_address_space_read()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        LauncherBackend backend = Backend(
            temporary,
            new[] { Shell(900), Server(3000), Rhino(1000) },
            processId => processId == 1000
                ? new[] { @"C:\worktrees\branch\Sample.rhp" }
                : throw new InvalidOperationException($"Process {processId} is not Rhino and must not be read."));

        CommandResult<RhinoInstanceAttribution> result =
            await backend.DescribeRhinoInstancesAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1000, Assert.Single(result.Value!.Instances).ProcessId);
    }

    [Fact]
    public async Task An_unreadable_process_table_refuses_to_answer_by_name()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            ProcessSnapshotReader = () => throw new InvalidOperationException(
                "Windows refused a process snapshot.")
        });

        CommandResult<RhinoInstanceAttribution> result =
            await backend.DescribeRhinoInstancesAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("rhino_instance_scan_failed", diagnostic.Code);
        Assert.Contains("Windows refused a process snapshot.", diagnostic.Message, StringComparison.Ordinal);
    }

    // Several live Rhino processes are the ordinary result of concurrent launches, so doctor
    // reports them and does not warn.
    [Fact]
    public async Task Doctor_lists_the_live_rhino_instances_without_calling_them_a_finding()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        LauncherBackend backend = Backend(
            temporary,
            new[] { Rhino(1000), Rhino(2000) },
            processId => new[] { $@"C:\worktrees\branch-{processId}\Sample.rhp" });

        CommandResult<DoctorReport> result = await backend.RunDoctorAsync(CancellationToken.None);

        DoctorCheck check = Assert.Single(
            result.Value!.Checks,
            candidate => candidate.Name == "rhino-instances");
        Assert.True(check.Passed);
        Assert.Equal(DiagnosticSeverity.Info, check.Severity);
        Assert.Contains("2 live Rhino process(es)", check.Message, StringComparison.Ordinal);
        Assert.Contains(@"pid 1000", check.Message, StringComparison.Ordinal);
        Assert.Contains(@"C:\worktrees\branch-2000\Sample.rhp", check.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "doctor_rhino-instances_failed");
    }

    [Fact]
    public async Task Doctor_names_a_process_table_it_could_not_read_for_attribution()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LocksDirectory = temporary.PathFor("launcher/locks"),
            CurrentReleasePath = temporary.PathFor("data/current.json"),
            RegistryProbeRunner = TestRegistryProbe.Truthful,
            ProcessSnapshotReader = () => throw new InvalidOperationException(
                "Windows refused a process snapshot.")
        });

        CommandResult<DoctorReport> result = await backend.RunDoctorAsync(CancellationToken.None);

        DoctorCheck check = Assert.Single(
            result.Value!.Checks,
            candidate => candidate.Name == "rhino-instances");
        Assert.False(check.Passed);
        Assert.Equal(DiagnosticSeverity.Error, check.Severity);
        Assert.Contains("Windows refused a process snapshot.", check.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mechanism itself, against a real address space: a process this test started holds
    /// a real <c>.rhp</c> mapped, and attribution finds it by reading that process rather
    /// than by being told what it loaded.
    /// </summary>
    [Fact]
    public async Task A_real_process_is_attributed_to_the_artifact_it_holds_mapped()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using TemporaryDirectory temporary = new TemporaryDirectory();
        string artifact = temporary.PathFor("selected/Sample.rhp");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllBytes(artifact, new byte[4096]);
        using ProcessHoldingArtifact holder = ProcessHoldingArtifact.Start(artifact);
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            // The real reader, over a process table that names this stand-in as Rhino.
            ProcessSnapshotReader = () => new[]
            {
                new RunningProcess(holder.ProcessId, Environment.ProcessId, "Rhino.exe", null, null)
            }
        });

        CommandResult<RhinoInstanceAttribution> result = await holder.WhileHoldingAsync(() =>
            backend.DescribeRhinoInstancesAsync(CancellationToken.None));

        Assert.True(result.Succeeded);
        RhinoInstance instance = Assert.Single(result.Value!.Instances);
        Assert.True(instance.IsAttributed, instance.UnattributableReason);
        Assert.Contains(artifact, instance.PlugInPaths, StringComparer.OrdinalIgnoreCase);
    }

    private static LauncherBackend Backend(
        TemporaryDirectory temporary,
        IReadOnlyList<RunningProcess> processes,
        Func<int, IReadOnlyList<string>> mappedPlugIns) => new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LocksDirectory = temporary.PathFor("launcher/locks"),
            CurrentReleasePath = temporary.PathFor("data/current.json"),
            RegistryProbeRunner = TestRegistryProbe.Truthful,
            ProcessSnapshotReader = () => processes,
            MappedPlugInReader = mappedPlugIns
        });

    private static RunningProcess Rhino(int processId) => new RunningProcess(
        processId,
        900,
        "Rhino.exe",
        @"C:\Program Files\Rhino 8\System\Rhino.exe",
        new DateTimeOffset(2026, 8, 19, 9, 0, 0, TimeSpan.Zero));

    private static RunningProcess Server(int processId) => new RunningProcess(
        processId,
        900,
        "rwl-mcp.exe",
        @"C:\Users\x\AppData\Local\RhinoWorktreeLauncher\releases\r\mcp\rwl-mcp.exe",
        new DateTimeOffset(2026, 8, 19, 8, 0, 0, TimeSpan.Zero));

    private static RunningProcess Shell(int processId) => new RunningProcess(
        processId,
        1,
        "pwsh.exe",
        @"C:\Program Files\PowerShell\7\pwsh.exe",
        new DateTimeOffset(2026, 8, 19, 7, 0, 0, TimeSpan.Zero));
}

/// <summary>
/// A process that does the one thing attribution reads: holds a <c>.rhp</c> mapped into its
/// address space. Nothing here starts Rhino.
/// </summary>
internal sealed class ProcessHoldingArtifact : IDisposable
{
    private readonly Process _process;

    private ProcessHoldingArtifact(Process process) => _process = process;

    public int ProcessId => _process.Id;

    public static ProcessHoldingArtifact Start(string artifact)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "$file = [System.IO.MemoryMappedFiles.MemoryMappedFile]::CreateFromFile(" +
            $"'{artifact}', [System.IO.FileMode]::Open); " +
            "$view = $file.CreateViewAccessor(); 'mapped'; Start-Sleep -Seconds 120");
        return new ProcessHoldingArtifact(Process.Start(startInfo)!);
    }

    /// <summary>
    /// Runs the query only once the mapping exists, so a slow start cannot be mistaken for a
    /// process holding nothing.
    /// </summary>
    public async Task<T> WhileHoldingAsync<T>(Func<Task<T>> query)
    {
        string? mapped = await _process.StandardOutput.ReadLineAsync();
        if (mapped is null)
        {
            throw new InvalidOperationException(
                "The stand-in process ended before it mapped the artifact: " +
                await _process.StandardError.ReadToEndAsync());
        }
        return await query();
    }

    public void Dispose()
    {
        if (!_process.HasExited)
            _process.Kill(entireProcessTree: true);
        _process.WaitForExit();
        _process.Dispose();
    }
}
