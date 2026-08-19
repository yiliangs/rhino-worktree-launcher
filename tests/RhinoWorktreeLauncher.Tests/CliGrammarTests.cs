using RhinoWorktreeLauncher;
using Rwl.Cli;

namespace RhinoWorktreeLauncher.Tests;

public sealed class CliGrammarTests
{
    private const string ExpectedUsage =
        """
        Usage:
          rwl project register <path> [--plugin-project <path>] [--solution <path>] [--configuration <name> --platform <name>] [--direct] [--no-remote] [--json]
          rwl project remove <id> [--json]
          rwl context --cwd <path> [--json]
          rwl worktree list --project <id> [--local-only] [--json]
          rwl worktree inspect --path <path> [--json]
          rwl launch --path <path> [--timeout <seconds>] [--json]
          rwl rhino instances [--json]
          rwl doctor [--json]
          rwl integration status [claude|codex] [--json]
          rwl integration install <claude|codex> [--bootstrap <path>] [--no-session-context] [--json]
          rwl integration remove <claude|codex> [--json]
        """;

    [Fact]
    public void Every_supported_route_returns_its_typed_command_with_compatible_values()
    {
        (string[] Args, CliCommand Expected)[] routes =
        {
            (
                new[]
                {
                    "--JSON", "project", "--DIRECT", "register",
                    "--PLUGIN-PROJECT", "plugin", "--SOLUTION", "solution",
                    "--CONFIGURATION", " ", "--PLATFORM", "\t", "--NO-REMOTE", "repository"
                },
                new ProjectRegisterCommand(
                    "repository", "plugin", "solution", " ", "\t",
                    Direct: true, NoRemote: true, Json: true)),
            (
                new[] { "project", "remove", "project-id", "--json" },
                new ProjectRemoveCommand("project-id", Json: true)),
            (
                new[] { "context", "--CwD", "repository", "--JsOn" },
                new ContextCommand("repository", Json: true)),
            (
                new[] { "worktree", "list", "--project", "project-id", "--local-only", "--json" },
                new WorktreeListCommand("project-id", LocalOnly: true, Json: true)),
            (
                new[] { "worktree", "inspect", "--path", "repository", "--json" },
                new WorktreeInspectCommand("repository", Json: true)),
            (
                new[] { "launch", "--path", "repository", "--timeout", "not-a-number", "--json" },
                new LaunchCommand("repository", "not-a-number", Json: true)),
            (
                new[] { "rhino", "instances", "--json" },
                new RhinoInstancesCommand(Json: true)),
            (
                new[] { "doctor", "--json" },
                new DoctorCommand(Json: true)),
            (
                new[] { "integration", "status", "--json" },
                new IntegrationStatusCommand(null, Json: true)),
            (
                new[] { "integration", "status", "CLAUDE-CODE" },
                new IntegrationStatusCommand(McpClientKind.ClaudeCode, Json: false)),
            (
                new[]
                {
                    "integration", "install", "CoDeX", "--bootstrap", "bootstrap.exe",
                    "--no-session-context", "--json"
                },
                new IntegrationInstallCommand(
                    McpClientKind.Codex,
                    "bootstrap.exe",
                    NoSessionContext: true,
                    Json: true)),
            (
                new[] { "integration", "remove", "CLAUDE" },
                new IntegrationRemoveCommand(McpClientKind.ClaudeCode, Json: false)),
            (
                new[] { "session-context", "--json" },
                new SessionContextCommand(Json: true))
        };

        foreach ((string[] args, CliCommand expected) in routes)
            Assert.Equal(expected, CliGrammar.Parse(args));
    }

    [Fact]
    public void Usage_is_generated_in_the_compatible_shape() =>
        Assert.Equal(ExpectedUsage.ReplaceLineEndings(), CliGrammar.Usage);

