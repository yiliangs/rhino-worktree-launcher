using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RhinoWorktreeLauncher;

[JsonConverter(typeof(JsonStringEnumConverter<McpClientKind>))]
public enum McpClientKind
{
    ClaudeCode,
    Codex
}

public sealed record McpClientIntegrationStatus(
    McpClientKind Client,
    bool McpConfigured,
    bool SessionContextSupported,
    bool SessionContextConfigured,
    string? BootstrapPath,
    bool BootstrapAvailable)
{
    public bool Ready => McpConfigured && BootstrapAvailable;
}

public sealed class McpClientIntegrationManager
{
    public const string McpServerName = "rhino-worktree-launcher";
    public const int ToolTimeoutSeconds = 300;

    private const string CodexSection = "[mcp_servers.rhino-worktree-launcher]";
    private static readonly Regex TomlSectionPattern = new Regex(
        @"(?m)^\s*\[(?<name>[^\]\r\n]+)\]\s*(?:#.*)?$",
        RegexOptions.CultureInvariant);
    private static readonly Regex TomlCommandPattern = new Regex(
        """(?m)^\s*command\s*=\s*(?<value>"(?:\\.|[^"])*")\s*(?:#.*)?$""",
        RegexOptions.CultureInvariant);
    private static readonly Regex TomlMcpArgumentPattern = new Regex(
        """(?m)^\s*args\s*=\s*\[\s*"mcp"\s*\]\s*(?:#.*)?$""",
        RegexOptions.CultureInvariant);

    private readonly string _claudeSettingsPath;
    private readonly string _claudeStatePath;
    private readonly string _codexConfigPath;

