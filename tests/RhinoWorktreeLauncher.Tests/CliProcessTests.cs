using System.Diagnostics;
using System.Text.Json;

namespace RhinoWorktreeLauncher.Tests;

public sealed class CliProcessTests
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
    public async Task No_arguments_prints_usage_to_stderr()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();

        CliResult result = await RunAsync(temporary, Array.Empty<string>());

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(ExpectedUsage.ReplaceLineEndings() + Environment.NewLine, result.StandardError);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("Doctor")]
    public async Task Unknown_or_mis_cased_command_prints_usage(string command)
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();

        CliResult result = await RunAsync(temporary, new[] { command });

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(ExpectedUsage.ReplaceLineEndings() + Environment.NewLine, result.StandardError);
    }

    [Fact]
    public async Task Option_names_are_case_insensitive_and_json_results_stay_on_stdout()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();

        CliResult result = await RunAsync(
            temporary,
            new[] { "context", "--CWD", temporary.PathFor("working"), "--JSON" });

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        Assert.False(document.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.NotEmpty(document.RootElement.GetProperty("diagnostics").EnumerateArray());
    }

    [Fact]
    public async Task Value_lookup_can_take_another_option_token()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();

        CliResult result = await RunAsync(
            temporary,
            new[] { "context", "--cwd", "--json" });

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.StartsWith(
            "An error occurred trying to start process 'git' with working directory ",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.Contains("working\\--json", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_required_option_is_an_argument_error_even_with_json_requested()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();

        CliResult result = await RunAsync(
            temporary,
            new[] { "context", "--json" });

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal("Missing required option --cwd." + Environment.NewLine, result.StandardError);
    }

    [Fact]
    public async Task Hidden_session_context_route_survives_irrelevant_value_options_and_unknown_flags()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();

        CliResult result = await RunAsync(
            temporary,
            new[] { "session-context", "--PATH", "ignored", "--unknown", "--help", "--" },
            "{}");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task Timeout_validation_is_deferred_until_context_resolution_succeeds()
    {
        using TemporaryDirectory unrelated = new TemporaryDirectory();
        CliResult unresolved = await RunAsync(
            unrelated,
            new[]
            {
                "launch", "--path", unrelated.PathFor("working"),
                "--timeout", "not-a-number", "--json"
            });

        Assert.Equal(1, unresolved.ExitCode);
        Assert.Equal(string.Empty, unresolved.StandardError);
        using (JsonDocument document = JsonDocument.Parse(unresolved.StandardOutput))
            Assert.False(document.RootElement.GetProperty("succeeded").GetBoolean());

        using TemporaryDirectory registered = RepositoryFixture.Create();
        string repository = registered.PathFor("repository");
        CliResult registration = await RunAsync(
            registered,
            new[] { "project", "register", repository });
        Assert.Equal(0, registration.ExitCode);

        CliResult resolved = await RunAsync(
            registered,
            new[] { "launch", "--path", repository, "--timeout", "not-a-number", "--json" });

        Assert.Equal(2, resolved.ExitCode);
        Assert.Equal(string.Empty, resolved.StandardOutput);
        Assert.Equal("--timeout must be a positive number." + Environment.NewLine, resolved.StandardError);
    }

    // Read-only and machine-independent: whatever Rhino processes this machine has, the
    // command answers in the command-result shape and starts nothing.
    [Fact]
    public async Task Rhino_instances_answers_from_the_live_machine()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();

        CliResult result = await RunAsync(temporary, new[] { "rhino", "instances", "--json" });

        Assert.Equal(0, result.ExitCode);
        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        Assert.True(document.RootElement.GetProperty("succeeded").GetBoolean());
        JsonElement value = document.RootElement.GetProperty("value");
        Assert.True(value.GetProperty("observedAt").GetDateTimeOffset() > DateTimeOffset.MinValue);
        Assert.Equal(JsonValueKind.Array, value.GetProperty("instances").ValueKind);
    }

    [Theory]
    [InlineData("--configuration", "Debug")]
    [InlineData("--platform", "x64")]
    public async Task Configuration_and_platform_still_require_each_other(
        string option,
        string value)
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();

        CliResult result = await RunAsync(
            temporary,
            new[]
            {
                "project", "register", temporary.PathFor("working"),
                option, value, "--json"
            });

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(
            "--configuration and --platform must be supplied together." + Environment.NewLine,
            result.StandardError);
    }

    private static async Task<CliResult> RunAsync(
        TemporaryDirectory temporary,
        IEnumerable<string> arguments,
        string? standardInput = null)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(AppContext.BaseDirectory, "rwl-cli.exe"),
            WorkingDirectory = temporary.PathFor("working"),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Directory.CreateDirectory(startInfo.WorkingDirectory);
        startInfo.Environment["RWL_DATA_ROOT"] = temporary.PathFor("data");
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)!;
        if (standardInput is not null)
            await process.StandardInput.WriteAsync(standardInput);
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, await output, await error);
    }

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);
}
