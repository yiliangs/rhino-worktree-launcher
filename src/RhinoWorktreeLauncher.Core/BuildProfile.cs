using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RhinoWorktreeLauncher;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BuildMode
{
    Typed,
    ImportedDriver
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BuildStepKind
{
    NpmCi,
    DotNetBuild
}

public sealed record BuildStep(
    BuildStepKind Kind,
    string Target,
    IReadOnlyList<string> Arguments);

public sealed record BuildArtifactProfile(
    Guid PluginId,
    string PluginFileName,
    string RhinoRuntime,
    IReadOnlyList<string> CriticalDependencies);

public sealed record BuildProfile(
    BuildMode Mode,
    IReadOnlyList<BuildStep> Steps,
    BuildArtifactProfile Artifacts,
    string? ImportedDriverPath)
{
    public bool IsConfigured => Mode == BuildMode.ImportedDriver
        ? !string.IsNullOrWhiteSpace(ImportedDriverPath)
        : Artifacts.PluginId != Guid.Empty &&
          !string.IsNullOrWhiteSpace(Artifacts.PluginFileName) &&
          Steps.Count > 0;

    public static BuildProfile Unconfigured { get; } = new BuildProfile(
        BuildMode.Typed,
        Array.Empty<BuildStep>(),
        new BuildArtifactProfile(Guid.Empty, string.Empty, "netfx", Array.Empty<string>()),
        null);
}

internal static class BuildProfileDiscovery
{
    private static readonly Regex PlugInGuid = new Regex(
        @"\[\s*(?:Guid|GuidAttribute)\s*\(\s*\""(?<id>[0-9a-fA-F-]{36})\""\s*\)\s*\][\s\S]{0,800}?class\s+\w+\s*:\s*(?:Rhino\.PlugIns\.)?PlugIn\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AssemblyGuid = new Regex(
        @"\[\s*assembly\s*:\s*(?:Guid|GuidAttribute)\s*\(\s*\""(?<id>[0-9a-fA-F-]{36})\""\s*\)\s*\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static BuildProfile Discover(string repositoryRoot)
    {
        string root = Path.GetFullPath(repositoryRoot);
        ProjectCandidate? project = EnumerateProjectFiles(root)
            .Select(path => ReadCandidate(root, path))
            .Where(candidate => candidate is not null)
            .Cast<ProjectCandidate>()
            .OrderByDescending(candidate => candidate.HasRhpTarget)
            .ThenBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (project is null)
            return BuildProfile.Unconfigured;

        string projectDirectory = Path.GetDirectoryName(Path.Combine(root, project.RelativePath))!;
        List<BuildStep> steps = EnumeratePackageLocks(root, projectDirectory)
            .Select(path => new BuildStep(
                BuildStepKind.NpmCi,
                Path.GetRelativePath(root, Path.GetDirectoryName(path)!),
                Array.Empty<string>()))
            .ToList();
        List<string> buildArguments = new List<string> { "-c", "Debug" };
        if (project.Platforms.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(platform =>
            string.Equals(platform.Trim(), "x64", StringComparison.OrdinalIgnoreCase)))
        {
            buildArguments.Add("-p:Platform=x64");
        }
        steps.Add(new BuildStep(
            BuildStepKind.DotNetBuild,
            project.RelativePath,
            buildArguments));

        return new BuildProfile(
            BuildMode.Typed,
            steps,
            new BuildArtifactProfile(
                project.PluginId,
                project.AssemblyName + ".rhp",
                project.TargetFramework.StartsWith("net4", StringComparison.OrdinalIgnoreCase)
                    ? "netfx"
                    : "netcore",
                project.ProjectReferences),
            null);
    }

    private static IEnumerable<string> EnumerateProjectFiles(string root) =>
        Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !HasExcludedSegment(root, path) && !IsNestedRepository(root, path));

    private static IEnumerable<string> EnumeratePackageLocks(string root, string projectDirectory) =>
        Directory.EnumerateFiles(root, "package-lock.json", SearchOption.AllDirectories)
            .Where(path => !HasExcludedSegment(root, path) && !IsNestedRepository(root, path))
            .Where(path =>
            {
                string packageRoot = Path.GetDirectoryName(path)!;
                return ContextResolver.SamePath(packageRoot, root) ||
                    ContextResolver.SamePath(packageRoot, projectDirectory) ||
                    string.Equals(new DirectoryInfo(packageRoot).Name, "web", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

    private static ProjectCandidate? ReadCandidate(string root, string projectPath)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(projectPath, LoadOptions.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }

        string? targetExt = Value(document, "TargetExt");
        bool hasRhpTarget = string.Equals(targetExt, ".rhp", StringComparison.OrdinalIgnoreCase);
        bool referencesRhino = document.Descendants().Any(element =>
            string.Equals(element.Name.LocalName, "Reference", StringComparison.Ordinal) &&
            ((string?)element.Attribute("Include"))?.StartsWith("RhinoCommon", StringComparison.OrdinalIgnoreCase) == true);
        if (!hasRhpTarget && !referencesRhino)
            return null;

        string projectDirectory = Path.GetDirectoryName(projectPath)!;
        Guid pluginId = FindPlugInGuid(projectDirectory);
        string assemblyName = Value(document, "AssemblyName") ?? Path.GetFileNameWithoutExtension(projectPath);
        string framework = Value(document, "TargetFramework") ?? Value(document, "TargetFrameworkVersion") ?? "net481";
        string platforms = Value(document, "Platforms") ?? Value(document, "Platform") ?? string.Empty;
        string[] references = document.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "ProjectReference", StringComparison.Ordinal))
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFileNameWithoutExtension(value!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ProjectCandidate(
            Path.GetRelativePath(root, projectPath),
            assemblyName,
            framework,
            platforms,
            pluginId,
            references,
            hasRhpTarget);
    }

    private static Guid FindPlugInGuid(string projectDirectory)
    {
        bool containsPlugIn = false;
        Guid assemblyId = Guid.Empty;
        foreach (string path in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !HasExcludedSegment(projectDirectory, path) &&
                !IsNestedRepository(projectDirectory, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string contents = File.ReadAllText(path);
            Match match = PlugInGuid.Match(contents);
            if (match.Success && Guid.TryParse(match.Groups["id"].Value, out Guid id))
                return id;
            containsPlugIn |= Regex.IsMatch(
                contents,
                @"class\s+\w+\s*:\s*(?:Rhino\.PlugIns\.)?PlugIn\b",
                RegexOptions.CultureInvariant);
            Match assemblyMatch = AssemblyGuid.Match(contents);
            if (assemblyMatch.Success)
                _ = Guid.TryParse(assemblyMatch.Groups["id"].Value, out assemblyId);
        }
        return containsPlugIn ? assemblyId : Guid.Empty;
    }

    private static string? Value(XDocument document, string name) => document.Descendants()
        .FirstOrDefault(element => string.Equals(element.Name.LocalName, name, StringComparison.Ordinal))?
        .Value.Trim();

    private static bool HasExcludedSegment(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment =>
            string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(segment, "node_modules", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNestedRepository(string root, string path)
    {
        DirectoryInfo? directory = new FileInfo(path).Directory;
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        while (directory is not null && !ContextResolver.SamePath(directory.FullName, fullRoot))
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return true;
            }
            directory = directory.Parent;
        }
        return false;
    }

    private sealed record ProjectCandidate(
        string RelativePath,
        string AssemblyName,
        string TargetFramework,
        string Platforms,
        Guid PluginId,
        IReadOnlyList<string> ProjectReferences,
        bool HasRhpTarget);
}
