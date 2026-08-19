using System.Text.Json;
using System.Text.Json.Serialization;
using RhinoWorktreeLauncher;

namespace Rwl.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions OutputJson = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            CliCommand? command = CliGrammar.Parse(args);
            // The executor owns registry mutation and needs no catalog, so it runs before
            // a backend exists.
            if (command is LaunchExecutorCommand executor)
                return await LaunchExecutorHost.RunAsync(executor.PipeName, CancellationToken.None);

            LauncherBackend backend = new LauncherBackend(new LauncherBackendOptions { HostKind = "cli" });
            return command switch
            {
                ProjectRegisterCommand register => await RegisterProjectAsync(backend, register),
                ProjectRemoveCommand remove => await WriteAsync(
                    await backend.RemoveProjectAsync(remove.ProjectId, CancellationToken.None),
                    remove.Json,
                    _ => $"Removed project '{remove.ProjectId}'."),
                ContextCommand context => await WriteAsync(
                    await backend.ResolveContextAsync(
                        context.WorkingDirectory,
                        CancellationToken.None),
                    context.Json,
                    value => $"{value.DisplayName}: {value.WorktreePath}"),
                WorktreeListCommand list => await WriteAsync(
                    await backend.GetWorktreeSnapshotAsync(
                        list.ProjectId,
                        includeRemote: !list.LocalOnly,
                        CancellationToken.None),
                    list.Json,
                    value => string.Join(Environment.NewLine, value.Worktrees.Select(worktree => worktree.Path))),
                WorktreeInspectCommand inspect => await WriteAsync(
                    await backend.InspectWorktreeAsync(
                        inspect.Path,
                        CancellationToken.None),
                    inspect.Json,
                    value => value.CanLaunch ? "Ready to launch." : "Not ready to launch."),
                LaunchCommand launch => await LaunchAsync(backend, launch),
                RhinoInstancesCommand instances => await WriteAsync(
                    await backend.DescribeRhinoInstancesAsync(CancellationToken.None),
                    instances.Json,
                    value => string.Join(
                        Environment.NewLine,
                        new[] { $"{value.Instances.Count} live Rhino process(es)." }
                            .Concat(value.Instances.Select(instance => instance.Describe())))),
                DoctorCommand doctor => await DoctorAsync(backend, doctor),
                IntegrationStatusCommand status => await IntegrationStatusAsync(status),
                IntegrationInstallCommand install => await InstallIntegrationAsync(install),
                IntegrationRemoveCommand remove => await RemoveIntegrationAsync(remove),
                SessionContextCommand => await SessionContextWriter.WriteAsync(
                    backend,
                    Console.In,
                    Console.Out),
                _ => WriteUsage()
            };
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task<int> RegisterProjectAsync(
        LauncherBackend backend,
        ProjectRegisterCommand command)
    {
        string? configuration = command.Configuration;
        string? platform = command.Platform;
        if (string.IsNullOrWhiteSpace(configuration) != string.IsNullOrWhiteSpace(platform))
            throw new ArgumentException("--configuration and --platform must be supplied together.");

        BuildConfiguration? selection = configuration is null
            ? null
            : new BuildConfiguration(configuration, platform!);
        return await WriteAsync(
            await backend.RegisterProjectAsync(
                new ProjectRegistrationRequest(
                    command.Path,
                    new ProjectAccessGrant(true, !command.NoRemote),
                    command.PluginProjectPath,
                    command.SolutionPath,
                    selection,
                    command.Direct ? LaunchMode.DirectLaunch : LaunchMode.BuildAndLaunch),
                CancellationToken.None),
            command.Json,
            value => $"Registered '{value.ProjectId}' with {value.BuildProfile.SolutionPath} " +
                $"({value.BuildProfile.SelectedConfiguration.DisplayName}, {value.BuildProfile.LaunchMode}).");
    }

    private static async Task<int> LaunchAsync(LauncherBackend backend, LaunchCommand command)
    {
        CommandResult<ResolvedContext> context = await backend.ResolveContextAsync(
            command.Path,
            CancellationToken.None);
        if (!context.Succeeded)
        {
            return await WriteAsync(
                context,
                command.Json,
                value => $"{value.DisplayName}: {value.WorktreePath}");
        }

        double timeoutSeconds = ParseTimeout(command.Timeout);
        Progress<LaunchProgress>? progress = command.Json
            ? null
            : new Progress<LaunchProgress>(update => Console.Error.WriteLine($"[{update.StageToken}] {update.Message}"));
        return await WriteAsync(
            await backend.LaunchAsync(
                command.Path,
                context.Value!.BuildProfile.LaunchMode,
                TimeSpan.FromSeconds(timeoutSeconds),
                progress,
                CancellationToken.None),
            command.Json,
            value => value.Status == LaunchStatus.Succeeded
                ? $"Verified {value.PluginPath} in Rhino process {value.RhinoProcessId}."
                : $"Launch failed. Diagnostics: {value.DiagnosticsLogPath}");
    }

    private static async Task<int> DoctorAsync(LauncherBackend backend, DoctorCommand command)
    {
        CommandResult<DoctorReport> result = await backend.RunDoctorAsync(CancellationToken.None);
        int adapterExit = await WriteAsync(
            result,
            command.Json,
            value => value.Healthy ? "RWL doctor passed." : "RWL doctor found required failures.");
        return adapterExit == 0 && result.Value?.Healthy == true ? 0 : 1;
    }

    private static async Task<int> InstallIntegrationAsync(IntegrationInstallCommand command)
    {
        string bootstrap = command.BootstrapPath ??
            Environment.GetEnvironmentVariable("RWL_BOOTSTRAP_PATH") ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RhinoWorktreeLauncher",
                "bootstrap",
                "rwl.exe");
        McpClientIntegrationManager manager = new McpClientIntegrationManager();
        McpClientIntegrationStatus status = await manager.InstallAsync(
            command.Client,
            bootstrap,
            installSessionContext: command.Client == McpClientKind.ClaudeCode &&
                !command.NoSessionContext,
            CancellationToken.None);
        WriteIntegrationStatus(status, command.Json);
        return 0;
    }

    private static async Task<int> RemoveIntegrationAsync(IntegrationRemoveCommand command)
    {
        McpClientIntegrationManager manager = new McpClientIntegrationManager();
        McpClientIntegrationStatus status = await manager.RemoveAsync(command.Client, CancellationToken.None);
        WriteIntegrationStatus(status, command.Json);
        return 0;
    }

    private static async Task<int> IntegrationStatusAsync(IntegrationStatusCommand command)
    {
        McpClientIntegrationManager manager = new McpClientIntegrationManager();
        McpClientKind[] clients = command.Client is null
            ? new[] { McpClientKind.ClaudeCode, McpClientKind.Codex }
            : new[] { command.Client.Value };
        McpClientIntegrationStatus[] statuses = await Task.WhenAll(clients.Select(candidate =>
            manager.GetStatusAsync(candidate, CancellationToken.None)));
        if (command.Json)
            Console.WriteLine(JsonSerializer.Serialize(statuses, OutputJson));
        else
            foreach (McpClientIntegrationStatus status in statuses)
                WriteIntegrationStatus(status, json: false);
        return statuses.All(status => !status.McpConfigured || status.Ready) ? 0 : 1;
    }

    private static void WriteIntegrationStatus(McpClientIntegrationStatus status, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(status, OutputJson));
            return;
        }

        string state = status.Ready
            ? "ready"
            : status.McpConfigured
                ? "configured, but bootstrap is missing"
                : "not configured";
        string context = status.SessionContextSupported
            ? status.SessionContextConfigured ? "; session context enabled" : "; session context disabled"
            : string.Empty;
        Console.WriteLine($"{McpClientIntegrationManager.DisplayName(status.Client)}: {state}{context}.");
    }

    private static Task<int> WriteAsync<T>(
        CommandResult<T> result,
        bool json,
        Func<T, string> humanText)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, OutputJson));
        }
        else if (result.Value is not null)
        {
            Console.WriteLine(humanText(result.Value));
            foreach (Diagnostic diagnostic in result.Diagnostics)
                Console.Error.WriteLine($"[{diagnostic.Code}] {diagnostic.Message}");
        }
        else
        {
            foreach (Diagnostic diagnostic in result.Diagnostics)
                Console.Error.WriteLine($"[{diagnostic.Code}] {diagnostic.Message}");
        }
        return Task.FromResult(result.Succeeded ? 0 : 1);
    }

    private static int WriteUsage()
    {
        Console.Error.WriteLine(CliGrammar.Usage);
        return 2;
    }

    private static double ParseTimeout(string? value)
    {
        return value is null
            ? 180
            : double.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                out double parsed) && parsed > 0
                ? parsed
                : throw new ArgumentException("--timeout must be a positive number.");
    }
}

