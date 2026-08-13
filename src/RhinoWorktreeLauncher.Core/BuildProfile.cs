using System.Text.Json.Serialization;

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
