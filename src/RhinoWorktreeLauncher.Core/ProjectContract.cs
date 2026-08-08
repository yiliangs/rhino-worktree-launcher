using System.Text.Json;

namespace RhinoWorktreeLauncher;

public sealed class ProjectContract
{
    public string ProjectId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public DriverContract Driver { get; init; } = new DriverContract();
    public LaunchContract Launch { get; init; } = new LaunchContract();

    public string ResolveDriverPath(string applicationRoot) => Path.GetFullPath(
        Path.Combine(applicationRoot, Driver.Entrypoint));

    public void Validate(string applicationRoot)
    {
        if (string.IsNullOrWhiteSpace(ProjectId) || string.IsNullOrWhiteSpace(DisplayName))
            throw new InvalidDataException("ProjectId and DisplayName are required.");
        if (Driver.ProtocolVersion != 1)
            throw new InvalidDataException($"Unsupported driver protocol version {Driver.ProtocolVersion}; expected 1.");
        if (string.IsNullOrWhiteSpace(Driver.Entrypoint))
            throw new InvalidDataException("Driver.Entrypoint is required.");
        if (Path.IsPathRooted(Driver.Entrypoint))
            throw new InvalidDataException("Driver.Entrypoint must be application-relative.");
        if (Launch.RhinoVersion != 8)
            throw new InvalidDataException($"Unsupported Rhino version {Launch.RhinoVersion}; expected 8.");
        if (!string.Equals(Launch.Mode, "rhino-package-directory", StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported launch mode '{Launch.Mode}'.");

        string root = Path.GetFullPath(applicationRoot).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!ResolveDriverPath(applicationRoot).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Driver.Entrypoint must remain inside RWL's application directory.");
    }

    internal static ProjectContract CreateDefault(string repositoryRoot, string? projectId = null)
    {
        string directoryName = new DirectoryInfo(repositoryRoot).Name;
        return new ProjectContract
        {
            ProjectId = string.IsNullOrWhiteSpace(projectId) ? CreateProjectId(directoryName) : projectId,
            DisplayName = CreateDisplayName(directoryName),
            Driver = new DriverContract
            {
                ProtocolVersion = 1,
                Entrypoint = Path.Combine("projects", string.IsNullOrWhiteSpace(projectId)
                    ? CreateProjectId(directoryName)
                    : projectId, "Driver.ps1")
            },
            Launch = new LaunchContract
            {
                RhinoVersion = 8,
                Mode = "rhino-package-directory"
            }
        };
    }

    private static string CreateDisplayName(string directoryName)
    {
        string[] words = directoryName.Split(
            new[] { '-', '_', ' ' },
            StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0
            ? directoryName
            : string.Join(" ", words.Select(word =>
                word.Length == 1
                    ? word.ToUpperInvariant()
                    : char.ToUpperInvariant(word[0]) + word.Substring(1)));
    }

    private static string CreateProjectId(string displayName)
    {
        System.Text.StringBuilder projectId = new System.Text.StringBuilder(displayName.Length);
        bool previousWasSeparator = false;
        foreach (char character in displayName.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                projectId.Append(character);
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && projectId.Length > 0)
            {
                projectId.Append('-');
                previousWasSeparator = true;
            }
        }
        string value = projectId.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(value) ? "rhino-plugin" : value;
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
