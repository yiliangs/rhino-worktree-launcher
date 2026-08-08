namespace RhinoWorktreeLauncher;

using System.Text.Json;

internal sealed class BuildCoordinator
{
    private readonly LauncherBackendOptions _options;
    private readonly ContextResolver _contextResolver;
    private readonly WorktreeWorkspaceManager _workspaceManager;

    public BuildCoordinator(
        LauncherBackendOptions options,
        ContextResolver contextResolver,
        WorktreeWorkspaceManager workspaceManager)
    {
        _options = options;
        _contextResolver = contextResolver;
        _workspaceManager = workspaceManager;
    }

    public async Task<CommandResult<PreparedLaunchArtifacts>> BuildAsync(
        string path,
        IProgress<BuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            CommandResult<ResolvedContext> contextResult = await _contextResolver.ResolveAsync(
                path,
                cancellationToken);
            if (!contextResult.Succeeded)
                return CommandResult<PreparedLaunchArtifacts>.Failure(contextResult.Diagnostics.ToArray());
            ResolvedContext context = contextResult.Value!;
            if (!context.BuildProfile.IsConfigured)
            {
                return CommandResult<PreparedLaunchArtifacts>.Failure(new Diagnostic(
                    "build_profile_incomplete",
                    "The app-owned build profile is incomplete. Edit it before launching."));
            }

            Report("snapshot", "Reconciling the app-owned worktree workspace.");
            WorktreeWorkspace workspace = await _workspaceManager.PrepareAsync(context, cancellationToken);
            PreparedLaunchArtifacts artifacts = context.BuildProfile.Mode switch
            {
                BuildMode.Typed => await RunTypedBuildAsync(
                    context.BuildProfile,
                    workspace,
                    progress,
                    cancellationToken),
                BuildMode.ImportedDriver => await RunImportedDriverAsync(
                    context,
                    workspace,
                    progress,
                    cancellationToken),
                _ => throw new InvalidDataException($"Unsupported build mode '{context.BuildProfile.Mode}'.")
            };
            return CommandResult<PreparedLaunchArtifacts>.Success(artifacts);
        }
        catch (OperationCanceledException)
        {
            return CommandResult<PreparedLaunchArtifacts>.Failure(new Diagnostic(
                "build_cancelled",
                "The build was cancelled."));
        }
        catch (Exception exception)
        {
            return CommandResult<PreparedLaunchArtifacts>.Failure(new Diagnostic(
                "build_failed",
                exception.Message));
        }

