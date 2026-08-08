using System.Text.Json;
using RhinoWorktreeLauncher;
using Rwl.Cli;

namespace RhinoWorktreeLauncher.Tests;

public sealed class SessionContextTests
{
    [Fact]
    public async Task Unrelated_directory_emits_no_session_context()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        LauncherBackend backend = CreateBackend(temporary);
        StringWriter output = new StringWriter();
        string unrelated = temporary.CreateDirectory("unrelated");

        await SessionContextWriter.WriteAsync(
            backend,
            new StringReader(JsonSerializer.Serialize(new { cwd = unrelated })),
            output);

        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task Compatible_unregistered_repository_requests_registration_without_running_driver()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        LauncherBackend backend = CreateBackend(temporary);
        StringWriter output = new StringWriter();

        await SessionContextWriter.WriteAsync(
            backend,
            new StringReader(JsonSerializer.Serialize(new { cwd = temporary.PathFor("repository") })),
            output);

        string context = ReadAdditionalContext(output);
        Assert.Contains("not registered", context, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rwl project register", context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registered_repository_routes_launches_through_mcp()
    {
        using TemporaryDirectory temporary = RepositoryFixture.Create();
        LauncherBackend backend = CreateBackend(temporary);
        await backend.RegisterProjectAsync(
            new ProjectRegistrationRequest(temporary.PathFor("repository"), ProjectAccessGrant.Full),
            CancellationToken.None);
        StringWriter output = new StringWriter();

        await SessionContextWriter.WriteAsync(
            backend,
            new StringReader(JsonSerializer.Serialize(new { cwd = temporary.PathFor("repository") })),
            output);

        string context = ReadAdditionalContext(output);
        Assert.Contains("rhino-worktree-launcher MCP tools", context, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(temporary.PathFor("repository")), context, StringComparison.OrdinalIgnoreCase);
    }

    private static LauncherBackend CreateBackend(TemporaryDirectory temporary) => new LauncherBackend(
        new LauncherBackendOptions
        {
            CatalogPath = temporary.PathFor("launcher/projects.json"),
            LogsDirectory = temporary.PathFor("launcher/logs")
        });

    private static string ReadAdditionalContext(StringWriter output)
    {
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        return document.RootElement
            .GetProperty("hookSpecificOutput")
            .GetProperty("additionalContext")
            .GetString()!;
    }
}
