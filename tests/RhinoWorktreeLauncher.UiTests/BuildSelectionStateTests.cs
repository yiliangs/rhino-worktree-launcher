namespace RhinoWorktreeLauncher.UiTests;

public sealed class BuildSelectionStateTests
{
    [Theory]
    [InlineData("add-empty", true, "Select a plug-in project", null)]
    [InlineData("config-empty", false, "No Rhino plug-in projects found", null)]
    [InlineData("add-single", true, "Select a plug-in project", "Only.csproj")]
    [InlineData("add-multiple", true, "Select a plug-in project", null)]
    [InlineData("config-saved-case-insensitive", true, "Select a plug-in project", "PLUGINS/SAVED.CSPROJ")]
    [InlineData("config-missing-saved-single", true, "Select a plug-in project", "Only.csproj")]
    [InlineData("config-missing-saved-multiple", true, "Select a plug-in project", null)]
    public void Initial_plugin_selection_preserves_dialog_policy(
        string scenario,
        bool expectedEnabled,
        string expectedPlaceholder,
        string? expectedPluginPath)
    {
        BuildSelectionState state = InitialState(scenario);

        Assert.Equal(expectedEnabled, state.PluginEnabled);
        Assert.Equal(expectedPlaceholder, state.PluginPlaceholder);
        Assert.Equal(expectedPluginPath, state.SelectedPlugin?.PluginProjectPath);
    }

    [Theory]
    [InlineData(
        "null-plugin",
        null,
        null,
        false,
        "Select a plug-in project first",
        null,
        false,
        "Select a solution first",
        false)]
    [InlineData(
        "empty-solutions",
        0,
        null,
        false,
        "No solutions found",
        null,
        false,
        "Select a solution first",
        false)]
    [InlineData(
        "unselected-solution",
        2,
        null,
        true,
        "Select a solution",
        null,
        false,
        "Select a solution first",
        false)]
    [InlineData(
        "empty-configurations",
        1,
        "Empty.slnx",
        true,
        "Select a solution",
        0,
        false,
        "No configurations found",
        false)]
    [InlineData(
        "complete",
        1,
        "Complete.slnx",
        true,
        "Select a solution",
        1,
        true,
        "Select a configuration",
        true)]
    public void Cascade_preserves_null_empty_placeholder_and_completion_semantics(
        string scenario,
        int? expectedSolutionCount,
        string? expectedSolutionPath,
        bool expectedSolutionEnabled,
        string expectedSolutionPlaceholder,
        int? expectedConfigurationCount,
        bool expectedConfigurationEnabled,
        string expectedConfigurationPlaceholder,
        bool expectedComplete)
    {
        BuildSelectionState state = CascadeState(scenario);

        Assert.Equal(expectedSolutionCount, state.Solutions?.Count);
        Assert.Equal(expectedSolutionPath, state.SelectedSolution?.SolutionPath);
        Assert.Equal(expectedSolutionEnabled, state.SolutionEnabled);
        Assert.Equal(expectedSolutionPlaceholder, state.SolutionPlaceholder);
        Assert.Equal(expectedConfigurationCount, state.Configurations?.Count);
        Assert.Equal(expectedConfigurationEnabled, state.ConfigurationEnabled);
        Assert.Equal(expectedConfigurationPlaceholder, state.ConfigurationPlaceholder);
        Assert.Equal(expectedComplete, state.IsComplete);
    }

    [Theory]
    [InlineData("add-only", "Only.slnx")]
    [InlineData("add-multiple", null)]
    [InlineData("config-only-with-different-plugin", "Only.slnx")]
    [InlineData("config-saved-case-insensitive", "SOLUTIONS/SAVED.SLNX")]
    [InlineData("config-saved-requires-saved-plugin", null)]
    public void Solution_selection_preserves_dialog_policy(string scenario, string? expectedSolutionPath)
    {
        BuildSelectionState state = SolutionState(scenario);

        Assert.Equal(expectedSolutionPath, state.SelectedSolution?.SolutionPath);
    }