        void Report(string stage, string message) => progress?.Report(
            new BuildProgress(stage, message, DateTimeOffset.UtcNow));
    }

    private async Task<PreparedLaunchArtifacts> RunTypedBuildAsync(
        BuildProfile profile,
        WorktreeWorkspace workspace,
        IProgress<BuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string> environment = CreateBuildEnvironment(workspace);
        foreach (BuildStep step in profile.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (step.Kind)
            {
                case BuildStepKind.NpmCi:
                {
                    string packageRoot = ResolveBuildPath(workspace.BuildPath, step.Target);
                    progress?.Report(new BuildProgress(
                        "dependencies",
                        $"Restoring npm dependencies in '{step.Target}'.",
                        DateTimeOffset.UtcNow));
                    await ProcessRunner.RunLinesAsync(
                        _options.NpmExecutable,
                        workspace.BuildPath,
                        new[] { "--prefix", packageRoot, "ci" }.Concat(step.Arguments),
                        environment,
                        line => ReportLineAsync(progress, "dependencies", line),
                        cancellationToken);
                    break;
                }
                case BuildStepKind.DotNetBuild:
                {
                    string projectPath = ResolveBuildPath(workspace.BuildPath, step.Target);
                    progress?.Report(new BuildProgress(
                        "build",
                        $"Building '{step.Target}' in the RWL workspace.",
                        DateTimeOffset.UtcNow));
                    string solutionDirectory = workspace.BuildPath.TrimEnd(Path.DirectorySeparatorChar) +
                        Path.DirectorySeparatorChar;
                    await ProcessRunner.RunLinesAsync(
                        _options.DotNetExecutable,
                        workspace.BuildPath,
                        new[] { "build", projectPath }
                            .Concat(step.Arguments)
                            .Append($"-p:SolutionDir={solutionDirectory}"),
                        environment,
                        line => ReportLineAsync(progress, "build", line),
                        cancellationToken);
                    break;
                }
                default:
                    throw new InvalidDataException($"Unsupported typed build step '{step.Kind}'.");
            }
        }

        string pluginPath = FindUniqueArtifact(workspace.BuildPath, profile.Artifacts.PluginFileName);
        string packageDirectory = Path.GetDirectoryName(pluginPath)!;
        VerifiedDependency[] dependencies = profile.Artifacts.CriticalDependencies
            .Select(name => new VerifiedDependency(
                name,
                ResolveDependency(packageDirectory, name)))
            .ToArray();
        return new PreparedLaunchArtifacts(
            profile.Artifacts.PluginId,
            packageDirectory,
            pluginPath,
            profile.Artifacts.RhinoRuntime,
            dependencies,
            workspace);
    }

    private async Task<PreparedLaunchArtifacts> RunImportedDriverAsync(
        ResolvedContext context,
        WorktreeWorkspace workspace,
        IProgress<BuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        string applicationRoot = Path.GetDirectoryName(Path.GetFullPath(_options.CatalogPath))!;
        string driverPath = ResolveContainedPath(
            applicationRoot,
            context.BuildProfile.ImportedDriverPath ?? string.Empty,
            "Imported driver");
        if (!File.Exists(driverPath))
            throw new FileNotFoundException("The imported driver copy is missing from RWL application storage.", driverPath);

        string requestDirectory = Path.Combine(Path.GetDirectoryName(workspace.BuildPath)!, "requests");
        Directory.CreateDirectory(requestDirectory);
        string requestPath = Path.Combine(requestDirectory, $"build-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            requestPath,
            JsonSerializer.Serialize(
                new BuildDriverRequest(
                    2,
                    "prepareBuild",
                    context.ProjectId,
                    workspace.SourcePath,
                    workspace.BuildPath),
                JsonDefaults.Write),
            cancellationToken);

        BuildDriverResult? result = null;
        progress?.Report(new BuildProgress(
            "driver",
            "Running the imported driver copy in the RWL workspace.",
            DateTimeOffset.UtcNow));
        await ProcessRunner.RunLinesAsync(
            _options.PowerShellExecutable,
            workspace.BuildPath,
            new[]
            {
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                driverPath,
                "-RequestPath",
                requestPath
            },
            CreateBuildEnvironment(workspace),
            line =>
            {
                if (string.IsNullOrWhiteSpace(line))
                    return Task.CompletedTask;
                try
                {
                    BuildDriverResult? candidate = JsonSerializer.Deserialize<BuildDriverResult>(
                        line,
                        JsonDefaults.Read);
                    if (candidate is not null && string.Equals(candidate.Kind, "result", StringComparison.Ordinal))
                        result = candidate;
                    else
                        progress?.Report(new BuildProgress("driver", line, DateTimeOffset.UtcNow));
                }
                catch (JsonException)
                {
                    progress?.Report(new BuildProgress("driver", line, DateTimeOffset.UtcNow));
                }
                return Task.CompletedTask;
            },
            cancellationToken);

        if (result is null || result.ProtocolVersion != 2)
            throw new InvalidDataException("The imported driver did not emit a protocol v2 terminal result.");
        if (!result.Success)
            throw new InvalidOperationException(result.ErrorMessage ?? "The imported driver reported failure.");
        if (result.PluginId == Guid.Empty)
            throw new InvalidDataException("The imported driver omitted a valid plug-in GUID.");
        if (!string.Equals(result.RhinoRuntime, "netfx", StringComparison.Ordinal) &&
            !string.Equals(result.RhinoRuntime, "netcore", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported Rhino runtime '{result.RhinoRuntime}'.");
        }

        string packageDirectory = ResolveReportedPath(workspace.BuildPath, result.PackageDirectory, "package directory");
        string pluginPath = ResolveReportedPath(workspace.BuildPath, result.PluginPath, "plug-in");
        string packagePrefix = packageDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!pluginPath.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(packageDirectory) ||
            !File.Exists(pluginPath))
        {
            throw new InvalidDataException("The imported driver reported a missing plug-in or one outside its package directory.");
        }

        VerifiedDependency[] dependencies = result.CriticalDependencies.Select(dependency =>
        {
            string dependencyPath = ResolveReportedPath(workspace.BuildPath, dependency.Path, "critical dependency");
            if (string.IsNullOrWhiteSpace(dependency.Name) ||
                !dependencyPath.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(dependencyPath))
            {
                throw new InvalidDataException(
                    $"Critical dependency '{dependency.Name}' is missing or outside the imported driver's package directory.");
            }
            return new VerifiedDependency(dependency.Name, dependencyPath);
        }).ToArray();
        return new PreparedLaunchArtifacts(
            result.PluginId,
            packageDirectory,
            pluginPath,
            result.RhinoRuntime,
            dependencies,
            workspace);
    }

    private static IReadOnlyDictionary<string, string> CreateBuildEnvironment(WorktreeWorkspace workspace)
    {
        string workspaceRoot = Path.GetDirectoryName(workspace.BuildPath)!;
        string cacheRoot = Path.Combine(workspaceRoot, "cache");
        string temporaryRoot = Path.Combine(workspaceRoot, "temp");
        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(temporaryRoot);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_CLI_HOME"] = Path.Combine(cacheRoot, "dotnet-home"),
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["NUGET_PACKAGES"] = Path.Combine(cacheRoot, "nuget-packages"),
            ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(cacheRoot, "nuget-http"),
            ["NUGET_PLUGINS_CACHE_PATH"] = Path.Combine(cacheRoot, "nuget-plugins"),
            ["npm_config_cache"] = Path.Combine(cacheRoot, "npm"),
            ["TEMP"] = temporaryRoot,
            ["TMP"] = temporaryRoot
        };
    }

    private static string ResolveBuildPath(string buildRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Build profile paths must be relative to the RWL workspace.");
        string root = Path.GetFullPath(buildRoot);
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Build profile path '{relativePath}' escaped the RWL workspace.");
        return path;
    }

    private static string ResolveContainedPath(string root, string relativePath, string label)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"{label} path must be relative to RWL application storage.");
        string fullRoot = Path.GetFullPath(root);
        string path = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        string prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{label} path escaped RWL application storage.");
        return path;
    }

    private static string ResolveReportedPath(string buildRoot, string reportedPath, string label)
    {
        if (string.IsNullOrWhiteSpace(reportedPath))
            throw new InvalidDataException($"The imported driver omitted its {label} path.");
        string root = Path.GetFullPath(buildRoot);
        string path = Path.GetFullPath(reportedPath);
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The imported driver's {label} escaped the RWL workspace.");
        return path;
    }

    private static string FindUniqueArtifact(string buildRoot, string fileName)
    {
        string[] matches = Directory.EnumerateFiles(buildRoot, fileName, SearchOption.AllDirectories)
            .Where(path => !ContainsSegment(buildRoot, path, "obj") &&
                !ContainsSegment(buildRoot, path, "node_modules"))
            .ToArray();
        return matches.Length switch
        {
            1 => Path.GetFullPath(matches[0]),
            0 => throw new FileNotFoundException(
                $"The build completed without producing declared plug-in '{fileName}'."),
            _ => throw new InvalidDataException(
                $"The build produced more than one '{fileName}'. Set a more specific artifact path in the app-owned profile.")
        };
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

    private static bool ContainsSegment(string root, string path, string segment) =>
        Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(value => string.Equals(value, segment, StringComparison.OrdinalIgnoreCase));

    private static Task ReportLineAsync(
        IProgress<BuildProgress>? progress,
        string stage,
        string line)
    {
        if (!string.IsNullOrWhiteSpace(line))
            progress?.Report(new BuildProgress(stage, line, DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }
}
