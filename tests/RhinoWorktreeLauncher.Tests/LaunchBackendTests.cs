using System.Diagnostics;
using System.Text.Json;
using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class LaunchBackendTests
{
    [Fact]
    public async Task Launch_succeeds_only_after_receipt_proves_selected_binaries()
    {
        string driver = """
            param([string]$RequestPath)
            $request = Get-Content -Raw -LiteralPath $RequestPath | ConvertFrom-Json
            $root = [IO.Path]::GetFullPath($request.worktreePath)
            $package = Join-Path $root 'artifacts\package'
            New-Item -ItemType Directory -Force -Path $package | Out-Null
            $plugin = Join-Path $package 'Sample.rhp'
            $dependency = Join-Path $package 'Sample.Core.dll'
            Set-Content -Path $plugin -Value 'plugin'
            Set-Content -Path $dependency -Value 'dependency'
            [ordered]@{
              protocolVersion = 1
              kind = 'result'
              success = $true
              packageDirectory = $package
              pluginPath = $plugin
              criticalDependencies = @(
                [ordered]@{ name = 'Sample.Core'; path = $dependency }
              )
              receipt = [ordered]@{
                launchIdEnvironmentVariable = 'RWL_LAUNCH_ID'
                receiptPathEnvironmentVariable = 'RWL_RECEIPT_PATH'
              }
            } | ConvertTo-Json -Depth 8 -Compress
            """;
        using TemporaryDirectory temporary = RepositoryFixture.Create(driver);
        ProcessStartInfo? launched = null;
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            RhinoProcessStarter = startInfo =>
            {
                launched = startInfo;
                Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    ArgumentList = { "/c", "exit", "0" },
                    UseShellExecute = false,
                    CreateNoWindow = true
                })!;
                string receiptPath = startInfo.Environment["RWL_RECEIPT_PATH"]!;
                string launchId = startInfo.Environment["RWL_LAUNCH_ID"]!;
                string package = startInfo.Environment["RHINO_PACKAGE_DIRS"]!;
                File.WriteAllText(receiptPath, JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    status = "loaded",
                    launchId,
                    processId = process.Id,
                    pluginPath = Path.Combine(package, "Sample.rhp"),
                    criticalDependencies = new[]
                    {
                        new { name = "Sample.Core", path = Path.Combine(package, "Sample.Core.dll") }
                    }
                }));
                return process;
            }
        });
        await backend.RegisterProjectAsync(temporary.PathFor("repository"), CancellationToken.None);

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            temporary.PathFor("repository"),
            TimeSpan.FromSeconds(10),
            progress: null,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(LaunchStatus.Succeeded, result.Value!.Status);
        Assert.Equal(
            Path.GetFullPath(temporary.PathFor("repository/artifacts/package")),
            launched!.Environment["RHINO_PACKAGE_DIRS"]!.TrimEnd(Path.DirectorySeparatorChar));
        Assert.True(File.Exists(result.Value.DiagnosticsLogPath));
        string[] logLines = await File.ReadAllLinesAsync(result.Value.DiagnosticsLogPath);
        Assert.NotEmpty(logLines);
        Assert.All(logLines, line =>
        {
            using JsonDocument record = JsonDocument.Parse(line);
            Assert.Equal("progress", record.RootElement.GetProperty("type").GetString());
        });
    }

    [Fact]
    public async Task Overlapping_launches_keep_package_directories_process_scoped()
    {
        string driver = """
            param([string]$RequestPath)
            $request = Get-Content -Raw -LiteralPath $RequestPath | ConvertFrom-Json
            $root = [IO.Path]::GetFullPath($request.worktreePath)
            $package = Join-Path $root 'artifacts\package'
            New-Item -ItemType Directory -Force -Path $package | Out-Null
            $plugin = Join-Path $package 'Sample.rhp'
            Set-Content -Path $plugin -Value 'plugin'
            [ordered]@{
              protocolVersion = 1
              kind = 'result'
              success = $true
              packageDirectory = $package
              pluginPath = $plugin
              criticalDependencies = @()
              receipt = [ordered]@{
                launchIdEnvironmentVariable = 'RWL_LAUNCH_ID'
                receiptPathEnvironmentVariable = 'RWL_RECEIPT_PATH'
              }
            } | ConvertTo-Json -Depth 8 -Compress
            """;
        using TemporaryDirectory temporary = RepositoryFixture.Create(driver);
        string repository = temporary.PathFor("repository");
        string linked = temporary.PathFor("linked");
        temporary.Run("git", repository, "worktree", "add", "--quiet", "-b", "parallel", linked);
        System.Collections.Concurrent.ConcurrentBag<string> packageDirectories = new();
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            RhinoProcessStarter = startInfo =>
            {
                Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    ArgumentList = { "/c", "ping", "127.0.0.1", "-n", "2", ">", "nul" },
                    UseShellExecute = false,
                    CreateNoWindow = true
                })!;
                string package = startInfo.Environment["RHINO_PACKAGE_DIRS"]!;
                packageDirectories.Add(package);
                File.WriteAllText(
                    startInfo.Environment["RWL_RECEIPT_PATH"]!,
                    JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1,
                        status = "loaded",
                        launchId = startInfo.Environment["RWL_LAUNCH_ID"],
                        processId = process.Id,
                        pluginPath = Path.Combine(package, "Sample.rhp"),
                        criticalDependencies = Array.Empty<object>()
                    }));
                return process;
            }
        });
        await backend.RegisterProjectAsync(repository, CancellationToken.None);

        CommandResult<LaunchResult>[] results = await Task.WhenAll(
            backend.LaunchAsync(repository, TimeSpan.FromSeconds(10), null, CancellationToken.None),
            backend.LaunchAsync(linked, TimeSpan.FromSeconds(10), null, CancellationToken.None));

        Assert.All(results, result => Assert.True(result.Succeeded));
        string[] packages = packageDirectories.Select(Path.GetFullPath).Order().ToArray();
        Assert.Equal(2, packages.Length);
        Assert.NotEqual(packages[0], packages[1]);
        Assert.Contains(packages, package => package.StartsWith(Path.GetFullPath(repository), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(packages, package => package.StartsWith(Path.GetFullPath(linked), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Dependency_path_mismatch_is_a_terminal_machine_readable_failure()
    {
        string driver = """
            param([string]$RequestPath)
            $request = Get-Content -Raw -LiteralPath $RequestPath | ConvertFrom-Json
            $root = [IO.Path]::GetFullPath($request.worktreePath)
            $package = Join-Path $root 'artifacts\package'
            New-Item -ItemType Directory -Force -Path $package | Out-Null
            $plugin = Join-Path $package 'Sample.rhp'
            $dependency = Join-Path $package 'Sample.Core.dll'
            Set-Content -Path $plugin -Value 'plugin'
            Set-Content -Path $dependency -Value 'dependency'
            [ordered]@{
              protocolVersion = 1; kind = 'result'; success = $true
              packageDirectory = $package; pluginPath = $plugin
              criticalDependencies = @([ordered]@{ name = 'Sample.Core'; path = $dependency })
              receipt = [ordered]@{
                launchIdEnvironmentVariable = 'RWL_LAUNCH_ID'
                receiptPathEnvironmentVariable = 'RWL_RECEIPT_PATH'
              }
            } | ConvertTo-Json -Depth 8 -Compress
            """;
        using TemporaryDirectory temporary = RepositoryFixture.Create(driver);
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            RhinoProcessStarter = startInfo =>
            {
                Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    ArgumentList = { "/c", "ping", "127.0.0.1", "-n", "2", ">", "nul" },
                    UseShellExecute = false,
                    CreateNoWindow = true
                })!;
                string package = startInfo.Environment["RHINO_PACKAGE_DIRS"]!;
                File.WriteAllText(
                    startInfo.Environment["RWL_RECEIPT_PATH"]!,
                    JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1,
                        status = "loaded",
                        launchId = startInfo.Environment["RWL_LAUNCH_ID"],
                        processId = process.Id,
                        pluginPath = Path.Combine(package, "Sample.rhp"),
                        criticalDependencies = new[]
                        {
                            new { name = "Sample.Core", path = temporary.PathFor("other/Sample.Core.dll") }
                        }
                    }));
                return process;
            }
        });
        await backend.RegisterProjectAsync(temporary.PathFor("repository"), CancellationToken.None);

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            temporary.PathFor("repository"),
            TimeSpan.FromSeconds(10),
            null,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(LaunchStatus.Failed, result.Value!.Status);
        Assert.Equal("receipt_mismatch", Assert.Single(result.Diagnostics).Code);
        Assert.True(File.Exists(result.Value.DiagnosticsLogPath));
    }

    [Fact]
    public async Task Driver_build_failure_preserves_its_machine_readable_error()
    {
        string driver = """
            [ordered]@{
              protocolVersion = 1; kind = 'result'; success = $false
              errorCode = 'build_failed'; errorMessage = 'Compiler rejected the selected worktree.'
            } | ConvertTo-Json -Compress
            """;
        using TemporaryDirectory temporary = RepositoryFixture.Create(driver);
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs")
        });
        await backend.RegisterProjectAsync(temporary.PathFor("repository"), CancellationToken.None);

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            temporary.PathFor("repository"),
            TimeSpan.FromSeconds(5),
            null,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("build_failed", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(LaunchStatus.Failed, result.Value!.Status);
    }

    [Fact]
    public async Task Rhino_start_failure_is_terminal_and_machine_readable()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create(SuccessfulDriver);
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            RhinoProcessStarter = _ => throw new InvalidOperationException("Rhino could not start.")
        });
        await backend.RegisterProjectAsync(temporary.PathFor("repository"), CancellationToken.None);

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            temporary.PathFor("repository"),
            TimeSpan.FromSeconds(5),
            null,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("launch_failed", Assert.Single(result.Diagnostics).Code);
        Assert.Contains("could not start", result.Diagnostics[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Receipt_timeout_terminates_the_unverified_Rhino_process()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create(SuccessfulDriver);
        string lockPath = temporary.PathFor("rhino-process.lock");
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            RhinoProcessStarter = _ =>
            {
                string escapedLockPath = lockPath.Replace("'", "''", StringComparison.Ordinal);
                ProcessStartInfo processStart = new ProcessStartInfo
                {
                    FileName = "pwsh",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                processStart.ArgumentList.Add("-NoProfile");
                processStart.ArgumentList.Add("-Command");
                processStart.ArgumentList.Add(
                    "$ErrorActionPreference = 'Stop'; " +
                    $"$stream = [IO.File]::Open('{escapedLockPath}', [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); " +
                    "[Console]::Out.WriteLine('ready'); [Console]::Out.Flush(); Start-Sleep -Seconds 30");
                Process process = Process.Start(processStart)!;
                Assert.Equal("ready", process.StandardOutput.ReadLine());
                return process;
            }
        });
        await backend.RegisterProjectAsync(temporary.PathFor("repository"), CancellationToken.None);

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            temporary.PathFor("repository"),
            TimeSpan.FromSeconds(2),
            null,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("launch_timeout", Assert.Single(result.Diagnostics).Code);
        using FileStream released = new FileStream(
            lockPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
    }

    [Fact]
    public async Task Receipt_with_unknown_status_fails_closed()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create(SuccessfulDriver);
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            RhinoProcessStarter = startInfo =>
            {
                Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    ArgumentList = { "/c", "ping", "127.0.0.1", "-n", "2", ">", "nul" },
                    UseShellExecute = false,
                    CreateNoWindow = true
                })!;
                string package = startInfo.Environment["RHINO_PACKAGE_DIRS"]!;
                File.WriteAllText(
                    startInfo.Environment["RWL_RECEIPT_PATH"]!,
                    JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1,
                        status = "pending",
                        launchId = startInfo.Environment["RWL_LAUNCH_ID"],
                        processId = process.Id,
                        pluginPath = Path.Combine(package, "Sample.rhp"),
                        criticalDependencies = Array.Empty<object>()
                    }));
                return process;
            }
        });
        await backend.RegisterProjectAsync(temporary.PathFor("repository"), CancellationToken.None);

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            temporary.PathFor("repository"),
            TimeSpan.FromSeconds(5),
            null,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("receipt_mismatch", Assert.Single(result.Diagnostics).Code);
    }

    private const string SuccessfulDriver = """
        param([string]$RequestPath)
        $request = Get-Content -Raw -LiteralPath $RequestPath | ConvertFrom-Json
        $root = [IO.Path]::GetFullPath($request.worktreePath)
        $package = Join-Path $root 'artifacts\package'
        New-Item -ItemType Directory -Force -Path $package | Out-Null
        $plugin = Join-Path $package 'Sample.rhp'
        Set-Content -Path $plugin -Value 'plugin'
        [ordered]@{
          protocolVersion = 1; kind = 'result'; success = $true
          packageDirectory = $package; pluginPath = $plugin
          criticalDependencies = @()
          receipt = [ordered]@{
            launchIdEnvironmentVariable = 'RWL_LAUNCH_ID'
            receiptPathEnvironmentVariable = 'RWL_RECEIPT_PATH'
          }
        } | ConvertTo-Json -Depth 8 -Compress
        """;
}
