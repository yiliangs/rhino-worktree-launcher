using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace RhinoWorktreeLauncher;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LaunchMode
{
    BuildAndLaunch,
    DirectLaunch
}

public sealed record BuildConfiguration(string Configuration, string Platform)
{
    public string DisplayName => $"{Configuration} | {Platform}";
}

public sealed record BuildArtifactProfile(
    Guid PluginId,
    string RhinoRuntime,
    IReadOnlyList<string> CriticalDependencies);

public sealed record BuildProfile(
    string SolutionPath,
    string PluginProjectPath,
    IReadOnlyList<BuildConfiguration> AvailableConfigurations,
    BuildConfiguration SelectedConfiguration,
    LaunchMode LaunchMode,
    BuildArtifactProfile Artifacts)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SolutionPath) &&
        !string.IsNullOrWhiteSpace(PluginProjectPath) &&
        !string.IsNullOrWhiteSpace(SelectedConfiguration.Configuration) &&
        !string.IsNullOrWhiteSpace(SelectedConfiguration.Platform) &&
        Artifacts.PluginId != Guid.Empty;

    public static BuildProfile Unconfigured { get; } = new BuildProfile(
        string.Empty,
        string.Empty,
        Array.Empty<BuildConfiguration>(),
        new BuildConfiguration(string.Empty, string.Empty),
        LaunchMode.BuildAndLaunch,
        new BuildArtifactProfile(Guid.Empty, "netfx", Array.Empty<string>()));
}

public sealed record SolutionBuildOptions(
    string SolutionPath,
    IReadOnlyList<BuildConfiguration> Configurations);

public sealed record PluginBuildOptions(
    string PluginProjectPath,
    IReadOnlyList<SolutionBuildOptions> Solutions);

public sealed record ProjectBuildOptions(IReadOnlyList<PluginBuildOptions> Plugins);

