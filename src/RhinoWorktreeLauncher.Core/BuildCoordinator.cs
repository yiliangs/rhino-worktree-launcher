using System.Text.Json;

namespace RhinoWorktreeLauncher;

internal sealed class BuildCoordinator
{
    private readonly LauncherBackendOptions _options;
    private readonly ContextResolver _contextResolver;

    public BuildCoordinator(LauncherBackendOptions options, ContextResolver contextResolver)
    {
        _options = options;
        _contextResolver = contextResolver;
    }

    public async Task<CommandResult<PreparedLaunchArtifacts>> PrepareAsync(
        string path,
        LaunchMode launchMode,
        IProgress<BuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            CommandResult<ResolvedContext> contextResult = await _contextResolver.ResolveAsync(path, cancellationToken);
            if (!contextResult.Succeeded)
                return CommandResult<PreparedLaunchArtifacts>.Failure(contextResult.Diagnostics.ToArray());
            ResolvedContext context = contextResult.Value!;
            if (!context.BuildProfile.IsConfigured)
            {
                return CommandResult<PreparedLaunchArtifacts>.Failure(new Diagnostic(
                    "build_configuration_incomplete",
                    "Choose a solution build configuration in Config before launching."));
            }

            ResolvedBuildProfile resolved = BuildProfileResolver.Resolve(
                context.WorktreePath,
                context.BuildProfile,
                BuildProfileResolutionMode.RediscoverCanonicalSelection,
                launchMode);
            BuildProfile profile = resolved.Profile;

            if (profile.LaunchMode == LaunchMode.BuildAndLaunch)
            {
                progress?.Report(new BuildProgress(
                    "build",
                    $"Building '{profile.SolutionPath}' ({profile.SelectedConfiguration.DisplayName}).",
                    DateTimeOffset.UtcNow));
                await ProcessRunner.RunLinesAsync(
                    _options.DotNetExecutable,
                    context.WorktreePath,
                    new[]
                    {
                        "build",
                        resolved.SolutionPath,
                        "-c",
                        profile.SelectedConfiguration.Configuration,
                        $"-p:Platform={profile.SelectedConfiguration.Platform}"
                    },
                    line => ReportLineAsync(progress, "build", line),
                    cancellationToken);
            }
            else
            {
                progress?.Report(new BuildProgress(
                    "artifact",
                    $"Using the existing '{profile.SelectedConfiguration.DisplayName}' build without rebuilding.",
                    DateTimeOffset.UtcNow));
            }

            string pluginPath = await ResolveTargetPathAsync(
                context.WorktreePath,
                resolved.SolutionPath,
                resolved.PluginProjectPath,
                resolved.ProjectConfiguration,
                cancellationToken);
            if (!File.Exists(pluginPath))
            {
                string action = profile.LaunchMode == LaunchMode.DirectLaunch
                    ? "Build this configuration first or use Build & Launch."
                    : "The solution build completed without producing it.";
                throw new FileNotFoundException(
                    $"Canonical plug-in artifact '{pluginPath}' was not found. {action}",
                    pluginPath);
            }

            string packageDirectory = Path.GetDirectoryName(pluginPath)!;
            VerifiedDependency[] dependencies = profile.Artifacts.CriticalDependencies
                .Select(name => new VerifiedDependency(name, ResolveDependency(packageDirectory, name)))
                .ToArray();
            return CommandResult<PreparedLaunchArtifacts>.Success(new PreparedLaunchArtifacts(
                profile.Artifacts.PluginId,
                packageDirectory,
                pluginPath,
                profile.Artifacts.RhinoRuntime,
                dependencies,
                context.WorktreePath));
        }
        catch (OperationCanceledException)
        {
            return CommandResult<PreparedLaunchArtifacts>.Failure(new Diagnostic(
                "build_cancelled",
                "Artifact preparation was cancelled."));
        }
        catch (Exception exception)
        {
            return CommandResult<PreparedLaunchArtifacts>.Failure(new Diagnostic(
                "artifact_prepare_failed",
                exception.Message));
        }
    }

    private async Task<string> ResolveTargetPathAsync(
        string worktreePath,
        string solutionPath,
        string projectPath,
        BuildConfiguration configuration,
        CancellationToken cancellationToken)
    {
        string solutionDirectory = Path.GetDirectoryName(solutionPath)!
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string output = await ProcessRunner.RunAsync(
            _options.DotNetExecutable,
            worktreePath,
            new[]
            {
                "msbuild",
                projectPath,
                "-nologo",
                "-getProperty:TargetPath",
                "-getProperty:TargetDir",
                $"-p:Configuration={configuration.Configuration}",
                $"-p:Platform={configuration.Platform}",
                $"-p:SolutionDir={solutionDirectory}",
                $"-p:SolutionPath={solutionPath}",
                $"-p:SolutionName={Path.GetFileNameWithoutExtension(solutionPath)}",
                $"-p:SolutionFileName={Path.GetFileName(solutionPath)}",
                $"-p:SolutionExt={Path.GetExtension(solutionPath)}"
            },
            cancellationToken);
        using JsonDocument json = JsonDocument.Parse(output);
        string? targetPath = json.RootElement
            .GetProperty("Properties")
            .GetProperty("TargetPath")
            .GetString();
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new InvalidDataException($"MSBuild did not report TargetPath for '{projectPath}'.");
        return Path.GetFullPath(Path.IsPathRooted(targetPath)
            ? targetPath
            : Path.Combine(Path.GetDirectoryName(projectPath)!, targetPath));
    }

    private static string ResolveDependency(string packageDirectory, string name)
    {
        string path = Path.Combine(packageDirectory, name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? name
            : name + ".dll");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Declared critical dependency '{name}' was not found beside the plug-in.", path);
        return Path.GetFullPath(path);
    }

    private static Task ReportLineAsync(IProgress<BuildProgress>? progress, string stage, string line)
    {
        if (!string.IsNullOrWhiteSpace(line))
            progress?.Report(new BuildProgress(stage, line, DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }
}
