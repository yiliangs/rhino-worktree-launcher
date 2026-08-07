using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class ManifestContractTests
{
    [Fact]
    public void Schema_v2_fixture_loads_with_versioned_driver_contract()
    {
        ProjectManifest manifest = ProjectManifest.Load(FixturePath("ValidProject"));

        Assert.Equal(2, manifest.SchemaVersion);
        Assert.Equal(1, manifest.Driver.ProtocolVersion);
        Assert.Equal("rhino-package-directory", manifest.Launch.Mode);
    }

    [Fact]
    public void Schema_v1_is_a_hard_failure()
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            ProjectManifest.Load(FixturePath("LegacyProject")));

        Assert.Contains("expected 2", exception.Message);
    }

    private static string FixturePath(string name) => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        name);
}