    public McpClientIntegrationManager(
        string? claudeSettingsPath = null,
        string? claudeStatePath = null,
        string? codexConfigPath = null)
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _claudeSettingsPath = Path.GetFullPath(claudeSettingsPath ??
            Path.Combine(userProfile, ".claude", "settings.json"));
        _claudeStatePath = Path.GetFullPath(claudeStatePath ??
            Path.Combine(userProfile, ".claude.json"));
        _codexConfigPath = Path.GetFullPath(codexConfigPath ??
            Path.Combine(userProfile, ".codex", "config.toml"));
    }

    public async Task<McpClientIntegrationStatus> InstallAsync(
        McpClientKind client,
        string bootstrapPath,
        bool installSessionContext,
        CancellationToken cancellationToken)
    {
        string fullBootstrapPath = Path.GetFullPath(bootstrapPath);
        if (!File.Exists(fullBootstrapPath))
        {
            throw new FileNotFoundException(
                "Install Rhino Worktree Launcher before configuring an MCP client.",
                fullBootstrapPath);
        }

        switch (client)
        {
            case McpClientKind.ClaudeCode:
                await InstallClaudeAsync(fullBootstrapPath, installSessionContext, cancellationToken);
                break;
            case McpClientKind.Codex:
                if (installSessionContext)
                    throw new ArgumentException("Codex does not support RWL's Claude SessionStart context hook.");
                await InstallCodexAsync(fullBootstrapPath, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(client), client, "Unknown MCP client.");
        }

        return await GetStatusAsync(client, cancellationToken);
    }

    public async Task<McpClientIntegrationStatus> RemoveAsync(
        McpClientKind client,
        CancellationToken cancellationToken)
    {
        switch (client)
        {
            case McpClientKind.ClaudeCode:
                await RemoveClaudeAsync(cancellationToken);
                break;
            case McpClientKind.Codex:
                await RemoveCodexAsync(cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(client), client, "Unknown MCP client.");
        }

        return await GetStatusAsync(client, cancellationToken);
    }

    public Task<McpClientIntegrationStatus> GetStatusAsync(
        McpClientKind client,
        CancellationToken cancellationToken) => client switch
        {
            McpClientKind.ClaudeCode => GetClaudeStatusAsync(cancellationToken),
            McpClientKind.Codex => GetCodexStatusAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(client), client, "Unknown MCP client.")
        };

    public static string ClientId(McpClientKind client) => client switch
    {
        McpClientKind.ClaudeCode => "claude",
        McpClientKind.Codex => "codex",
        _ => throw new ArgumentOutOfRangeException(nameof(client), client, "Unknown MCP client.")
    };

    public static string DisplayName(McpClientKind client) => client switch
    {
        McpClientKind.ClaudeCode => "Claude Code",
        McpClientKind.Codex => "Codex",
        _ => throw new ArgumentOutOfRangeException(nameof(client), client, "Unknown MCP client.")
    };

    private async Task InstallClaudeAsync(
        string bootstrapPath,
        bool installSessionContext,
        CancellationToken cancellationToken)
    {
        JsonObject state = await ReadObjectAsync(_claudeStatePath, cancellationToken);
        bool settingsExisted = File.Exists(_claudeSettingsPath);
        JsonObject settings = await ReadObjectAsync(_claudeSettingsPath, cancellationToken);

        JsonObject mcpServers = GetOrCreateObject(state, "mcpServers");
        mcpServers[McpServerName] = new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = bootstrapPath,
            ["args"] = new JsonArray("mcp")
        };
        RemoveOwnedClaudeHook(settings);
        if (installSessionContext)
        {
            JsonArray sessionStart = GetOrCreateArray(
                GetOrCreateObject(settings, "hooks"),
                "SessionStart");
            sessionStart.Add(new JsonObject
            {
                ["hooks"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = $"\"{bootstrapPath}\" session-context",
                        ["timeout"] = 15
                    }
                }
            });
        }

        await WriteJsonIfChangedAsync(_claudeStatePath, state, cancellationToken);
        if (settingsExisted || installSessionContext)
            await WriteJsonIfChangedAsync(_claudeSettingsPath, settings, cancellationToken);
    }

    private async Task InstallCodexAsync(string bootstrapPath, CancellationToken cancellationToken)
    {
        string existing = await ReadTextAsync(_codexConfigPath, cancellationToken);
        string withoutOwnedSection = RemoveCodexSection(existing).TrimEnd();
        string canonical = string.Join(Environment.NewLine, new[]
        {
            CodexSection,
            $"command = {JsonSerializer.Serialize(bootstrapPath)}",
            "args = [\"mcp\"]",
            $"tool_timeout_sec = {ToolTimeoutSeconds}"
        });
        string updated = string.IsNullOrWhiteSpace(withoutOwnedSection)
            ? canonical + Environment.NewLine
            : withoutOwnedSection + Environment.NewLine + Environment.NewLine + canonical + Environment.NewLine;
        await WriteTextIfChangedAsync(_codexConfigPath, updated, cancellationToken);
    }

    private async Task RemoveClaudeAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_claudeSettingsPath))
        {
            JsonObject settings = await ReadObjectAsync(_claudeSettingsPath, cancellationToken);
            RemoveOwnedClaudeHook(settings);
            await WriteJsonIfChangedAsync(_claudeSettingsPath, settings, cancellationToken);
        }

        if (File.Exists(_claudeStatePath))
        {
            JsonObject state = await ReadObjectAsync(_claudeStatePath, cancellationToken);
            if (state["mcpServers"] is JsonObject mcpServers &&
                mcpServers.Remove(McpServerName) &&
                mcpServers.Count == 0)
            {
                _ = state.Remove("mcpServers");
            }
            await WriteJsonIfChangedAsync(_claudeStatePath, state, cancellationToken);
        }
    }

    private async Task RemoveCodexAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_codexConfigPath))
            return;
        string existing = await ReadTextAsync(_codexConfigPath, cancellationToken);
        string withoutOwnedSection = RemoveCodexSection(existing).TrimEnd();
        await WriteTextIfChangedAsync(
            _codexConfigPath,
            string.IsNullOrWhiteSpace(withoutOwnedSection)
                ? string.Empty
                : withoutOwnedSection + Environment.NewLine,
            cancellationToken);
    }

    private async Task<McpClientIntegrationStatus> GetClaudeStatusAsync(
        CancellationToken cancellationToken)
    {
        JsonObject state = await ReadObjectAsync(_claudeStatePath, cancellationToken);
        JsonObject settings = await ReadObjectAsync(_claudeSettingsPath, cancellationToken);
        JsonObject? server = state["mcpServers"]?[McpServerName] as JsonObject;
        string? bootstrapPath = server?["command"]?.GetValue<string>();
        bool configured = server?["type"]?.GetValue<string>() == "stdio" &&
            server?["args"] is JsonArray args &&
            args.Any(argument => argument?.GetValue<string>() == "mcp");
        return new McpClientIntegrationStatus(
            McpClientKind.ClaudeCode,
            configured,
            SessionContextSupported: true,
            SessionContextConfigured: HasOwnedClaudeHook(settings),
            bootstrapPath,
            BootstrapAvailable: bootstrapPath is not null && File.Exists(bootstrapPath));
    }

    private async Task<McpClientIntegrationStatus> GetCodexStatusAsync(
        CancellationToken cancellationToken)
    {
        string config = await ReadTextAsync(_codexConfigPath, cancellationToken);
        string? section = GetCodexSection(config);
        string? bootstrapPath = null;
        if (section is not null && TomlCommandPattern.Match(section) is { Success: true } command)
        {
            try
            {
                bootstrapPath = JsonSerializer.Deserialize<string>(command.Groups["value"].Value);
            }
            catch (JsonException)
            {
                // Report the owned entry as not configured when its string is malformed.
            }
        }
        bool configured = section is not null &&
            bootstrapPath is not null &&
            TomlMcpArgumentPattern.IsMatch(section);
        return new McpClientIntegrationStatus(
            McpClientKind.Codex,
            configured,
            SessionContextSupported: false,
            SessionContextConfigured: false,
            bootstrapPath,
            BootstrapAvailable: bootstrapPath is not null && File.Exists(bootstrapPath));
    }

    private static void RemoveOwnedClaudeHook(JsonObject settings)
    {
        if (settings["hooks"] is not JsonObject hooks ||
            hooks["SessionStart"] is not JsonArray sessionStart)
        {
            return;
        }

        for (int index = sessionStart.Count - 1; index >= 0; index--)
        {
            if (IsOwnedClaudeHook(sessionStart[index]))
                sessionStart.RemoveAt(index);
        }
        if (sessionStart.Count == 0)
            _ = hooks.Remove("SessionStart");
        if (hooks.Count == 0)
            _ = settings.Remove("hooks");
    }

    private static bool HasOwnedClaudeHook(JsonObject settings) =>
        settings["hooks"]?["SessionStart"] is JsonArray sessionStart &&
        sessionStart.Any(IsOwnedClaudeHook);

    private static bool IsOwnedClaudeHook(JsonNode? node)
    {
        if (node is not JsonObject entry || entry["hooks"] is not JsonArray hooks)
            return false;
        return hooks.Any(hookNode =>
        {
            string command = hookNode?["command"]?.GetValue<string>() ?? string.Empty;
            return command.Contains("session-context", StringComparison.OrdinalIgnoreCase) &&
                (command.Contains("rwl.exe", StringComparison.OrdinalIgnoreCase) ||
                 command.Contains("rwl-bootstrap.exe", StringComparison.OrdinalIgnoreCase));
        });
    }

    private static string RemoveCodexSection(string config)
    {
        (int Start, int End)? bounds = FindCodexSection(config);
        if (bounds is null)
            return config;
        return config.Remove(bounds.Value.Start, bounds.Value.End - bounds.Value.Start);
    }

    private static string? GetCodexSection(string config)
    {
        (int Start, int End)? bounds = FindCodexSection(config);
        return bounds is null
            ? null
            : config.Substring(bounds.Value.Start, bounds.Value.End - bounds.Value.Start);
    }

    private static (int Start, int End)? FindCodexSection(string config)
    {
        MatchCollection sections = TomlSectionPattern.Matches(config);
        for (int index = 0; index < sections.Count; index++)
        {
            if (!string.Equals(
                sections[index].Groups["name"].Value.Trim(),
                "mcp_servers.rhino-worktree-launcher",
                StringComparison.Ordinal))
            {
                continue;
            }

            int end = index + 1 < sections.Count ? sections[index + 1].Index : config.Length;
            return (sections[index].Index, end);
        }
        return null;
    }

    private static async Task<JsonObject> ReadObjectAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return new JsonObject();
        string json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonNode.Parse(json) as JsonObject ??
            throw new InvalidDataException($"Client configuration '{path}' must contain a JSON object.");
    }

    private static async Task<string> ReadTextAsync(
        string path,
        CancellationToken cancellationToken) => File.Exists(path)
        ? await File.ReadAllTextAsync(path, cancellationToken)
        : string.Empty;

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

    private static Task WriteJsonIfChangedAsync(
        string path,
        JsonObject value,
        CancellationToken cancellationToken) => WriteTextIfChangedAsync(
        path,
        value.ToJsonString(JsonDefaults.Write),
        cancellationToken);

    private static async Task WriteTextIfChangedAsync(
        string path,
        string value,
        CancellationToken cancellationToken)
    {
        string? existing = File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken)
            : null;
        if (string.Equals(existing, value, StringComparison.Ordinal))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (existing is not null)
            File.Copy(path, path + ".rwl-backup", overwrite: true);

        string temporaryPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, value, new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