    [Theory]
    [InlineData("--cwd")]
    [InlineData("--project")]
    [InlineData("--path")]
    [InlineData("--timeout")]
    [InlineData("--bootstrap")]
    [InlineData("--plugin-project")]
    [InlineData("--solution")]
    [InlineData("--configuration")]
    [InlineData("--platform")]
    public void Every_known_value_option_consumes_the_next_non_option_for_routing(string option)
    {
        CliCommand? parsed = CliGrammar.Parse(new[] { option, "ignored", "doctor" });

        Assert.Equal(new DoctorCommand(Json: false), parsed);
    }

    [Fact]
    public void Command_words_are_case_sensitive()
    {
        Assert.Null(CliGrammar.Parse(new[] { "Doctor" }));
        Assert.Null(CliGrammar.Parse(new[] { "project", "Register", "repository" }));
        Assert.Null(CliGrammar.Parse(new[] { "Integration", "status" }));
    }

    [Fact]
    public void Bare_double_dash_tokens_are_removed_without_becoming_help_or_assignments()
    {
        CliCommand? parsed = CliGrammar.Parse(
            new[] { "doctor", "--unknown", "--help", "--", "--json=true" });

        Assert.Equal(new DoctorCommand(Json: false), parsed);
    }

    [Fact]
    public void Unknown_options_do_not_consume_a_following_value()
    {
        Assert.Null(CliGrammar.Parse(new[] { "--unknown", "still-positional", "doctor" }));
    }

    [Fact]
    public void First_duplicate_value_wins()
    {
        CliCommand? parsed = CliGrammar.Parse(
            new[] { "context", "--cwd", "first", "--cwd", "second" });

        Assert.Equal(new ContextCommand("first", Json: false), parsed);
    }

    [Fact]
    public void Value_lookup_may_return_another_option_token()
    {
        CliCommand? parsed = CliGrammar.Parse(new[] { "context", "--cwd", "--json" });

        Assert.Equal(new ContextCommand("--json", Json: true), parsed);
    }

    [Theory]
    [MemberData(nameof(MissingCwdCases))]
    public void Missing_required_value_uses_the_exact_argument_error(string[] args)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => CliGrammar.Parse(args));

        Assert.Equal("Missing required option --cwd.", exception.Message);
    }

    public static TheoryData<string[]> MissingCwdCases => new TheoryData<string[]>
    {
        new[] { "context" },
        new[] { "context", "--cwd" },
        new[] { "context", "--cwd=repository" }
    };

    [Fact]
    public void Missing_optional_values_and_tail_fall_back_to_null()
    {
        Assert.Equal(
            new IntegrationStatusCommand(null, Json: false),
            CliGrammar.Parse(new[] { "integration", "status" }));
        Assert.Equal(
            new ProjectRegisterCommand(
                "repository", null, null, null, null,
                Direct: false, NoRemote: false, Json: false),
            CliGrammar.Parse(new[] { "project", "register", "repository", "--plugin-project" }));
        Assert.Equal(
            new LaunchCommand("repository", null, Json: false),
            CliGrammar.Parse(new[] { "launch", "--path", "repository", "--timeout" }));
    }

    [Fact]
    public void Configuration_and_platform_values_are_not_normalized()
    {
        ProjectRegisterCommand absent = Assert.IsType<ProjectRegisterCommand>(
            CliGrammar.Parse(new[] { "project", "register", "repository" }));
        ProjectRegisterCommand whitespace = Assert.IsType<ProjectRegisterCommand>(
            CliGrammar.Parse(new[]
            {
                "project", "register", "repository",
                "--configuration", " ", "--platform", "\t"
            }));

        Assert.Null(absent.Configuration);
        Assert.Null(absent.Platform);
        Assert.Equal(" ", whitespace.Configuration);
        Assert.Equal("\t", whitespace.Platform);
    }

    [Fact]
    public void Unknown_extra_positionals_return_usage_and_unknown_clients_keep_the_exact_error()
    {
        Assert.Null(CliGrammar.Parse(new[] { "doctor", "extra" }));

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            CliGrammar.Parse(new[] { "integration", "install", "cursor" }));
        Assert.Equal(
            "Unknown MCP client 'cursor'. Expected 'claude' or 'codex'.",
            exception.Message);
    }
}
