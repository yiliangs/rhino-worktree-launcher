using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RhinoWorktreeLauncher;

namespace Rwl.Mcp;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
            options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton(_ => new LauncherBackend(new LauncherBackendOptions
        {
            HostKind = "mcp"
        }));
        // Started here, not on the first launch: the client that spawns this server can
        // sandbox it, and a server that cannot reach the interactive shell has to say so
        // before a launch waits on it. The probe runs in the background, so it delays no
        // part of the stdio handshake.
        builder.Services.AddSingleton(new LaunchHostReadiness());
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "rhino-worktree-launcher",
                    Title = "Rhino Worktree Launcher",
                    Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
                    Description = "Resolves, inspects, and launches registered Rhino plug-in Git worktrees."
                };
                options.ServerInstructions = RwlTools.ServerInstructions;
            })
            .WithStdioServerTransport()
            .WithTools<RwlTools>(serializerOptions: McpJson.Options);

        IHost host = builder.Build();
        // This server exists only for the session that started it. When that session ends it
        // becomes unreachable, and an unreachable server left running holds the release it
        // was spawned from and answers to nobody, which is what five concurrent servers and
        // one orphan from a superseded release looked like on 2026-08-18.
        _ = SessionEndWatch.Start(
            (end, cancellationToken) =>
            {
                Report($"[{end.Code}] {end.Message}");
                return host.StopAsync(cancellationToken);
            },
            (end, reason) =>
            {
                Report($"[session_end_abandoned] {reason} Ending this server anyway, because it " +
                    $"can no longer serve the session it was started for: {end.Message}");
                Environment.Exit(0);
            },
            Report);

        await host.RunAsync();
    }

    // Standard error is where this server's log already goes, and it stays writable while the
    // host is stopping, which the host's own logging does not.
    private static void Report(string message) => Console.Error.WriteLine(message);
}
