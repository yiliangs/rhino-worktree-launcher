using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace RhinoWorktreeLauncher;

internal static class SolutionModelReader
{
    public static SolutionModel Open(string solutionPath)
    {
        Microsoft.VisualStudio.SolutionPersistence.ISolutionSerializer serializer =
            SolutionSerializers.GetSerializerByMoniker(solutionPath) ?? throw new InvalidDataException(
                $"Unsupported solution format '{Path.GetExtension(solutionPath)}'.");
        return serializer.OpenAsync(solutionPath, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    public static BuildConfiguration ResolveProjectConfiguration(
        string solutionPath,
        string pluginProjectPath,
        BuildProfile profile)
    {
        SolutionModel solution = Open(solutionPath);
        string solutionDirectory = Path.GetDirectoryName(solutionPath)!;
        SolutionProjectModel project = solution.SolutionProjects.FirstOrDefault(item => PathIdentity.AreEquivalent(
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
}
