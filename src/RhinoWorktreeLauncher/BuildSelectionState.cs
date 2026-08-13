namespace RhinoWorktreeLauncher;

internal sealed class BuildSelectionState
{
    private readonly SelectionContext _context;
    private readonly BuildProfile? _savedProfile;

    private BuildSelectionState(
        SelectionContext context,
        BuildProfile? savedProfile,
        IReadOnlyList<PluginBuildOptions> plugins,
        PluginBuildOptions? selectedPlugin,
        IReadOnlyList<SolutionBuildOptions>? solutions,
        SolutionBuildOptions? selectedSolution,
        IReadOnlyList<BuildConfiguration>? configurations,
        BuildConfiguration? selectedConfiguration)
    {
        _context = context;
        _savedProfile = savedProfile;
        Plugins = plugins;
        SelectedPlugin = selectedPlugin;
        Solutions = solutions;
        SelectedSolution = selectedSolution;
        Configurations = configurations;
        SelectedConfiguration = selectedConfiguration;
    }

    public IReadOnlyList<PluginBuildOptions> Plugins { get; }
    public PluginBuildOptions? SelectedPlugin { get; }
    public bool PluginEnabled => _context == SelectionContext.Add || Plugins.Count > 0;
    public string PluginPlaceholder => _context == SelectionContext.Config && Plugins.Count == 0
        ? "No Rhino plug-in projects found"
        : "Select a plug-in project";
    public IReadOnlyList<SolutionBuildOptions>? Solutions { get; }
    public SolutionBuildOptions? SelectedSolution { get; }
    public bool SolutionEnabled => Solutions is not null && Solutions.Count > 0;
    public string SolutionPlaceholder => SelectedPlugin is null
        ? "Select a plug-in project first"
        : SolutionEnabled
            ? "Select a solution"
            : "No solutions found";
    public IReadOnlyList<BuildConfiguration>? Configurations { get; }
    public BuildConfiguration? SelectedConfiguration { get; }
    public bool ConfigurationEnabled => Configurations is not null && Configurations.Count > 0;
    public string ConfigurationPlaceholder => SelectedSolution is null
        ? "Select a solution first"
        : ConfigurationEnabled
            ? "Select a configuration"
            : "No configurations found";
    public bool IsComplete =>
        SelectedPlugin is not null &&
        SelectedSolution is not null &&
        SelectedConfiguration is not null;

    public static BuildSelectionState ForAdd(ProjectBuildOptions options)
    {
        BuildSelectionState initial = Empty(SelectionContext.Add, null, options.Plugins);
        PluginBuildOptions? plugin = options.Plugins.Count == 1 ? options.Plugins[0] : null;
        return initial.SelectPlugin(plugin);
    }

    public static BuildSelectionState ForConfig(ProjectBuildOptions options, BuildProfile savedProfile)
    {
        BuildSelectionState initial = Empty(SelectionContext.Config, savedProfile, options.Plugins);
        PluginBuildOptions? plugin = options.Plugins.FirstOrDefault(candidate => string.Equals(
            candidate.PluginProjectPath,
            savedProfile.PluginProjectPath,
            StringComparison.OrdinalIgnoreCase)) ?? (options.Plugins.Count == 1 ? options.Plugins[0] : null);
        return initial.SelectPlugin(plugin);
    }

    public BuildSelectionState SelectPlugin(PluginBuildOptions? plugin)
    {
        IReadOnlyList<SolutionBuildOptions>? solutions = plugin?.Solutions;
        SolutionBuildOptions? solution = null;
        if (plugin is not null)
        {
            if (_context == SelectionContext.Config && string.Equals(
                plugin.PluginProjectPath,
                _savedProfile!.PluginProjectPath,
                StringComparison.OrdinalIgnoreCase))
            {
                solution = plugin.Solutions.FirstOrDefault(candidate => string.Equals(
                    candidate.SolutionPath,
                    _savedProfile.SolutionPath,
                    StringComparison.OrdinalIgnoreCase));
            }

            solution ??= plugin.Solutions.Count == 1 ? plugin.Solutions[0] : null;
        }

        return new BuildSelectionState(
            _context,
            _savedProfile,
            Plugins,
            plugin,
            solutions,
            null,
            null,
            null).SelectSolution(solution);
    }

    public BuildSelectionState SelectSolution(SolutionBuildOptions? solution)
    {
        IReadOnlyList<BuildConfiguration>? configurations = solution?.Configurations;
        BuildConfiguration? configuration = solution is null
            ? null
            : SelectConfiguration(solution);
        return new BuildSelectionState(
            _context,
            _savedProfile,
            Plugins,
            SelectedPlugin,
            Solutions,
            solution,
            configurations,
            configuration);
    }

    public BuildSelectionState SelectConfiguration(BuildConfiguration? configuration) =>
        new BuildSelectionState(
            _context,
            _savedProfile,
            Plugins,
            SelectedPlugin,
            Solutions,
            SelectedSolution,
            Configurations,
            configuration);

    private static BuildSelectionState Empty(
        SelectionContext context,
        BuildProfile? savedProfile,
        IReadOnlyList<PluginBuildOptions> plugins) =>
        new BuildSelectionState(context, savedProfile, plugins, null, null, null, null, null);

    private BuildConfiguration? SelectConfiguration(SolutionBuildOptions solution)
    {
        if (_context == SelectionContext.Config && string.Equals(
            solution.SolutionPath,
            _savedProfile!.SolutionPath,
            StringComparison.OrdinalIgnoreCase))
        {
            BuildConfiguration? saved = solution.Configurations.FirstOrDefault(configuration =>
                SameConfiguration(configuration, _savedProfile.SelectedConfiguration));
            if (saved is not null)
                return saved;
        }

        BuildConfiguration? debugX64 = solution.Configurations.FirstOrDefault(configuration =>
            string.Equals(configuration.Configuration, "Debug", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(configuration.Platform, "x64", StringComparison.OrdinalIgnoreCase));
        if (debugX64 is not null)
            return debugX64;

        if (_context == SelectionContext.Add)
        {
            BuildConfiguration? debug = solution.Configurations.FirstOrDefault(configuration => string.Equals(
                configuration.Configuration,
                "Debug",
                StringComparison.OrdinalIgnoreCase));
            if (debug is not null)
                return debug;
        }

        return solution.Configurations.FirstOrDefault();
    }

    private static bool SameConfiguration(BuildConfiguration left, BuildConfiguration right) =>
        string.Equals(left.Configuration, right.Configuration, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Platform, right.Platform, StringComparison.OrdinalIgnoreCase);

    private enum SelectionContext
    {
        Add,
        Config
    }
}
