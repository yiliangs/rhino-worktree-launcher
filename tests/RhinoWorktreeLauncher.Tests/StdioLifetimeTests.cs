using System.Diagnostics;
using System.Runtime.Versioning;

namespace RhinoWorktreeLauncher.Tests;

/// <summary>
/// What happens to the shipped stdio processes when the session that started them ends. The
/// condition these cover was live on 2026-08-18: an MCP server from a superseded release,
/// still running, with the process that started it long gone.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StdioLifetimeTests
{
    private static readonly TimeSpan EndsWithin = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task The_mcp_server_ends_when_its_client_closes_standard_input()
    {
        using Process server = Process.Start(new ProcessStartInfo
        {
            FileName = McpServerPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        })!;
        Task<string> error = server.StandardError.ReadToEndAsync();
        try
        {
            await InitializeAsync(server);

            server.StandardInput.Close();

            Assert.True(
                server.WaitForExit((int)EndsWithin.TotalMilliseconds),
                $"The MCP server outlived its client's standard input. {await error}");
            Assert.Equal(0, server.ExitCode);
        }
        finally
        {
            if (!server.HasExited)
                server.Kill(entireProcessTree: true);
            await error;
        }
    }

    /// <summary>
    /// The orphan's exact shape: the process that started the server dies while the server's
    /// standard input stays open, because a third process holds the other end of it. Only the
    /// parent-process signal can decide this one.
    /// </summary>
    [Fact]
    public async Task The_mcp_server_ends_when_the_process_that_started_it_dies()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string serverProcessIdPath = temporary.PathFor("server-process-id.txt");
        // The server inherits this stub's standard streams, and this test holds the writing
        // end of them, so killing the stub takes away the parent and nothing else.
        using Process stub = Process.Start(StubHost(
            $"$server = Start-Process -FilePath '{McpServerPath}' -PassThru -NoNewWindow; " +
            $"Set-Content -LiteralPath '{serverProcessIdPath}' -Value $server.Id; " +
            "Start-Sleep -Seconds 300"))!;
        _ = stub.StandardOutput.ReadToEndAsync();
        _ = stub.StandardError.ReadToEndAsync();
        using Process server = await WaitForProcessAsync(serverProcessIdPath);
        try
        {
            // The stub alone, never its tree: the server has to notice this for itself.
            stub.Kill(entireProcessTree: false);
            await stub.WaitForExitAsync();

            Assert.True(
                server.WaitForExit((int)EndsWithin.TotalMilliseconds),
                "The MCP server outlived the process that started it, which is the orphan " +
                "condition this watch exists to end.");
        }
        finally
        {
            if (!stub.HasExited)
                stub.Kill(entireProcessTree: true);
            if (!server.HasExited)
                server.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task The_bootstrap_passes_the_end_of_its_input_to_the_server_it_forwards_to()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string endedPath = temporary.PathFor("input-ended.txt");
        // A child that ends itself when its input does, which is what the MCP server does.
        using Process bootstrap = Process.Start(Bootstrap(
            temporary,
            $"$null = [Console]::In.ReadToEnd(); Set-Content -LiteralPath '{endedPath}' -Value 'ended'"))!;
        Task<string> error = bootstrap.StandardError.ReadToEndAsync();
        _ = bootstrap.StandardOutput.ReadToEndAsync();
        try
        {
            bootstrap.StandardInput.Close();

            Assert.True(
                bootstrap.WaitForExit((int)EndsWithin.TotalMilliseconds),
                $"The bootstrap outlived its own standard input. {await error}");
            Assert.Equal(0, bootstrap.ExitCode);
            Assert.True(
                File.Exists(endedPath),
                "The bootstrap did not close the standard input of the process it forwards to, " +
                "so that process never learned the session had ended.");
            Assert.DoesNotContain("stdio_child_did_not_end", await error, StringComparison.Ordinal);
        }
        finally
        {
            if (!bootstrap.HasExited)
                bootstrap.Kill(entireProcessTree: true);
            await error;
        }
    }

    [Fact]
    public async Task The_bootstrap_ends_a_child_that_ignores_the_end_of_its_input()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string childProcessIdPath = temporary.PathFor("child-process-id.txt");
        // A child from an older release, or one whose transport is stuck, never reads its
        // input at all. It is still unreachable once the session ends.
        using Process bootstrap = Process.Start(Bootstrap(
            temporary,
            $"Set-Content -LiteralPath '{childProcessIdPath}' -Value $PID; Start-Sleep -Seconds 300"))!;
        Task<string> error = bootstrap.StandardError.ReadToEndAsync();
        _ = bootstrap.StandardOutput.ReadToEndAsync();
        using Process child = await WaitForProcessAsync(childProcessIdPath);
        try
        {
            bootstrap.StandardInput.Close();

            Assert.True(
                child.WaitForExit((int)EndsWithin.TotalMilliseconds),
                "The bootstrap left a child running after the session that started it ended.");
            Assert.True(bootstrap.WaitForExit((int)EndsWithin.TotalMilliseconds));
            Assert.Contains("stdio_child_did_not_end", await error, StringComparison.Ordinal);
        }
        finally
        {
            if (!bootstrap.HasExited)
                bootstrap.Kill(entireProcessTree: true);
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
            await error;
        }
    }

    private static async Task InitializeAsync(Process server)
    {
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await server.StandardInput.WriteLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"rwl-test","version":"1.0.0"}}}""");
        await server.StandardInput.FlushAsync(timeout.Token);
        string? answer = await server.StandardOutput.ReadLineAsync(timeout.Token);
        Assert.False(string.IsNullOrWhiteSpace(answer));
    }

    private static async Task<Process> WaitForProcessAsync(string processIdPath)
    {
        using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!File.Exists(processIdPath))
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        string recorded = string.Empty;
        while (!int.TryParse(recorded.Trim(), out _))
        {
            recorded = await File.ReadAllTextAsync(processIdPath, timeout.Token);
            if (!int.TryParse(recorded.Trim(), out _))
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }
        return Process.GetProcessById(int.Parse(recorded.Trim()));
    }

    /// <summary>
    /// The built bootstrap in its forwarding mode, pointed at a release whose MCP executable
    /// is the given stub. The stub stands in for the server so the test decides how the child
    /// behaves; the bootstrap's own behavior is what is under test.
    /// </summary>
    private static ProcessStartInfo Bootstrap(TemporaryDirectory temporary, string stubScript)
    {
        temporary.WriteFile(
            "data/current.json",
            $$"""
            {
              "desktop": {{System.Text.Json.JsonSerializer.Serialize(PowerShellPath)}},
              "cli": {{System.Text.Json.JsonSerializer.Serialize(PowerShellPath)}},
              "mcp": {{System.Text.Json.JsonSerializer.Serialize(PowerShellPath)}}
            }
            """);
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(BootstrapPath);
        startInfo.ArgumentList.Add("mcp");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(stubScript);
        startInfo.Environment["RWL_DATA_ROOT"] = temporary.PathFor("data");
        return startInfo;
    }

    private static ProcessStartInfo StubHost(string script)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = PowerShellPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        return startInfo;
    }

    private static string McpServerPath => Path.Combine(AppContext.BaseDirectory, "rwl-mcp.exe");

    private static string PowerShellPath => Path.Combine(
        Environment.SystemDirectory,
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    private static string BootstrapPath => Path.Combine(
        RepositoryRoot(),
        "src",
        "Rwl.Bootstrap",
        "bin",
        new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ??
            throw new DirectoryNotFoundException("The test build configuration was not found."),
        "net8.0",
        "win-x64",
        "rwl.dll");

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RhinoWorktreeLauncher.slnx")))
            directory = directory.Parent;
        return directory?.FullName ??
            throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }
}
