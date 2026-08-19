using System.Globalization;
using System.Text.Json;

namespace RhinoWorktreeLauncher;

internal enum RwlProcessRole
{
    Bootstrap,
    McpServer,
    Cli,
    Desktop
}

/// <summary>
/// One live RWL process as doctor reports it. A process is flagged, never acted on: doctor
/// reads the machine and says what it found.
/// </summary>
internal sealed record RwlProcess(
    int ProcessId,
    RwlProcessRole Role,
    string? ReleaseId,
    DateTimeOffset? StartedAt,
    int ParentProcessId,
    bool ParentAlive,
    bool ReleaseIsStale)
{
    /// <summary>
    /// Whether this process only exists to serve one client's standard streams. Those are the
    /// ones a dead parent orphans; a launch executor outlives the bootstrap that started it
    /// by design (ADR 0015), and the desktop outlives the shell that started it.
    /// </summary>
    public bool IsSessionBound => Role is RwlProcessRole.Bootstrap or RwlProcessRole.McpServer;

    public bool IsOrphan => IsSessionBound && !ParentAlive;

    public string RoleName => Role switch
    {
        RwlProcessRole.Bootstrap => "bootstrap",
        RwlProcessRole.McpServer => "mcp-server",
        RwlProcessRole.Cli => "cli",
        RwlProcessRole.Desktop => "desktop",
        _ => "unknown"
    };

    public string Describe() =>
        $"pid {ProcessId} {RoleName}, {(ReleaseId is null ? "no release directory" : $"release {ReleaseId}")}, " +
        $"started {(StartedAt is null ? "at an unreadable time" : StartedAt.Value.ToString("u", CultureInfo.InvariantCulture))}, " +
        $"parent {ParentProcessId} {(ParentAlive ? "running" : "gone")}";
}

/// <summary>
/// What RWL processes are running, which of them nobody can reach, and which are serving code
/// the installation has already replaced. Classification is separated from enumeration so the
/// judgement is decided by data rather than by the machine the doctor happens to run on.
/// </summary>
internal static class RwlProcessInventory
{
    public static IReadOnlyList<RwlProcess> Describe(
        IReadOnlyList<RunningProcess> processes,
        string? currentReleaseId)
    {
        List<RwlProcess> inventory = new List<RwlProcess>();
        foreach (RunningProcess process in processes)
        {
            RwlProcessRole? role = RoleOf(process.Name);
            if (role is null)
                continue;

            string? releaseId = ReleaseIdOf(process.ExecutablePath);
            inventory.Add(new RwlProcess(
                process.ProcessId,
                role.Value,
                releaseId,
                process.StartedAt,
                process.ParentProcessId,
                ProcessSnapshot.ParentOf(processes, process.ProcessId) is not null,
                releaseId is not null &&
                    currentReleaseId is not null &&
                    !string.Equals(releaseId, currentReleaseId, StringComparison.OrdinalIgnoreCase)));
        }
        return inventory
            .OrderBy(process => process.Role)
            .ThenBy(process => process.ProcessId)
            .ToArray();
    }

    /// <summary>
    /// The release every bootstrap resolves right now. A process that started earlier resolved
    /// a different one and keeps serving it, which is what makes this comparison worth making.
    /// </summary>
    public static string? ReadCurrentReleaseId(string currentReleasePath)
    {
        if (!File.Exists(currentReleasePath))
        {
            throw new FileNotFoundException(
                $"The installed release pointer '{currentReleasePath}' does not exist, so a " +
                "running RWL process cannot be compared against the installed release.",
                currentReleasePath);
        }

        JsonElement pointer = JsonSerializer.Deserialize<JsonElement>(
            File.ReadAllText(currentReleasePath));
        foreach (string name in new[] { "mcp", "cli", "desktop" })
        {
            if (!pointer.TryGetProperty(name, out JsonElement path))
                continue;
            string? releaseId = ReleaseIdOf(path.GetString());
            if (releaseId is not null)
                return releaseId;
        }
        throw new InvalidDataException(
            $"The installed release pointer '{currentReleasePath}' names no path under a " +
            "release directory, so the installed release cannot be identified.");
    }

    /// <summary>
    /// The release directory an executable was resolved from. A process outside a release
    /// directory, such as the stable bootstrap or a developer build, has none.
    /// </summary>
    public static string? ReleaseIdOf(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;

        string[] segments = executablePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index + 1 < segments.Length; index++)
        {
            if (string.Equals(segments[index], "releases", StringComparison.OrdinalIgnoreCase))
                return segments[index + 1];
        }
        return null;
    }

    private static RwlProcessRole? RoleOf(string executableName) => executableName.ToLowerInvariant() switch
    {
        "rwl.exe" => RwlProcessRole.Bootstrap,
        "rwl-mcp.exe" => RwlProcessRole.McpServer,
        "rwl-cli.exe" => RwlProcessRole.Cli,
        "rhinoworktreelauncher.exe" => RwlProcessRole.Desktop,
        _ => null
    };
}
