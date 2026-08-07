using System.Text.Json;
using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class ClaudeIntegrationTests
{
    [Fact]
    public async Task Install_and_remove_preserve_unrelated_Claude_settings_and_are_idempotent()
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
        ClaudeIntegrationManager manager = new ClaudeIntegrationManager(settingsPath, statePath);
        string bootstrap = temporary.PathFor("bootstrap/rwl.exe");

        await manager.InstallAsync(bootstrap, CancellationToken.None);
        await manager.InstallAsync(bootstrap, CancellationToken.None);

        using (JsonDocument settings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath)))
        {
            Assert.Equal("dark", settings.RootElement.GetProperty("theme").GetString());
            JsonElement[] hooks = settings.RootElement.GetProperty("hooks")
                .GetProperty("SessionStart")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(2, hooks.Length);
            Assert.Equal("existing-hook", hooks[0].GetProperty("hooks")[0].GetProperty("command").GetString());
        }
        using (JsonDocument state = JsonDocument.Parse(await File.ReadAllTextAsync(statePath)))
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

        await manager.RemoveAsync(CancellationToken.None);

        using JsonDocument removedSettings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
        Assert.Single(removedSettings.RootElement.GetProperty("hooks").GetProperty("SessionStart").EnumerateArray());
        using JsonDocument removedState = JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
        Assert.True(removedState.RootElement.GetProperty("mcpServers").TryGetProperty("existing-server", out _));
        Assert.False(removedState.RootElement.GetProperty("mcpServers").TryGetProperty("rhino-worktree-launcher", out _));
    }
}
