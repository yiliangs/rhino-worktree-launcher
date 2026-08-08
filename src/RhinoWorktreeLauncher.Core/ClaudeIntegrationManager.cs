using System.Text.Json;
using System.Text.Json.Nodes;

namespace RhinoWorktreeLauncher;

public sealed class ClaudeIntegrationManager
{
    public const string McpServerName = "rhino-worktree-launcher";
    private readonly string _settingsPath;
    private readonly string _statePath;

    public ClaudeIntegrationManager(string? settingsPath = null, string? statePath = null)
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _settingsPath = Path.GetFullPath(settingsPath ?? Path.Combine(userProfile, ".claude", "settings.json"));
        _statePath = Path.GetFullPath(statePath ?? Path.Combine(userProfile, ".claude.json"));
    }

    public async Task InstallAsync(string bootstrapPath, CancellationToken cancellationToken)
    {
        string fullBootstrapPath = Path.GetFullPath(bootstrapPath);
        JsonObject settings = await ReadObjectAsync(_settingsPath, cancellationToken);
        JsonObject hooks = GetOrCreateObject(settings, "hooks");
        JsonArray sessionStart = GetOrCreateArray(hooks, "SessionStart");
        _ = RemoveOwnedHooks(sessionStart);
        sessionStart.Add(new JsonObject
        {
            ["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = $"\"{fullBootstrapPath}\" session-context",
                    ["timeout"] = 15
                }
            }
        });
        await WriteAtomicAsync(_settingsPath, settings, cancellationToken);

        JsonObject state = await ReadObjectAsync(_statePath, cancellationToken);
        JsonObject mcpServers = GetOrCreateObject(state, "mcpServers");
        mcpServers[McpServerName] = new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = fullBootstrapPath,
            ["args"] = new JsonArray("mcp")
        };
        await WriteAtomicAsync(_statePath, state, cancellationToken);
    }

    public async Task RemoveAsync(CancellationToken cancellationToken)
    {
        JsonObject settings = await ReadObjectAsync(_settingsPath, cancellationToken);
        if (settings["hooks"] is JsonObject hooks &&
            hooks["SessionStart"] is JsonArray sessionStart &&
            RemoveOwnedHooks(sessionStart) > 0)
        {
            if (sessionStart.Count == 0)
                _ = hooks.Remove("SessionStart");
            if (hooks.Count == 0)
                _ = settings.Remove("hooks");
        }
        await WriteAtomicAsync(_settingsPath, settings, cancellationToken);

        JsonObject state = await ReadObjectAsync(_statePath, cancellationToken);
        if (state["mcpServers"] is JsonObject mcpServers &&
            mcpServers.Remove(McpServerName) &&
            mcpServers.Count == 0)
        {
            _ = state.Remove("mcpServers");
        }
        await WriteAtomicAsync(_statePath, state, cancellationToken);
    }

    private static int RemoveOwnedHooks(JsonArray sessionStart)
    {
        int removed = 0;
        for (int index = sessionStart.Count - 1; index >= 0; index--)
        {
            if (!IsOwnedHook(sessionStart[index]))
                continue;

            sessionStart.RemoveAt(index);
            removed++;
        }
        return removed;
    }

    private static bool IsOwnedHook(JsonNode? node)
    {
        if (node is not JsonObject entry || entry["hooks"] is not JsonArray hooks)
            return false;
        foreach (JsonNode? hookNode in hooks)
        {
            string command = hookNode?["command"]?.GetValue<string>() ?? string.Empty;
            if (command.Contains("session-context", StringComparison.OrdinalIgnoreCase) &&
                (command.Contains("rwl.exe", StringComparison.OrdinalIgnoreCase) ||
                 command.Contains("rwl-bootstrap.exe", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<JsonObject> ReadObjectAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return new JsonObject();
        string json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonNode.Parse(json) as JsonObject ??
            throw new InvalidDataException($"Claude configuration '{path}' must contain a JSON object.");
    }

    private static JsonObject GetOrCreateObject(JsonObject owner, string name)
    {
        if (owner[name] is JsonObject existing)
            return existing;
        JsonObject created = new JsonObject();
        owner[name] = created;
        return created;
    }

    private static JsonArray GetOrCreateArray(JsonObject owner, string name)
    {
        if (owner[name] is JsonArray existing)
            return existing;
        JsonArray created = new JsonArray();
        owner[name] = created;
        return created;
    }

    private static async Task WriteAtomicAsync(
        string path,
        JsonObject value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
            File.Copy(path, path + ".rwl-backup", overwrite: true);

        string temporaryPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                value.ToJsonString(JsonDefaults.Write),
                cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