    [Theory]
    [InlineData("add-debug-x64", "dEbUg | X64")]
    [InlineData("add-any-debug", "Debug | Any CPU")]
    [InlineData("add-first", "Release | ARM64")]
    [InlineData("config-saved-case-insensitive", "rELEASE | Arm64")]
    [InlineData("config-saved-does-not-require-saved-plugin", "Release | ARM64")]
    [InlineData("config-saved-requires-saved-solution", "Debug | x64")]
    [InlineData("config-debug-x64", "DEBUG | X64")]
    [InlineData("config-does-not-fall-back-to-any-debug", "Release | ARM64")]
    public void Configuration_selection_preserves_dialog_fallback_order(
        string scenario,
        string expectedConfiguration)
    {
        BuildSelectionState state = ConfigurationState(scenario);

        Assert.Equal(expectedConfiguration, state.SelectedConfiguration?.DisplayName);
    }

    [Fact]
    public void Explicit_configuration_selection_updates_the_renderable_result()
    {
        BuildConfiguration release = Configuration("Release", "Any CPU");
        BuildSelectionState state = AddState(Configuration("Debug", "x64"), release);

        BuildSelectionState selected = state.SelectConfiguration(release);

        Assert.Same(release, selected.SelectedConfiguration);
        Assert.True(selected.IsComplete);
    }

    private static BuildSelectionState InitialState(string scenario)
    {
        return scenario switch
        {
            "add-empty" => BuildSelectionState.ForAdd(Options()),
            "config-empty" => BuildSelectionState.ForConfig(
                Options(),
                SavedProfile("Saved.csproj", "Saved.slnx")),
            "add-single" => BuildSelectionState.ForAdd(Options(Plugin("Only.csproj"))),
            "add-multiple" => BuildSelectionState.ForAdd(Options(
                Plugin("One.csproj"),
                Plugin("Two.csproj"))),
            "config-saved-case-insensitive" => BuildSelectionState.ForConfig(
                Options(Plugin("Other.csproj"), Plugin("PLUGINS/SAVED.CSPROJ")),
                SavedProfile("plugins/saved.csproj", "Saved.slnx")),
            "config-missing-saved-single" => BuildSelectionState.ForConfig(
                Options(Plugin("Only.csproj")),
                SavedProfile("Missing.csproj", "Saved.slnx")),
            "config-missing-saved-multiple" => BuildSelectionState.ForConfig(
                Options(Plugin("One.csproj"), Plugin("Two.csproj")),
                SavedProfile("Missing.csproj", "Saved.slnx")),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };
    }

    private static BuildSelectionState CascadeState(string scenario)
    {
        if (scenario == "null-plugin")
        {
            return BuildSelectionState.ForAdd(Options(Plugin("One.csproj"), Plugin("Two.csproj")))
                .SelectPlugin(null);
        }

        if (scenario == "empty-solutions")
        {
            PluginBuildOptions plugin = Plugin("Empty.csproj");
            return BuildSelectionState.ForAdd(Options(plugin, Plugin("Other.csproj")))
                .SelectPlugin(plugin);
        }

        if (scenario == "unselected-solution")
        {
            PluginBuildOptions plugin = Plugin(
                "Multiple.csproj",
                Solution("One.slnx", Configuration("Debug", "x64")),
                Solution("Two.slnx", Configuration("Debug", "x64")));
            return BuildSelectionState.ForAdd(Options(plugin));
        }

        if (scenario == "empty-configurations")
        {
            return BuildSelectionState.ForAdd(Options(Plugin(
                "Empty.csproj",
                Solution("Empty.slnx"))));
        }

        if (scenario == "complete")
        {
            return BuildSelectionState.ForAdd(Options(Plugin(
                "Complete.csproj",
                Solution("Complete.slnx", Configuration("Debug", "x64")))));
        }

        throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
    }

    private static BuildSelectionState SolutionState(string scenario)
    {
        if (scenario == "add-only")
        {
            return BuildSelectionState.ForAdd(Options(Plugin(
                "Plugin.csproj",
                Solution("Only.slnx"))));
        }

        if (scenario == "add-multiple")
        {
            return BuildSelectionState.ForAdd(Options(Plugin(
                "Plugin.csproj",
                Solution("One.slnx"),
                Solution("Two.slnx"))));
        }

        if (scenario == "config-only-with-different-plugin")
        {
            return BuildSelectionState.ForConfig(
                Options(Plugin("Only.csproj", Solution("Only.slnx"))),
                SavedProfile("Missing.csproj", "Saved.slnx"));
        }

        if (scenario == "config-saved-case-insensitive")
        {
            return BuildSelectionState.ForConfig(
                Options(Plugin(
                    "PLUGINS/SAVED.CSPROJ",
                    Solution("Other.slnx"),
                    Solution("SOLUTIONS/SAVED.SLNX"))),
                SavedProfile("plugins/saved.csproj", "solutions/saved.slnx"));
        }

        if (scenario == "config-saved-requires-saved-plugin")
        {
            return BuildSelectionState.ForConfig(
                Options(Plugin(
                    "Different.csproj",
                    Solution("Other.slnx"),
                    Solution("Saved.slnx"))),
                SavedProfile("Saved.csproj", "Saved.slnx"));
        }

        throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
    }

