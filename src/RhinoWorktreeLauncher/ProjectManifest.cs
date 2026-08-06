using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhinoWorktreeLauncher;

public sealed class ProjectManifest
{
    public int SchemaVersion { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public PrimaryLaunchContract PrimaryLaunch { get; set; } = new PrimaryLaunchContract();
    public WorktreeLaunchContract WorktreeLaunch { get; set; } = new WorktreeLaunchContract();
    public ReadinessContract Readiness { get; set; } = new ReadinessContract();

    [JsonIgnore]
    public string ManifestPath { get; private set; } = string.Empty;

    [JsonIgnore]
    public string RepositoryRoot => Path.GetDirectoryName(ManifestPath)!;

    public static ProjectManifest Load(string path)
    {
        string manifestPath = Directory.Exists(path)
            ? Path.Combine(path, ".rhino-worktree-launcher.json")
            : path;
        manifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException(
                $"No .rhino-worktree-launcher.json was found at '{manifestPath}'.",
                manifestPath);

        JsonSerializerOptions options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        ProjectManifest? manifest = JsonSerializer.Deserialize<ProjectManifest>(
            File.ReadAllText(manifestPath),
            options);
        if (manifest is null)
            throw new InvalidDataException($"Project manifest '{manifestPath}' is empty.");

        manifest.ManifestPath = manifestPath;
        manifest.Validate();
        return manifest;
    }

    private void Validate()
    {
        if (SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported launcher schema version {SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(ProjectId) || string.IsNullOrWhiteSpace(DisplayName))
            throw new InvalidDataException("ProjectId and DisplayName are required.");
        if (!string.Equals(PrimaryLaunch.Mode, "normal-rhino", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsupported primary launch mode '{PrimaryLaunch.Mode}'.");
        if (PrimaryLaunch.RhinoVersion <= 0)
            throw new InvalidDataException("PrimaryLaunch.RhinoVersion must be positive.");
        if (string.IsNullOrWhiteSpace(WorktreeLaunch.Entrypoint))
            throw new InvalidDataException("WorktreeLaunch.Entrypoint is required.");
    }
}

public sealed class PrimaryLaunchContract
{
    public string Mode { get; set; } = string.Empty;
    public int RhinoVersion { get; set; }
}

public sealed class WorktreeLaunchContract
{
    public string Entrypoint { get; set; } = string.Empty;
}

public sealed class ReadinessContract
{
    public string[] RequiredFiles { get; set; } = Array.Empty<string>();
}
