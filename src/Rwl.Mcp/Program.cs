using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RhinoWorktreeLauncher;

namespace Rwl.Mcp;

internal static class Program
{
    public static Task Main(string[] args)
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

        return builder.Build().RunAsync();
    }
}