internal static class SessionContextWriter
{
    private static readonly JsonSerializerOptions OutputJson = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<int> WriteAsync(
        LauncherBackend backend,
        TextReader input,
        TextWriter output)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(await input.ReadToEndAsync());
        }
        catch (JsonException)
        {
            return 0;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("cwd", out JsonElement cwdElement) ||
                string.IsNullOrWhiteSpace(cwdElement.GetString()))
            {
                return 0;
            }

            CommandResult<ResolvedContext> result = await backend.ResolveContextAsync(
                cwdElement.GetString()!,
                CancellationToken.None);
            if (!result.Succeeded)
            {
                Diagnostic? registration = result.Diagnostics.FirstOrDefault(diagnostic =>
                    diagnostic.Code == "project_registration_required");
                if (registration is null)
                    return 0;

                await WriteContextAsync(output, registration.Message);
                return 0;
            }

            ResolvedContext context = result.Value!;
            await WriteContextAsync(
                output,
                $"This session is inside the registered Rhino project \"{context.DisplayName}\" at worktree \"{context.WorktreePath}\". " +
                "Use the rhino-worktree-launcher MCP tools for Rhino launch and loaded-binary verification, and use them normally: a launch runs in a separate executor process started by the interactive Windows shell, and that process owns the plug-in registration, the Rhino start, verification, and the restore. " +
                "Do not launch Rhino.exe directly or edit plug-in registration. Ordinary editing, Git operations, and repository-owned tests that never start Rhino remain outside RWL. " +
                "A repository-owned harness that does start Rhino is not an exception: it competes for the same plug-in registration, so report it and ask before running it. " +
                "Concurrent launches mean several Rhino processes can be running, each a different build, so bind every post-launch check or interaction to the rhinoProcessId in the launch result, or ask the rhino_worktree_attribution tool which process holds which artifact. " +
                "A failed launch names the step that failed with a diagnostic code and gives the path of its JSONL log; quote that code when reporting the failure and read the log before retrying. " +
                "The codes interactive_spawn_unavailable and registry_seed_not_visible mean this host's own current-user registry writes are being intercepted rather than anything being wrong with the worktree: the fallback is to run the same launch as `rwl launch --path <worktree>` from an ordinary terminal outside this session, then report what it reports.");
            return 0;
        }
    }

    private static async Task WriteContextAsync(TextWriter output, string context) =>
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            hookSpecificOutput = new
            {
                hookEventName = "SessionStart",
                additionalContext = context
            }
        }, OutputJson));
}
