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
                    BuildStage.Build,
                    $"Building '{profile.SolutionPath}' ({profile.SelectedConfiguration.DisplayName}).",
                    DateTimeOffset.UtcNow));
                await BuildAsync(context.WorktreePath, resolved, profile, progress, cancellationToken);
            }
            else
            {
                progress?.Report(new BuildProgress(
                    BuildStage.Artifact,
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
        // A failure class the launcher recognises names itself. Everything else keeps the
        // stage-level code and whatever the failing tool said.
        catch (LaunchDiagnosticException exception)
        {
            return CommandResult<PreparedLaunchArtifacts>.Failure(new Diagnostic(
                exception.Code,
                exception.Message));
        }
        catch (Exception exception)
        {
            return CommandResult<PreparedLaunchArtifacts>.Failure(new Diagnostic(
                "artifact_prepare_failed",
                exception.Message));
        }
    }

    /// <summary>
    /// Runs the solution build, watching its output for the one failure class the launcher
    /// recognises: another program holding a build output file open, which is what a Rhino
    /// still running with this plug-in loaded does. That build fails with pages of MSBuild
    /// copy retries, so the transcript stays in the launch log and the caller is handed the
    /// condition, the file, and who is holding it.
    /// </summary>
    private async Task BuildAsync(
        string worktreePath,
        ResolvedBuildProfile resolved,
        BuildProfile profile,
        IProgress<BuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        LockedBuildOutputWatch watch = new LockedBuildOutputWatch();
        try
        {
            await ProcessRunner.RunLinesAsync(
                _options.DotNetExecutable,
                worktreePath,
                new[]
                {
                    "build",
                    resolved.SolutionPath,
                    "-c",
                    profile.SelectedConfiguration.Configuration,
                    $"-p:Platform={profile.SelectedConfiguration.Platform}"
                },
                line =>
                {
                    watch.Observe(line);
                    return ReportLineAsync(progress, BuildStage.Build, line);
                },
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            // The runner carries the failing tool's standard error, which the streamed
            // standard output above never passes through.
            watch.ObserveAll(exception.Message);
            if (watch.Locked is not { } locked)
                throw;
            throw new LaunchDiagnosticException(
                "build_output_locked",
                DescribeLockedOutput(locked, worktreePath),
                exception);
        }
    }

    private string DescribeLockedOutput(LockedBuildOutput locked, string worktreePath)
    {
        string file = locked.Path is null
            ? "The build did not name the file it could not replace; the launch log holds the build output."
            : locked.Path;
        return string.Join(Environment.NewLine, new[]
        {
            "A running program is holding this build's output file open, so the build could not replace it.",
            file,
            DescribeHolders(worktreePath),
            "Close the program holding it and launch again."
        });
    }

    /// <summary>
    /// Which live Rhino holds a plug-in artifact from this worktree, asked with the same
    /// read-only address-space query that attributes a Rhino after a launch (ADR 0002). RWL
    /// reads no other process, so a holder that is not Rhino is reported as one it cannot
    /// name rather than guessed at.
    /// </summary>
    private string DescribeHolders(string worktreePath)
    {
        const string unnamed =
            "RWL found no live Rhino holding a plug-in artifact from this worktree, so it cannot name what holds the file.";
        try
        {
            RhinoInstanceAttribution attribution = RhinoInstanceReader.Describe(
                _options.ProcessSnapshotReader(),
                _options.MappedPlugInReader);
            int[] holders = attribution.Instances
                .Where(instance => instance.PlugInPaths.Any(path => PathIdentity.IsUnder(path, worktreePath)))
                .Select(instance => instance.ProcessId)
                .ToArray();
            return holders.Length == 0
                ? unnamed
                : $"Rhino {string.Join(" and ", holders.Select(id => $"pid {id}"))} " +
                  $"{(holders.Length == 1 ? "holds" : "hold")} this worktree's plug-in.";
        }
        // Naming the holder is an aid, not the finding. A machine that will not answer the
        // question still gets told which file is held.
        catch (Exception)
        {
            return unnamed;
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

    private static Task ReportLineAsync(IProgress<BuildProgress>? progress, BuildStage stage, string line)
    {
        if (!string.IsNullOrWhiteSpace(line))
            progress?.Report(new BuildProgress(stage, line, DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }
}
