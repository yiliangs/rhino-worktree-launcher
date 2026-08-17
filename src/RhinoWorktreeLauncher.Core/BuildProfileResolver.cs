namespace RhinoWorktreeLauncher;

internal enum BuildProfileResolutionMode
{
    SavedSelection,
    RediscoverCanonicalSelection
}

internal enum BuildProfileState
{
    /// <summary>The saved solution and plug-in project both exist in the tree.</summary>
    Resolved,

    /// <summary>The saved paths are gone, but the tree still holds a Rhino plug-in project to re-select.</summary>
    Relocated,

    /// <summary>The tree holds no Rhino plug-in project at all, so the registration can no longer launch.</summary>
    Absent
}

internal sealed record ResolvedBuildProfile(
    BuildProfile Profile,
    string SolutionPath,
    string PluginProjectPath,
    BuildConfiguration ProjectConfiguration);

internal static class BuildProfileResolver
{
    public static ResolvedBuildProfile Resolve(
        string repositoryRoot,
        BuildProfile savedProfile,
        BuildProfileResolutionMode mode,
        LaunchMode launchMode)
    {
        if (!savedProfile.IsConfigured)
            throw new InvalidDataException("The canonical solution build configuration is incomplete.");

        string root = Path.GetFullPath(repositoryRoot);
        BuildProfile profile = mode == BuildProfileResolutionMode.RediscoverCanonicalSelection
            ? BuildProfileDiscovery.Discover(
                root,
                savedProfile.PluginProjectPath,
                savedProfile.SolutionPath,
                savedProfile.SelectedConfiguration,
                launchMode)
            : savedProfile;
        string owner = mode == BuildProfileResolutionMode.SavedSelection
            ? "registered project"
            : "selected worktree";
        string solutionPath = ResolveContainedPath(root, profile.SolutionPath, owner);
        string pluginProjectPath = ResolveContainedPath(root, profile.PluginProjectPath, owner);

        if (mode == BuildProfileResolutionMode.SavedSelection &&
            (!File.Exists(solutionPath) || !File.Exists(pluginProjectPath)))
        {
            throw new FileNotFoundException("The saved canonical solution build configuration is unavailable.");
        }

        BuildConfiguration projectConfiguration = SolutionModelReader.ResolveProjectConfiguration(
            solutionPath,
            pluginProjectPath,
            profile);
        return new ResolvedBuildProfile(
            profile,
            solutionPath,
            pluginProjectPath,
            projectConfiguration);
    }

    public static bool IsAvailable(string repositoryRoot, BuildProfile profile)
    {
        if (!profile.IsConfigured)
            return false;
        try
        {
            _ = Resolve(
                repositoryRoot,
                profile,
                BuildProfileResolutionMode.SavedSelection,
                profile.LaunchMode);
            return true;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException or
            InvalidDataException)
        {
            return false;
        }
    }

    // Catalog reads run this on every load, so the common case must stay at two stat calls.
    // Only an already-broken registration pays for the recursive plug-in project scan.
    public static BuildProfileState Evaluate(string repositoryRoot, BuildProfile profile)
    {
        string root = Path.GetFullPath(repositoryRoot);
        if (SavedPathsExist(root, profile))
            return BuildProfileState.Resolved;
        return BuildProfileDiscovery.ContainsPluginProject(root)
            ? BuildProfileState.Relocated
            : BuildProfileState.Absent;
    }

    private static bool SavedPathsExist(string root, BuildProfile profile)
    {
        try
        {
            return File.Exists(ResolveContainedPath(root, profile.SolutionPath, "registered project")) &&
                File.Exists(ResolveContainedPath(root, profile.PluginProjectPath, "registered project"));
        }
        catch (Exception exception) when (exception is InvalidDataException or
            ArgumentException or
            NotSupportedException)
        {
            return false;
        }
    }

    private static string ResolveContainedPath(string root, string relativePath, string owner)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"Build configuration paths must be relative to the {owner}.");

        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Build configuration path '{relativePath}' escaped the {owner}.");
        return path;
    }
}
