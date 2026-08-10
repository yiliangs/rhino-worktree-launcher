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
            Arguments arguments = new Arguments(args);
            LauncherBackend backend = new LauncherBackend();
            return arguments.Positionals switch
            {
                ["project", "register", string path] => await RegisterProjectAsync(backend, arguments, path),
                ["project", "remove", string projectId] => await WriteAsync(
                    await backend.RemoveProjectAsync(projectId, CancellationToken.None),
                    arguments.Json,
                    _ => $"Removed project '{projectId}'."),
                ["context"] => await WriteAsync(
                    await backend.ResolveContextAsync(
                        arguments.RequiredOption("--cwd"),
                        CancellationToken.None),
                    arguments.Json,
                    value => $"{value.DisplayName}: {value.WorktreePath}"),
                ["worktree", "list"] => await WriteAsync(
                    await backend.GetWorktreeSnapshotAsync(
                        arguments.RequiredOption("--project"),
                        includeRemote: !arguments.HasFlag("--local-only"),
                        CancellationToken.None),
                    arguments.Json,
                    value => string.Join(Environment.NewLine, value.Worktrees.Select(worktree => worktree.Path))),
                ["worktree", "inspect"] => await WriteAsync(
                    await backend.InspectWorktreeAsync(
                        arguments.RequiredOption("--path"),
                        CancellationToken.None),
                    arguments.Json,
                    value => value.CanLaunch ? "Ready to launch." : "Not ready to launch."),
                ["launch"] => await LaunchAsync(backend, arguments),
                ["doctor"] => await DoctorAsync(backend, arguments),
                ["integration", "status"] => await IntegrationStatusAsync(arguments, client: null),
                ["integration", "status", string client] => await IntegrationStatusAsync(arguments, ParseClient(client)),
                ["integration", "install", string client] => await InstallIntegrationAsync(arguments, ParseClient(client)),
                ["integration", "remove", string client] => await RemoveIntegrationAsync(arguments, ParseClient(client)),
                ["session-context"] => await SessionContextWriter.WriteAsync(
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
        Arguments arguments,
        string path)
    {
        string? configuration = arguments.OptionalOption("--configuration", null);
        string? platform = arguments.OptionalOption("--platform", null);
        if (string.IsNullOrWhiteSpace(configuration) != string.IsNullOrWhiteSpace(platform))
            throw new ArgumentException("--configuration and --platform must be supplied together.");

        BuildConfiguration? selection = configuration is null
            ? null
            : new BuildConfiguration(configuration, platform!);
        return await WriteAsync(
            await backend.RegisterProjectAsync(
                new ProjectRegistrationRequest(
                    path,
                    new ProjectAccessGrant(true, !arguments.HasFlag("--no-remote")),
                    arguments.OptionalOption("--plugin-project", null),
                    arguments.OptionalOption("--solution", null),
                    selection,
                    arguments.HasFlag("--direct") ? LaunchMode.DirectLaunch : LaunchMode.BuildAndLaunch),
                CancellationToken.None),
            arguments.Json,
            value => $"Registered '{value.ProjectId}' with {value.BuildProfile.SolutionPath} " +
                $"({value.BuildProfile.SelectedConfiguration.DisplayName}, {value.BuildProfile.LaunchMode}).");
    }

    private static async Task<int> LaunchAsync(LauncherBackend backend, Arguments arguments)
    {
        string path = arguments.RequiredOption("--path");
        CommandResult<ResolvedContext> context = await backend.ResolveContextAsync(
            path,
            CancellationToken.None);
        if (!context.Succeeded)
        {
            return await WriteAsync(
                context,
                arguments.Json,
                value => $"{value.DisplayName}: {value.WorktreePath}");
        }

        double timeoutSeconds = arguments.OptionalDouble("--timeout", 180);
        Progress<LaunchProgress>? progress = arguments.Json
            ? null
            : new Progress<LaunchProgress>(update => Console.Error.WriteLine($"[{update.Stage}] {update.Message}"));
        return await WriteAsync(
            await backend.LaunchAsync(
                path,
                context.Value!.BuildProfile.LaunchMode,
                TimeSpan.FromSeconds(timeoutSeconds),
                progress,
                CancellationToken.None),
            arguments.Json,
            value => value.Status == LaunchStatus.Succeeded
                ? $"Verified {value.PluginPath} in Rhino process {value.RhinoProcessId}."
                : $"Launch failed. Diagnostics: {value.DiagnosticsLogPath}");
    }

    private static async Task<int> DoctorAsync(LauncherBackend backend, Arguments arguments)
    {
        CommandResult<DoctorReport> result = await backend.RunDoctorAsync(CancellationToken.None);
        int adapterExit = await WriteAsync(
            result,
            arguments.Json,
            value => value.Healthy ? "RWL doctor passed." : "RWL doctor found required failures.");
        return adapterExit == 0 && result.Value?.Healthy == true ? 0 : 1;
    }

    private static async Task<int> InstallIntegrationAsync(
        Arguments arguments,
        McpClientKind client)
    {
        string bootstrap = arguments.OptionalOption(
            "--bootstrap",
            Environment.GetEnvironmentVariable("RWL_BOOTSTRAP_PATH") ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RhinoWorktreeLauncher",
                "bootstrap",
                "rwl.exe"));
        McpClientIntegrationManager manager = new McpClientIntegrationManager();
        McpClientIntegrationStatus status = await manager.InstallAsync(
            client,
            bootstrap,
            installSessionContext: client == McpClientKind.ClaudeCode &&
                !arguments.HasFlag("--no-session-context"),
            CancellationToken.None);
        WriteIntegrationStatus(status, arguments.Json);
        return 0;
    }

    private static async Task<int> RemoveIntegrationAsync(
        Arguments arguments,
        McpClientKind client)
    {
        McpClientIntegrationManager manager = new McpClientIntegrationManager();
        McpClientIntegrationStatus status = await manager.RemoveAsync(client, CancellationToken.None);
        WriteIntegrationStatus(status, arguments.Json);
        return 0;
    }

    private static async Task<int> IntegrationStatusAsync(
        Arguments arguments,
        McpClientKind? client)
    {
        McpClientIntegrationManager manager = new McpClientIntegrationManager();
        McpClientKind[] clients = client is null
            ? new[] { McpClientKind.ClaudeCode, McpClientKind.Codex }
            : new[] { client.Value };
        McpClientIntegrationStatus[] statuses = await Task.WhenAll(clients.Select(candidate =>
            manager.GetStatusAsync(candidate, CancellationToken.None)));
        if (arguments.Json)
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

    private static McpClientKind ParseClient(string value) => value.ToLowerInvariant() switch
    {
        "claude" or "claude-code" => McpClientKind.ClaudeCode,
        "codex" => McpClientKind.Codex,
        _ => throw new ArgumentException($"Unknown MCP client '{value}'. Expected 'claude' or 'codex'.")
    };

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
        Console.Error.WriteLine(
            """
            Usage:
              rwl project register <path> [--plugin-project <path>] [--solution <path>] [--configuration <name> --platform <name>] [--direct] [--no-remote] [--json]
              rwl project remove <id> [--json]
              rwl context --cwd <path> [--json]
              rwl worktree list --project <id> [--local-only] [--json]
              rwl worktree inspect --path <path> [--json]
              rwl launch --path <path> [--timeout <seconds>] [--json]
              rwl doctor [--json]
              rwl integration status [claude|codex] [--json]
              rwl integration install <claude|codex> [--bootstrap <path>] [--no-session-context] [--json]
              rwl integration remove <claude|codex> [--json]
            """);
        return 2;
    }

    private sealed class Arguments
    {
        private readonly string[] _args;

        public Arguments(string[] args)
        {
            _args = args;
            Positionals = args
                .Where((argument, index) => !argument.StartsWith("--", StringComparison.Ordinal) &&
                    (index == 0 || !OptionConsumesValue(args[index - 1])))
                .ToArray();
        }

        public string[] Positionals { get; }
        public bool Json => HasFlag("--json");

        public bool HasFlag(string name) => _args.Contains(name, StringComparer.OrdinalIgnoreCase);

        public string RequiredOption(string name) => OptionalOption(name, null) ??
            throw new ArgumentException($"Missing required option {name}.");

        public string OptionalOption(string name, string? fallback)
        {
            for (int index = 0; index < _args.Length - 1; index++)
            {
                if (string.Equals(_args[index], name, StringComparison.OrdinalIgnoreCase))
                    return _args[index + 1];
            }
            return fallback!;
        }

        public double OptionalDouble(string name, double fallback)
        {
            string? value = OptionalOption(name, null);
            return value is null
                ? fallback
                : double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out double parsed) && parsed > 0
                    ? parsed
                    : throw new ArgumentException($"{name} must be a positive number.");
        }

        private static bool OptionConsumesValue(string option) => option.Equals("--cwd", StringComparison.OrdinalIgnoreCase) ||
            option.Equals("--project", StringComparison.OrdinalIgnoreCase) ||
            option.Equals("--path", StringComparison.OrdinalIgnoreCase) ||
            option.Equals("--timeout", StringComparison.OrdinalIgnoreCase) ||
            option.Equals("--bootstrap", StringComparison.OrdinalIgnoreCase) ||
            option.Equals("--plugin-project", StringComparison.OrdinalIgnoreCase) ||
            option.Equals("--solution", StringComparison.OrdinalIgnoreCase) ||
            option.Equals("--configuration", StringComparison.OrdinalIgnoreCase) ||
            option.Equals("--platform", StringComparison.OrdinalIgnoreCase);
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
                "Use the rhino-worktree-launcher MCP tools for Rhino launch and loaded-binary verification. " +
                "Do not launch Rhino.exe directly or edit plug-in registration. Ordinary editing, Git operations, and repository-owned headless verification remain outside RWL.");
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
