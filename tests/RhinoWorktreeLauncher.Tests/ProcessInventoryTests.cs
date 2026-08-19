using System.Diagnostics;
using System.Runtime.Versioning;

namespace RhinoWorktreeLauncher.Tests;

/// <summary>
/// What doctor concludes from a process table. The table is injected, so these decide the
/// judgement rather than whatever the machine running the tests happens to have open; the one
/// test that reads the real table asserts only what is true of every machine.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessInventoryTests
{
    private const string CurrentRelease = "20260818-125536-790";
    private const string SupersededRelease = "20260815-174812-452";

    [Fact]
    public async Task Doctor_lists_the_live_rwl_processes_with_release_and_parent()
    {
        using TemporaryDirectory temporary = Installed();
        LauncherBackend backend = Backend(temporary, new[]
        {
            Shell(1000),
            Bootstrap(2000, parent: 1000),
            Server(3000, parent: 2000, CurrentRelease),
            Desktop(4000, parent: 1000, CurrentRelease)
        });

        CommandResult<DoctorReport> result = await backend.RunDoctorAsync(CancellationToken.None);

        DoctorCheck check = Assert.Single(result.Value!.Checks, candidate => candidate.Name == "processes");
        Assert.True(check.Passed);
        Assert.Contains("3 live RWL process(es)", check.Message, StringComparison.Ordinal);
        Assert.Contains($"installed release {CurrentRelease}", check.Message, StringComparison.Ordinal);
        Assert.Contains("pid 3000 mcp-server", check.Message, StringComparison.Ordinal);
        Assert.Contains("parent 2000 running", check.Message, StringComparison.Ordinal);
        Assert.Contains("pid 4000 desktop", check.Message, StringComparison.Ordinal);
    }

    // The condition found live on 2026-08-18: a server still running, its client and the
    // process that bridged its streams both gone.
    [Fact]
    public async Task Doctor_flags_a_server_whose_parent_is_gone()
    {
        using TemporaryDirectory temporary = Installed();
        LauncherBackend backend = Backend(temporary, new[]
        {
            Server(3000, parent: 2000, CurrentRelease)
        });

        CommandResult<DoctorReport> result = await backend.RunDoctorAsync(CancellationToken.None);

        DoctorCheck check = Assert.Single(result.Value!.Checks, candidate => candidate.Name == "process:3000");
        Assert.False(check.Passed);
        Assert.Equal(DiagnosticSeverity.Warning, check.Severity);
        Assert.Contains("orphaned", check.Message, StringComparison.Ordinal);
        Assert.Contains("End it from Task Manager", check.Message, StringComparison.Ordinal);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "doctor_process_3000_failed");
    }

    // Windows reuses process ids, so a live process wearing the dead parent's id must not
    // make an orphan look attended.
    [Fact]
    public async Task Doctor_flags_a_server_whose_parent_id_was_taken_by_a_later_process()
    {
        using TemporaryDirectory temporary = Installed();
        LauncherBackend backend = Backend(temporary, new[]
        {
            Server(3000, parent: 2000, CurrentRelease) with { StartedAt = Started(9) },
            Shell(2000) with { StartedAt = Started(11) }
        });

        CommandResult<DoctorReport> result = await backend.RunDoctorAsync(CancellationToken.None);

        DoctorCheck check = Assert.Single(result.Value!.Checks, candidate => candidate.Name == "process:3000");
        Assert.Contains("orphaned", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_flags_a_server_serving_a_release_the_installation_has_replaced()
    {
        using TemporaryDirectory temporary = Installed();
        LauncherBackend backend = Backend(temporary, new[]
        {
            Shell(1000),
            Bootstrap(2000, parent: 1000),
            Server(3000, parent: 2000, SupersededRelease)
        });

        CommandResult<DoctorReport> result = await backend.RunDoctorAsync(CancellationToken.None);

        DoctorCheck check = Assert.Single(result.Value!.Checks, candidate => candidate.Name == "process:3000");
        Assert.Equal(DiagnosticSeverity.Warning, check.Severity);
        Assert.Contains(
            $"serving release {SupersededRelease} while the installed release is {CurrentRelease}",
            check.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("orphaned", check.Message, StringComparison.Ordinal);
        // A warning is a finding, not a failure: nothing here stops RWL from launching.
        Assert.True(result.Value.Healthy);
    }

    // A launch executor is deliberately detached from the bootstrap that started it
    // (ADR 0015), so a dead parent is its ordinary state and never a finding.
    [Fact]
    public async Task Doctor_does_not_call_a_detached_launch_executor_orphaned()
    {
        using TemporaryDirectory temporary = Installed();
        LauncherBackend backend = Backend(temporary, new[]
        {
            new RunningProcess(5000, 2000, "rwl-cli.exe", ReleasePath(CurrentRelease, "cli", "rwl-cli.exe"), Started(5))
        });

        CommandResult<DoctorReport> result = await backend.RunDoctorAsync(CancellationToken.None);

        Assert.DoesNotContain(result.Value!.Checks, candidate => candidate.Name == "process:5000");
        Assert.Contains("pid 5000 cli", Assert.Single(
            result.Value.Checks,
            candidate => candidate.Name == "processes").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_reports_a_process_table_it_cannot_read()
    {
        using TemporaryDirectory temporary = Installed();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LocksDirectory = temporary.PathFor("launcher/locks"),
            CurrentReleasePath = temporary.PathFor("data/current.json"),
            RegistryProbeRunner = TestRegistryProbe.Truthful,
            ProcessSnapshotReader = () => throw new InvalidOperationException("Windows refused a process snapshot.")
        });

        CommandResult<DoctorReport> result = await backend.RunDoctorAsync(CancellationToken.None);

        DoctorCheck check = Assert.Single(result.Value!.Checks, candidate => candidate.Name == "processes");
        Assert.False(check.Passed);
        Assert.Equal(DiagnosticSeverity.Error, check.Severity);
        Assert.Contains("Windows refused a process snapshot.", check.Message, StringComparison.Ordinal);
        Assert.False(result.Value.Healthy);
    }

    // Without the pointer there is no installed release to compare against, which matters
    // only while something is running from a release directory.
    [Fact]
    public async Task Doctor_names_an_unreadable_release_pointer_only_when_a_release_is_running()
    {
        using TemporaryDirectory withRelease = new TemporaryDirectory();
        CommandResult<DoctorReport> flagged = await Backend(
            withRelease,
            new[] { Server(3000, parent: 2000, CurrentRelease), Shell(2000) })
            .RunDoctorAsync(CancellationToken.None);
        using TemporaryDirectory withoutRelease = new TemporaryDirectory();
        CommandResult<DoctorReport> quiet = await Backend(
            withoutRelease,
            new[] { Bootstrap(2000, parent: 1000), Shell(1000) })
            .RunDoctorAsync(CancellationToken.None);

        DoctorCheck check = Assert.Single(
            flagged.Value!.Checks,
            candidate => candidate.Name == "processes:release");
        Assert.Equal(DiagnosticSeverity.Warning, check.Severity);
        Assert.Contains("does not exist", check.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(quiet.Value!.Checks, candidate => candidate.Name == "processes:release");
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\RhinoWorktreeLauncher\releases\20260818-125536-790\mcp\rwl-mcp.exe", "20260818-125536-790")]
    [InlineData(@"C:\Users\x\AppData\Local\RhinoWorktreeLauncher\bootstrap\rwl.exe", null)]
    [InlineData(@"C:\repos\rhino-worktree-launcher\src\Rwl.Mcp\bin\Debug\net8.0\rwl-mcp.exe", null)]
    [InlineData(null, null)]
    public void A_release_is_read_from_the_path_the_process_was_resolved_from(string? path, string? expected) =>
        Assert.Equal(expected, RwlProcessInventory.ReleaseIdOf(path));

    /// <summary>
    /// The enumeration itself, against the real machine. It asserts only what is true
    /// everywhere: a process RWL started is described with the path and start time the
    /// classification needs, and its parent is this test.
    /// </summary>
    [Fact]
    public async Task The_process_table_describes_a_running_rwl_process_and_its_parent()
    {
        using Process server = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(AppContext.BaseDirectory, "rwl-mcp.exe"),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        })!;
        _ = server.StandardError.ReadToEndAsync();
        _ = server.StandardOutput.ReadToEndAsync();
        try
        {
            IReadOnlyList<RunningProcess> processes = ProcessSnapshot.Read();

            RunningProcess described = Assert.Single(
                processes,
                process => process.ProcessId == server.Id);
            Assert.Equal("rwl-mcp.exe", described.Name, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(
                Path.Combine(AppContext.BaseDirectory, "rwl-mcp.exe"),
                described.ExecutablePath,
                StringComparer.OrdinalIgnoreCase);
            Assert.NotNull(described.StartedAt);
            Assert.Equal(
                Environment.ProcessId,
                ProcessSnapshot.ParentOf(processes, server.Id)?.ProcessId);
            Assert.Null(ProcessSnapshot.ParentOf(processes, processId: -1));
        }
        finally
        {
            server.StandardInput.Close();
            if (!server.WaitForExit(30_000))
                server.Kill(entireProcessTree: true);
        }
    }

    private static LauncherBackend Backend(
        TemporaryDirectory temporary,
        IReadOnlyList<RunningProcess> processes) => new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            LocksDirectory = temporary.PathFor("launcher/locks"),
            CurrentReleasePath = temporary.PathFor("data/current.json"),
            RegistryProbeRunner = TestRegistryProbe.Truthful,
            ProcessSnapshotReader = () => processes
        });

    private static TemporaryDirectory Installed()
    {
        TemporaryDirectory temporary = new TemporaryDirectory();
        temporary.WriteFile(
            "data/current.json",
            $$"""
            {
              "desktop": {{Json(ReleasePath(CurrentRelease, "desktop", "RhinoWorktreeLauncher.exe"))}},
              "cli": {{Json(ReleasePath(CurrentRelease, "cli", "rwl-cli.exe"))}},
              "mcp": {{Json(ReleasePath(CurrentRelease, "mcp", "rwl-mcp.exe"))}}
            }
            """);
        return temporary;
    }

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static RunningProcess Shell(int processId) =>
        new RunningProcess(processId, 900, "pwsh.exe", @"C:\Program Files\PowerShell\7\pwsh.exe", Started(1));

    private static RunningProcess Bootstrap(int processId, int parent) => new RunningProcess(
        processId,
        parent,
        "rwl.exe",
        @"C:\Users\x\AppData\Local\RhinoWorktreeLauncher\bootstrap\rwl.exe",
        Started(2));

    private static RunningProcess Server(int processId, int parent, string release) => new RunningProcess(
        processId,
        parent,
        "rwl-mcp.exe",
        ReleasePath(release, "mcp", "rwl-mcp.exe"),
        Started(3));

    private static RunningProcess Desktop(int processId, int parent, string release) => new RunningProcess(
        processId,
        parent,
        "RhinoWorktreeLauncher.exe",
        ReleasePath(release, "desktop", "RhinoWorktreeLauncher.exe"),
        Started(4));

    private static string ReleasePath(string release, string component, string executable) =>
        $@"C:\Users\x\AppData\Local\RhinoWorktreeLauncher\releases\{release}\{component}\{executable}";

    private static DateTimeOffset Started(int hour) =>
        new DateTimeOffset(2026, 8, 18, hour, 0, 0, TimeSpan.Zero);
}
