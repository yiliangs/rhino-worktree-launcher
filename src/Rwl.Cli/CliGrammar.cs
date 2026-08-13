using RhinoWorktreeLauncher;

namespace Rwl.Cli;

internal abstract record CliCommand;

internal sealed record ProjectRegisterCommand(
    string Path,
    string? PluginProjectPath,
    string? SolutionPath,
    string? Configuration,
    string? Platform,
    bool Direct,
    bool NoRemote,
    bool Json) : CliCommand;

internal sealed record ProjectRemoveCommand(string ProjectId, bool Json) : CliCommand;

internal sealed record ContextCommand(string WorkingDirectory, bool Json) : CliCommand;

internal sealed record WorktreeListCommand(
    string ProjectId,
    bool LocalOnly,
    bool Json) : CliCommand;

internal sealed record WorktreeInspectCommand(string Path, bool Json) : CliCommand;

internal sealed record LaunchCommand(
    string Path,
    string? Timeout,
    bool Json) : CliCommand;

internal sealed record DoctorCommand(bool Json) : CliCommand;

internal sealed record IntegrationStatusCommand(
    McpClientKind? Client,
    bool Json) : CliCommand;

internal sealed record IntegrationInstallCommand(
    McpClientKind Client,
    string? BootstrapPath,
    bool NoSessionContext,
    bool Json) : CliCommand;

internal sealed record IntegrationRemoveCommand(
    McpClientKind Client,
    bool Json) : CliCommand;

internal sealed record SessionContextCommand(bool Json) : CliCommand;

internal static class CliGrammar
{
    private const string ClientChoices = "claude|codex";

    private static readonly OptionSpec CwdOption = OptionSpec.Value("--cwd", "<path>");
    private static readonly OptionSpec ProjectOption = OptionSpec.Value("--project", "<id>");
    private static readonly OptionSpec PathOption = OptionSpec.Value("--path", "<path>");
    private static readonly OptionSpec TimeoutOption = OptionSpec.Value("--timeout", "<seconds>");
    private static readonly OptionSpec BootstrapOption = OptionSpec.Value("--bootstrap", "<path>");
    private static readonly OptionSpec PluginProjectOption = OptionSpec.Value("--plugin-project", "<path>");
    private static readonly OptionSpec SolutionOption = OptionSpec.Value("--solution", "<path>");
    private static readonly OptionSpec ConfigurationOption = OptionSpec.Value("--configuration", "<name>");
    private static readonly OptionSpec PlatformOption = OptionSpec.Value("--platform", "<name>");
    private static readonly OptionSpec DirectOption = OptionSpec.Flag("--direct");
    private static readonly OptionSpec NoRemoteOption = OptionSpec.Flag("--no-remote");
    private static readonly OptionSpec LocalOnlyOption = OptionSpec.Flag("--local-only");
    private static readonly OptionSpec NoSessionContextOption = OptionSpec.Flag("--no-session-context");
    private static readonly OptionSpec JsonOption = OptionSpec.Flag("--json");

