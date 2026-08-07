using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhinoWorktreeLauncher;

public sealed class ProjectManifest
{
    public const string DefaultFileName = ".rhino-worktree-launcher.json";

    public int SchemaVersion { get; init; }
    public string ProjectId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public DriverContract Driver { get; init; } = new DriverContract();
    public LaunchContract Launch { get; init; } = new LaunchContract();

    [JsonIgnore]
    public string ManifestPath { get; private set; } = string.Empty;

    [JsonIgnore]
    public string RepositoryRoot => Path.GetDirectoryName(ManifestPath)!;

    public static ProjectManifest Load(string repositoryOrManifestPath)
    {
        string manifestPath = Directory.Exists(repositoryOrManifestPath)
            ? Path.Combine(repositoryOrManifestPath, DefaultFileName)
            : repositoryOrManifestPath;
        manifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"No {DefaultFileName} was found at '{manifestPath}'.",
                manifestPath);
        }

        ProjectManifest? manifest = JsonSerializer.Deserialize<ProjectManifest>(
            File.ReadAllText(manifestPath),
            JsonDefaults.Read);
        if (manifest is null)
            throw new InvalidDataException($"Project manifest '{manifestPath}' is empty.");

        manifest.ManifestPath = manifestPath;
        manifest.Validate();
        return manifest;
    }

    public string ResolveDriverPath(string repositoryRoot) => Path.GetFullPath(
        Path.Combine(repositoryRoot, Driver.Entrypoint));

    private void Validate()
    {
        if (SchemaVersion != 2)
            throw new InvalidDataException($"Unsupported launcher schema version {SchemaVersion}; expected 2.");
        if (string.IsNullOrWhiteSpace(ProjectId) || string.IsNullOrWhiteSpace(DisplayName))
            throw new InvalidDataException("ProjectId and DisplayName are required.");
        if (Driver.ProtocolVersion != 1)
            throw new InvalidDataException($"Unsupported driver protocol version {Driver.ProtocolVersion}; expected 1.");
        if (string.IsNullOrWhiteSpace(Driver.Entrypoint))
            throw new InvalidDataException("Driver.Entrypoint is required.");
        if (Path.IsPathRooted(Driver.Entrypoint))
            throw new InvalidDataException("Driver.Entrypoint must be repository-relative.");
        if (Launch.RhinoVersion != 8)
            throw new InvalidDataException($"Unsupported Rhino version {Launch.RhinoVersion}; expected 8.");
        if (!string.Equals(Launch.Mode, "rhino-package-directory", StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported launch mode '{Launch.Mode}'.");

        string driverPath = ResolveDriverPath(RepositoryRoot);
        if (!driverPath.StartsWith(
            Path.GetFullPath(RepositoryRoot) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Driver.Entrypoint must remain inside the repository.");
        }
    }
}

public sealed class DriverContract
{
    public int ProtocolVersion { get; init; }
    public string Entrypoint { get; init; } = string.Empty;
}

public sealed class LaunchContract
{
    public int RhinoVersion { get; init; }
    public string Mode { get; init; } = string.Empty;
}

internal static class JsonDefaults
{
    public static JsonSerializerOptions Read { get; } = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    public static JsonSerializerOptions Write { get; } = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
