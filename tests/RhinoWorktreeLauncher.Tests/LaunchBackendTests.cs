using System.Diagnostics;
using System.Text.Json;
using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class LaunchBackendTests
{
    [Fact]
    public async Task Launch_succeeds_only_after_the_app_verifier_proves_workspace_binaries()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        string driverPath = temporary.PathFor("selected/Driver.ps1");
        temporary.WriteFile("selected/Driver.ps1", SuccessfulBuildDriver);
        string verifierPath = temporary.PathFor("launcher/verifier/Rwl.RhinoVerifier.rhp");
        temporary.WriteFile("launcher/verifier/Rwl.RhinoVerifier.rhp", "verifier");
        ProcessStartInfo? launched = null;
        LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs"),
            WorkspacesDirectory = temporary.PathFor("launcher/workspaces"),
            VerifierPluginPath = verifierPath,
            RhinoExecutableResolver = _ => "fake-rhino.exe",
            RhinoProcessStarter = startInfo =>
            {
                launched = startInfo;
                Process process = StartSleepingProcess();
                VerifierRequest request = JsonSerializer.Deserialize<VerifierRequest>(
                    File.ReadAllText(startInfo.Environment["RWL_VERIFY_REQUEST"]!),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                File.WriteAllText(request.ResultPath, JsonSerializer.Serialize(new VerifierResult
                {
                    SchemaVersion = 1,
                    Status = "loaded",
                    LaunchId = request.LaunchId,
                    ProcessId = process.Id,
                    PluginPath = request.PluginPath,
                    CriticalDependencies = request.CriticalDependencies
                }));
                return process;
            }
        });
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(
                temporary.PathFor("repository"),
                ProjectAccessGrant.Full,
                driverPath),
            CancellationToken.None);
        File.Delete(driverPath);

        CommandResult<LaunchResult> result = await backend.LaunchAsync(
            temporary.PathFor("repository"),
            TimeSpan.FromSeconds(10),
            progress: null,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(LaunchStatus.Succeeded, result.Value!.Status);
        Assert.StartsWith(
            temporary.PathFor("launcher/workspaces"),
            result.Value.PluginPath!,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            launched!.ArgumentList,
            argument => argument.Contains("_RwlVerifyLaunch", StringComparison.Ordinal));
        Assert.Contains(
            Path.GetDirectoryName(verifierPath)!,
            launched.Environment["RHINO_PACKAGE_DIRS"]!,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(result.Value.DiagnosticsLogPath));
    }

    private static Process StartSleepingProcess()
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
        return Process.Start(startInfo)!;
    }

    private const string SuccessfulBuildDriver = """
        param([Parameter(Mandatory = $true)][string]$RequestPath)
        $request = Get-Content -Raw -LiteralPath $RequestPath | ConvertFrom-Json
        $package = Join-Path $request.buildPath 'package'
        New-Item -ItemType Directory -Force -Path $package | Out-Null
        $plugin = Join-Path $package 'Sample.rhp'
        $dependency = Join-Path $package 'Sample.Core.dll'
        Set-Content -LiteralPath $plugin -Value 'plugin'
        Set-Content -LiteralPath $dependency -Value 'dependency'
        [ordered]@{
            protocolVersion = 2
            kind = 'result'
            success = $true
            pluginId = '735b6a53-ddc2-46e9-a82c-c0cd86d0609a'
            packageDirectory = $package
            pluginPath = $plugin
            rhinoRuntime = 'netfx'
            criticalDependencies = @(
                [ordered]@{ name = 'Sample.Core'; path = $dependency }
            )
        } | ConvertTo-Json -Depth 8 -Compress
        """;
}