    private static BuildSelectionState ConfigurationState(string scenario)
    {
        if (scenario == "add-debug-x64")
        {
            return AddState(
                Configuration("Release", "Any CPU"),
                Configuration("Debug", "Any CPU"),
                Configuration("dEbUg", "X64"));
        }

        if (scenario == "add-any-debug")
        {
            return AddState(
                Configuration("Release", "ARM64"),
                Configuration("Debug", "Any CPU"));
        }

        if (scenario == "add-first")
        {
            return AddState(
                Configuration("Release", "ARM64"),
                Configuration("Profile", "Any CPU"));
        }

        if (scenario == "config-saved-case-insensitive")
        {
            return ConfigState(
                "Plugin.csproj",
                "Saved.slnx",
                SavedProfile("plugin.csproj", "saved.slnx", "release", "arm64"),
                Configuration("Debug", "x64"),
                Configuration("rELEASE", "Arm64"));
        }

        if (scenario == "config-saved-does-not-require-saved-plugin")
        {
            return ConfigState(
                "Different.csproj",
                "Saved.slnx",
                SavedProfile("Saved.csproj", "Saved.slnx", "Release", "ARM64"),
                Configuration("Debug", "x64"),
                Configuration("Release", "ARM64"));
        }

        if (scenario == "config-saved-requires-saved-solution")
        {
            return ConfigState(
                "Plugin.csproj",
                "Different.slnx",
                SavedProfile("Plugin.csproj", "Saved.slnx", "Release", "ARM64"),
                Configuration("Release", "ARM64"),
                Configuration("Debug", "x64"));
        }

        if (scenario == "config-debug-x64")
        {
            return ConfigState(
                "Plugin.csproj",
                "Selected.slnx",
                SavedProfile("Plugin.csproj", "Selected.slnx", "Missing", "Any CPU"),
                Configuration("Release", "ARM64"),
                Configuration("DEBUG", "X64"));
        }

        if (scenario == "config-does-not-fall-back-to-any-debug")
        {
            return ConfigState(
                "Plugin.csproj",
                "Selected.slnx",
                SavedProfile("Plugin.csproj", "Other.slnx", "Missing", "Any CPU"),
                Configuration("Release", "ARM64"),
                Configuration("Debug", "Any CPU"));
        }

        throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
    }

    private static BuildSelectionState AddState(params BuildConfiguration[] configurations) =>
        BuildSelectionState.ForAdd(Options(Plugin(
            "Plugin.csproj",
            Solution("Selected.slnx", configurations))));

    private static BuildSelectionState ConfigState(
        string pluginPath,
        string solutionPath,
        BuildProfile savedProfile,
        params BuildConfiguration[] configurations) =>
        BuildSelectionState.ForConfig(
            Options(Plugin(pluginPath, Solution(solutionPath, configurations))),
            savedProfile);

    private static ProjectBuildOptions Options(params PluginBuildOptions[] plugins) => new ProjectBuildOptions(plugins);

    private static PluginBuildOptions Plugin(string path, params SolutionBuildOptions[] solutions) =>
        new PluginBuildOptions(path, solutions);

    private static SolutionBuildOptions Solution(string path, params BuildConfiguration[] configurations) =>
        new SolutionBuildOptions(path, configurations);

    private static BuildConfiguration Configuration(string configuration, string platform) =>
        new BuildConfiguration(configuration, platform);

    private static BuildProfile SavedProfile(
        string pluginPath,
        string solutionPath,
        string configuration = "Release",
        string platform = "Any CPU") =>
        new BuildProfile(
            solutionPath,
            pluginPath,
            Array.Empty<BuildConfiguration>(),
            Configuration(configuration, platform),
            LaunchMode.BuildAndLaunch,
            new BuildArtifactProfile(Guid.Empty, "netfx", Array.Empty<string>()));
}