internal static class BuildProfileDiscovery
{
    private static readonly Regex PlugInGuid = new Regex(
        @"\[\s*(?:Guid|GuidAttribute)\s*\(\s*\""(?<id>[0-9a-fA-F-]{36})\""\s*\)\s*\][\s\S]{0,800}?class\s+\w+\s*:\s*(?:Rhino\.PlugIns\.)?PlugIn\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AssemblyGuid = new Regex(
        @"\[\s*assembly\s*:\s*(?:Guid|GuidAttribute)\s*\(\s*\""(?<id>[0-9a-fA-F-]{36})\""\s*\)\s*\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static BuildProfile Discover(
        string repositoryRoot,
        string? selectedPluginProjectPath = null,
        string? selectedSolutionPath = null,
        BuildConfiguration? selectedConfiguration = null,
        LaunchMode launchMode = LaunchMode.BuildAndLaunch)
    {
        string root = Path.GetFullPath(repositoryRoot);
        ProjectCandidate project = SelectPluginProject(root, selectedPluginProjectPath);
        SolutionCandidate[] solutions = FindSolutions(root, project).ToArray();
        if (solutions.Length == 0)
        {
            throw new InvalidDataException(
                $"No solution containing Rhino plug-in project '{project.RelativePath}' was found.");
        }

        SolutionCandidate solution;
        if (string.IsNullOrWhiteSpace(selectedSolutionPath))
        {
            if (solutions.Length > 1)
            {
                throw new InvalidDataException(
                    $"More than one solution contains Rhino plug-in project '{project.RelativePath}'. Choose the canonical solution in Config.");
            }
            solution = solutions[0];
        }
        else
        {
            string requested = NormalizeRelativePath(selectedSolutionPath);
            solution = solutions.FirstOrDefault(candidate => string.Equals(
                candidate.RelativePath,
                requested,
                StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException(
                $"Solution '{selectedSolutionPath}' does not contain Rhino plug-in project '{project.RelativePath}'.");
        }

        BuildConfiguration configuration = selectedConfiguration ?? SelectDefault(solution.Configurations);
        if (!solution.Configurations.Contains(configuration, BuildConfigurationComparer.Instance))
        {
            throw new InvalidDataException(
                $"Solution configuration '{configuration.DisplayName}' is not available in '{solution.RelativePath}'.");
        }

        return new BuildProfile(
            solution.RelativePath,
            project.RelativePath,
            solution.Configurations,
            configuration,
            launchMode,
            new BuildArtifactProfile(
                project.PluginId,
                IsNetFramework(project.TargetFramework)
                    ? "netfx"
                    : "netcore",
                project.ProjectReferences));
    }

    public static ProjectBuildOptions DiscoverOptions(string repositoryRoot)
    {
        string root = Path.GetFullPath(repositoryRoot);
        return new ProjectBuildOptions(FindPluginProjects(root)
            .Select(project => new PluginBuildOptions(
                project.RelativePath,
                FindSolutions(root, project)
                    .Select(solution => new SolutionBuildOptions(
                        solution.RelativePath,
                        solution.Configurations))
                    .ToArray()))
            .ToArray());
    }

    public static BuildConfiguration ResolveProjectConfiguration(string repositoryRoot, BuildProfile profile)
    {
        string root = Path.GetFullPath(repositoryRoot);
        string solutionPath = ResolveContainedPath(root, profile.SolutionPath);
        string pluginProjectPath = ResolveContainedPath(root, profile.PluginProjectPath);
        SolutionModel solution = OpenSolution(solutionPath);
        string solutionDirectory = Path.GetDirectoryName(solutionPath)!;
        SolutionProjectModel project = solution.SolutionProjects.FirstOrDefault(item => ContextResolver.SamePath(
            Path.GetFullPath(Path.Combine(solutionDirectory, item.FilePath)),
            pluginProjectPath)) ?? throw new InvalidDataException(
            $"Solution '{profile.SolutionPath}' no longer contains plug-in project '{profile.PluginProjectPath}'.");
        (string? configuration, string? platform, bool build, _) = project.GetProjectConfiguration(
            profile.SelectedConfiguration.Configuration,
            profile.SelectedConfiguration.Platform);
        if (!build || string.IsNullOrWhiteSpace(configuration) || string.IsNullOrWhiteSpace(platform))
        {
            throw new InvalidDataException(
                $"Solution configuration '{profile.SelectedConfiguration.DisplayName}' does not build plug-in project '{profile.PluginProjectPath}'.");
        }
        return new BuildConfiguration(configuration, platform);
    }

    private static ProjectCandidate SelectPluginProject(string root, string? selectedPluginProjectPath)
    {
        ProjectCandidate[] projects = FindPluginProjects(root);
        if (string.IsNullOrWhiteSpace(selectedPluginProjectPath))
        {
            if (projects.Length > 1)
            {
                throw new InvalidDataException(
                    "More than one Rhino plug-in project was found. Choose the canonical plug-in project in Config.");
            }
            return projects[0];
        }

        string requested = NormalizeRelativePath(selectedPluginProjectPath);
        return projects.FirstOrDefault(project => string.Equals(
            project.RelativePath,
            requested,
            StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException(
            $"Rhino plug-in project '{selectedPluginProjectPath}' was not found.");
    }

    private static ProjectCandidate[] FindPluginProjects(string root)
    {
        ProjectCandidate[] projects = EnumerateProjectFiles(root)
            .Select(path => ReadCandidate(root, path))
            .Where(candidate => candidate is not null)
            .Cast<ProjectCandidate>()
            .OrderByDescending(candidate => candidate.HasRhpTarget)
            .ThenBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (projects.Length == 0)
            throw new InvalidDataException("No Rhino plug-in project was found.");
        return projects;
    }

    private static IEnumerable<SolutionCandidate> FindSolutions(string root, ProjectCandidate project)
    {
        string projectPath = Path.GetFullPath(Path.Combine(root, project.RelativePath));
        foreach (string solutionPath in EnumerateSolutionFiles(root))
        {
            SolutionModel solution = OpenSolution(solutionPath);
            string solutionDirectory = Path.GetDirectoryName(solutionPath)!;
            SolutionProjectModel? pluginProject = solution.SolutionProjects.FirstOrDefault(item => ContextResolver.SamePath(
                Path.GetFullPath(Path.Combine(solutionDirectory, item.FilePath)),
                projectPath));
            if (pluginProject is null)
            {
                continue;
            }

            string[] buildTypes = solution.BuildTypes.Count > 0
                ? solution.BuildTypes.ToArray()
                : ProjectValues(project.Configurations, "Debug", "Release");
            string[] platforms = solution.Platforms.Count > 0
                ? solution.Platforms.ToArray()
                : ProjectValues(project.Platforms, "Any CPU");
            BuildConfiguration[] configurations = buildTypes
                .SelectMany(buildType => platforms.Select(platform => new BuildConfiguration(buildType, platform)))
                .Where(configuration => pluginProject.GetProjectConfiguration(
                    configuration.Configuration,
                    configuration.Platform).Build)
                .ToArray();
            yield return new SolutionCandidate(
                NormalizeRelativePath(Path.GetRelativePath(root, solutionPath)),
                configurations);
        }
    }

    private static BuildConfiguration SelectDefault(IReadOnlyList<BuildConfiguration> configurations)
    {
        if (configurations.Count == 0)
            throw new InvalidDataException("The canonical solution does not expose any build configurations.");
        return configurations.FirstOrDefault(configuration =>
                   string.Equals(configuration.Configuration, "Debug", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(configuration.Platform, "x64", StringComparison.OrdinalIgnoreCase)) ??
               configurations.FirstOrDefault(configuration =>
                   string.Equals(configuration.Configuration, "Debug", StringComparison.OrdinalIgnoreCase)) ??
               configurations[0];
    }

    private static SolutionModel OpenSolution(string solutionPath)
    {
        Microsoft.VisualStudio.SolutionPersistence.ISolutionSerializer serializer =
            SolutionSerializers.GetSerializerByMoniker(solutionPath) ?? throw new InvalidDataException(
                $"Unsupported solution format '{Path.GetExtension(solutionPath)}'.");
        return serializer.OpenAsync(solutionPath, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Build configuration paths must be relative to the registered project.");
        string fullRoot = Path.GetFullPath(root);
        string path = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        string prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Build configuration path '{relativePath}' escaped the registered project.");
        return path;
    }

    private static string[] ProjectValues(string value, params string[] defaults) =>
        string.IsNullOrWhiteSpace(value)
            ? defaults
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsNetFramework(string targetFramework) =>
        targetFramework.StartsWith("net4", StringComparison.OrdinalIgnoreCase) ||
        targetFramework.StartsWith("v4", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateProjectFiles(string root) =>
        Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !HasExcludedSegment(root, path) && !IsNestedRepository(root, path));

    private static IEnumerable<string> EnumerateSolutionFiles(string root) =>
        Directory.EnumerateFiles(root, "*.sln", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(root, "*.slnx", SearchOption.AllDirectories))
            .Where(path => !HasExcludedSegment(root, path) && !IsNestedRepository(root, path))
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
        string framework = Value(document, "TargetFramework") ?? Value(document, "TargetFrameworkVersion") ?? "net481";
        string configurations = Value(document, "Configurations") ?? "Debug;Release";
        string platforms = Value(document, "Platforms") ?? Value(document, "Platform") ?? "Any CPU";
        string[] references = document.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "ProjectReference", StringComparison.Ordinal))
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFileNameWithoutExtension(value!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ProjectCandidate(
            NormalizeRelativePath(Path.GetRelativePath(root, projectPath)),
            framework,
            configurations,
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
            .Where(path => !HasExcludedSegment(projectDirectory, path) && !IsNestedRepository(projectDirectory, path))
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

    private static string NormalizeRelativePath(string path) => path
        .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private sealed record ProjectCandidate(
        string RelativePath,
        string TargetFramework,
        string Configurations,
        string Platforms,
        Guid PluginId,
        IReadOnlyList<string> ProjectReferences,
        bool HasRhpTarget);

    private sealed record SolutionCandidate(
        string RelativePath,
        IReadOnlyList<BuildConfiguration> Configurations);

    private sealed class BuildConfigurationComparer : IEqualityComparer<BuildConfiguration>
    {
        public static BuildConfigurationComparer Instance { get; } = new BuildConfigurationComparer();

        public bool Equals(BuildConfiguration? x, BuildConfiguration? y) =>
            x is not null && y is not null &&
            string.Equals(x.Configuration, y.Configuration, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Platform, y.Platform, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(BuildConfiguration value) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Configuration),
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Platform));
    }
}
