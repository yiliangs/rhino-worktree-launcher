using System.Text.Json;
using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class McpClientIntegrationTests
{
    [Fact]
    public async Task Claude_install_and_remove_preserve_unrelated_settings_and_are_idempotent()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string settingsPath = temporary.PathFor(".claude/settings.json");
        string statePath = temporary.PathFor(".claude.json");
        temporary.WriteFile(
            ".claude/settings.json",
            """
            {
              "theme": "dark",
              "hooks": {
                "SessionStart": [
                  { "hooks": [ { "type": "command", "command": "existing-hook" } ] }
                ]
              }
            }
            """);
        temporary.WriteFile(
            ".claude.json",
            """
            {
              "mcpServers": {
                "existing-server": { "type": "stdio", "command": "existing.exe" }
              },
              "unrelated": 42
            }
            """);
        string bootstrap = temporary.PathFor("bootstrap/rwl.exe");
        temporary.WriteFile("bootstrap/rwl.exe", string.Empty);
        McpClientIntegrationManager manager = new McpClientIntegrationManager(
            settingsPath,
            statePath,
            temporary.PathFor(".codex/config.toml"));

        await manager.InstallAsync(
            McpClientKind.ClaudeCode,
            bootstrap,
            installSessionContext: true,
            CancellationToken.None);
        string firstSettings = await File.ReadAllTextAsync(settingsPath);
        string firstState = await File.ReadAllTextAsync(statePath);
        McpClientIntegrationStatus status = await manager.InstallAsync(
            McpClientKind.ClaudeCode,
            bootstrap,
            installSessionContext: true,
            CancellationToken.None);

        Assert.Equal(firstSettings, await File.ReadAllTextAsync(settingsPath));
        Assert.Equal(firstState, await File.ReadAllTextAsync(statePath));
        Assert.True(status.Ready);
        Assert.True(status.SessionContextConfigured);
        using (JsonDocument settings = JsonDocument.Parse(firstSettings))
        {
            Assert.Equal("dark", settings.RootElement.GetProperty("theme").GetString());
            JsonElement[] hooks = settings.RootElement.GetProperty("hooks")
                .GetProperty("SessionStart")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(2, hooks.Length);
            Assert.Equal("existing-hook", hooks[0].GetProperty("hooks")[0].GetProperty("command").GetString());
        }
        using (JsonDocument state = JsonDocument.Parse(firstState))
        {
            Assert.Equal(42, state.RootElement.GetProperty("unrelated").GetInt32());
            Assert.True(state.RootElement.GetProperty("mcpServers").TryGetProperty("existing-server", out _));
            Assert.Equal(
                Path.GetFullPath(bootstrap),
                state.RootElement.GetProperty("mcpServers")
                    .GetProperty("rhino-worktree-launcher")
                    .GetProperty("command")
                    .GetString());
        }

        await manager.RemoveAsync(McpClientKind.ClaudeCode, CancellationToken.None);

        using JsonDocument removedSettings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
        Assert.Single(removedSettings.RootElement.GetProperty("hooks").GetProperty("SessionStart").EnumerateArray());
        using JsonDocument removedState = JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
        Assert.True(removedState.RootElement.GetProperty("mcpServers").TryGetProperty("existing-server", out _));
        Assert.False(removedState.RootElement.GetProperty("mcpServers").TryGetProperty("rhino-worktree-launcher", out _));
    }

    [Fact]
    public async Task Codex_install_and_remove_preserve_unrelated_TOML_and_set_launch_timeout()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string configPath = temporary.PathFor(".codex/config.toml");
        temporary.WriteFile(
            ".codex/config.toml",
            """
            model = "gpt-5"
            # Keep this comment.

            [mcp_servers.existing]
            command = "existing.exe"
            """ + Environment.NewLine);
        string bootstrap = temporary.PathFor("bootstrap/rwl.exe");
        temporary.WriteFile("bootstrap/rwl.exe", string.Empty);
        McpClientIntegrationManager manager = new McpClientIntegrationManager(
            temporary.PathFor(".claude/settings.json"),
            temporary.PathFor(".claude.json"),
            configPath);

        await manager.InstallAsync(
            McpClientKind.Codex,
            bootstrap,
            installSessionContext: false,
            CancellationToken.None);
        string first = await File.ReadAllTextAsync(configPath);
        McpClientIntegrationStatus status = await manager.InstallAsync(
            McpClientKind.Codex,
            bootstrap,
            installSessionContext: false,
            CancellationToken.None);

        Assert.Equal(first, await File.ReadAllTextAsync(configPath));
        Assert.True(status.Ready);
        Assert.False(status.SessionContextSupported);
        Assert.Contains("# Keep this comment.", first);
        Assert.Contains("[mcp_servers.existing]", first);
        Assert.Contains("[mcp_servers.rhino-worktree-launcher]", first);
        Assert.Contains("args = [\"mcp\"]", first);
        Assert.Contains($"tool_timeout_sec = {McpClientIntegrationManager.ToolTimeoutSeconds}", first);

        await manager.RemoveAsync(McpClientKind.Codex, CancellationToken.None);

        string removed = await File.ReadAllTextAsync(configPath);
        Assert.Contains("# Keep this comment.", removed);
        Assert.Contains("[mcp_servers.existing]", removed);
        Assert.DoesNotContain("[mcp_servers.rhino-worktree-launcher]", removed);
    }

    [Fact]
    public async Task Claude_install_can_leave_session_context_explicitly_disabled()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string bootstrap = temporary.PathFor("bootstrap/rwl.exe");
        temporary.WriteFile("bootstrap/rwl.exe", string.Empty);
        McpClientIntegrationManager manager = new McpClientIntegrationManager(
            temporary.PathFor(".claude/settings.json"),
            temporary.PathFor(".claude.json"),
            temporary.PathFor(".codex/config.toml"));

        McpClientIntegrationStatus status = await manager.InstallAsync(
            McpClientKind.ClaudeCode,
            bootstrap,
            installSessionContext: false,
            CancellationToken.None);

        Assert.True(status.Ready);
        Assert.False(status.SessionContextConfigured);
    }
}
