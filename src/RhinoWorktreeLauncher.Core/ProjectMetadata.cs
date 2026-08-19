using System.Text.Json;

namespace RhinoWorktreeLauncher;

internal sealed record ProjectIdentity(string ProjectId, string DisplayName)
{
    public static ProjectIdentity Create(string repositoryRoot, string? projectId = null)
    {
        string directoryName = new DirectoryInfo(repositoryRoot).Name;
        return new ProjectIdentity(
            string.IsNullOrWhiteSpace(projectId) ? CreateProjectId(directoryName) : projectId,
            CreateDisplayName(directoryName));
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

    // One record per line, for the JSONL diagnostics the launch and its executor append to.
    public static JsonSerializerOptions Line { get; } = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