    private static readonly CommandSpec[] Commands = CreateCommands();
    private static readonly HashSet<string> ValueOptionNames = Commands
        .SelectMany(command => command.OptionGroups)
        .SelectMany(group => group.Options)
        .Where(option => option.ConsumesValue)
        .Select(option => option.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static string Usage { get; } = string.Join(
        Environment.NewLine,
        new[] { "Usage:" }.Concat(Commands
            .Where(command => command.Visible)
            .Select(command => $"  rwl {command.Usage}")));

    public static CliCommand? Parse(string[] args)
    {
        Arguments arguments = new Arguments(args, ValueOptionNames);
        foreach (CommandSpec command in Commands)
        {
            CliCommand? parsed = command.TryCreate(arguments);
            if (parsed is not null)
                return parsed;
        }
        return null;
    }

    private static CommandSpec[] CreateCommands() =>
        new[]
        {
            new CommandSpec(
                new[]
                {
                    OperandSpec.Literal("project"),
                    OperandSpec.Literal("register"),
                    OperandSpec.Required("<path>")
                },
                new[]
                {
                    OptionGroupSpec.Optional(PluginProjectOption),
                    OptionGroupSpec.Optional(SolutionOption),
                    OptionGroupSpec.Optional(ConfigurationOption, PlatformOption),
                    OptionGroupSpec.Optional(DirectOption),
                    OptionGroupSpec.Optional(NoRemoteOption),
                    OptionGroupSpec.Optional(JsonOption)
                },
                (arguments, values) => new ProjectRegisterCommand(
                    values[0]!,
                    arguments.Optional(PluginProjectOption),
                    arguments.Optional(SolutionOption),
                    arguments.Optional(ConfigurationOption),
                    arguments.Optional(PlatformOption),
                    arguments.HasFlag(DirectOption),
                    arguments.HasFlag(NoRemoteOption),
                    arguments.HasFlag(JsonOption))),
            new CommandSpec(
                new[]
                {
                    OperandSpec.Literal("project"),
                    OperandSpec.Literal("remove"),
                    OperandSpec.Required("<id>")
                },
                new[] { OptionGroupSpec.Optional(JsonOption) },
                (arguments, values) => new ProjectRemoveCommand(
                    values[0]!,
                    arguments.HasFlag(JsonOption))),
            new CommandSpec(
                new[]
                {
                    OperandSpec.Literal("context")
                },
                new[]
                {
                    OptionGroupSpec.Required(CwdOption),
                    OptionGroupSpec.Optional(JsonOption)
                },
                (arguments, _) => new ContextCommand(
                    arguments.Required(CwdOption),
                    arguments.HasFlag(JsonOption))),
            new CommandSpec(
                new[]
                {
                    OperandSpec.Literal("worktree"),
                    OperandSpec.Literal("list")
                },
                new[]
                {
                    OptionGroupSpec.Required(ProjectOption),
                    OptionGroupSpec.Optional(LocalOnlyOption),
                    OptionGroupSpec.Optional(JsonOption)
                },
                (arguments, _) => new WorktreeListCommand(
                    arguments.Required(ProjectOption),
                    arguments.HasFlag(LocalOnlyOption),
                    arguments.HasFlag(JsonOption))),
            new CommandSpec(
                new[]
                {
                    OperandSpec.Literal("worktree"),
                    OperandSpec.Literal("inspect")
                },
                new[]
                {
                    OptionGroupSpec.Required(PathOption),
                    OptionGroupSpec.Optional(JsonOption)
                },
                (arguments, _) => new WorktreeInspectCommand(
                    arguments.Required(PathOption),
                    arguments.HasFlag(JsonOption))),
            new CommandSpec(
                new[] { OperandSpec.Literal("launch") },
                new[]
                {
                    OptionGroupSpec.Required(PathOption),
                    OptionGroupSpec.Optional(TimeoutOption),
                    OptionGroupSpec.Optional(JsonOption)
                },
                (arguments, _) => new LaunchCommand(
                    arguments.Required(PathOption),
                    arguments.Optional(TimeoutOption),
                    arguments.HasFlag(JsonOption))),
            new CommandSpec(
                new[] { OperandSpec.Literal("doctor") },
                new[] { OptionGroupSpec.Optional(JsonOption) },
                (arguments, _) => new DoctorCommand(arguments.HasFlag(JsonOption))),
            new CommandSpec(
                new[]
                {
                    OperandSpec.Literal("integration"),
                    OperandSpec.Literal("status"),
                    OperandSpec.Optional(ClientChoices)
                },
                new[] { OptionGroupSpec.Optional(JsonOption) },
                (arguments, values) => new IntegrationStatusCommand(
                    values[0] is string client ? ParseClient(client) : null,
                    arguments.HasFlag(JsonOption))),
            new CommandSpec(
                new[]
                {
                    OperandSpec.Literal("integration"),
                    OperandSpec.Literal("install"),
                    OperandSpec.Required($"<{ClientChoices}>")
                },
                new[]
                {
                    OptionGroupSpec.Optional(BootstrapOption),
                    OptionGroupSpec.Optional(NoSessionContextOption),
                    OptionGroupSpec.Optional(JsonOption)
                },
                (arguments, values) => new IntegrationInstallCommand(
                    ParseClient(values[0]!),
                    arguments.Optional(BootstrapOption),
                    arguments.HasFlag(NoSessionContextOption),
                    arguments.HasFlag(JsonOption))),
            new CommandSpec(
                new[]
                {
                    OperandSpec.Literal("integration"),
                    OperandSpec.Literal("remove"),
                    OperandSpec.Required($"<{ClientChoices}>")
                },
                new[] { OptionGroupSpec.Optional(JsonOption) },
                (arguments, values) => new IntegrationRemoveCommand(
                    ParseClient(values[0]!),
                    arguments.HasFlag(JsonOption))),
            new CommandSpec(
                new[] { OperandSpec.Literal("session-context") },
                Array.Empty<OptionGroupSpec>(),
                (arguments, _) => new SessionContextCommand(arguments.HasFlag(JsonOption)),
                Visible: false)
        };

    private static McpClientKind ParseClient(string value) => value.ToLowerInvariant() switch
    {
        "claude" or "claude-code" => McpClientKind.ClaudeCode,
        "codex" => McpClientKind.Codex,
        _ => throw new ArgumentException($"Unknown MCP client '{value}'. Expected 'claude' or 'codex'.")
    };

    private sealed class Arguments
    {
        private readonly string[] _args;

        public Arguments(string[] args, HashSet<string> valueOptionNames)
        {
            _args = args;
            Positionals = args
                .Where((argument, index) => !argument.StartsWith("--", StringComparison.Ordinal) &&
                    (index == 0 || !valueOptionNames.Contains(args[index - 1])))
                .ToArray();
        }

        public string[] Positionals { get; }

        public bool HasFlag(OptionSpec option) =>
            _args.Contains(option.Name, StringComparer.OrdinalIgnoreCase);

        public string Required(OptionSpec option) => Optional(option) ??
            throw new ArgumentException($"Missing required option {option.Name}.");

        public string? Optional(OptionSpec option)
        {
            for (int index = 0; index < _args.Length - 1; index++)
            {
                if (string.Equals(_args[index], option.Name, StringComparison.OrdinalIgnoreCase))
                    return _args[index + 1];
            }
            return null;
        }
    }

    private sealed record OptionSpec(string Name, string? ValueOperand)
    {
        public bool ConsumesValue => ValueOperand is not null;
        public string Usage => ValueOperand is null ? Name : $"{Name} {ValueOperand}";

        public static OptionSpec Flag(string name) => new OptionSpec(name, null);
        public static OptionSpec Value(string name, string valueOperand) => new OptionSpec(name, valueOperand);
    }

    private sealed record OptionGroupSpec(bool IsRequired, OptionSpec[] Options)
    {
        public string Usage
        {
            get
            {
                string usage = string.Join(" ", Options.Select(option => option.Usage));
                return IsRequired ? usage : $"[{usage}]";
            }
        }

        public static OptionGroupSpec Required(params OptionSpec[] options) =>
            new OptionGroupSpec(true, options);

        public static OptionGroupSpec Optional(params OptionSpec[] options) =>
            new OptionGroupSpec(false, options);
    }

    private sealed record OperandSpec(string Usage, string? LiteralValue, bool IsOptional)
    {
        public bool IsLiteral => LiteralValue is not null;
        public string RenderedUsage => IsOptional ? $"[{Usage}]" : Usage;

        public static OperandSpec Literal(string value) => new OperandSpec(value, value, false);
        public static OperandSpec Required(string usage) => new OperandSpec(usage, null, false);
        public static OperandSpec Optional(string usage) => new OperandSpec(usage, null, true);
    }

    private sealed record CommandSpec(
        OperandSpec[] Operands,
        OptionGroupSpec[] OptionGroups,
        Func<Arguments, string?[], CliCommand> Factory,
        bool Visible = true)
    {
        public string Usage => string.Join(
            " ",
            Operands.Select(operand => operand.RenderedUsage)
                .Concat(OptionGroups.Select(group => group.Usage)));

        public CliCommand? TryCreate(Arguments arguments)
        {
            int requiredCount = Operands.Count(operand => !operand.IsOptional);
            if (arguments.Positionals.Length < requiredCount ||
                arguments.Positionals.Length > Operands.Length)
            {
                return null;
            }

            string?[] values = new string?[Operands.Count(operand => !operand.IsLiteral)];
            int valueIndex = 0;
            for (int index = 0; index < Operands.Length; index++)
            {
                OperandSpec operand = Operands[index];
                if (index >= arguments.Positionals.Length)
                {
                    if (!operand.IsLiteral)
                        values[valueIndex++] = null;
                    continue;
                }

                string positional = arguments.Positionals[index];
                if (operand.IsLiteral)
                {
                    if (!string.Equals(positional, operand.LiteralValue, StringComparison.Ordinal))
                        return null;
                }
                else
                {
                    values[valueIndex++] = positional;
                }
            }

            return Factory(arguments, values);
        }
    }
}
