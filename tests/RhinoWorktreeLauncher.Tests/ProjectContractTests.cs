using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class ProjectContractTests
{
    [Fact]
    public void Default_contract_uses_an_app_local_project_driver()
    {
        string repository = Path.Combine(Path.GetTempPath(), "Sample Plugin");

        ProjectContract contract = ProjectContract.CreateDefault(repository);
        contract.Validate(repository);

        Assert.Equal("sample-plugin", contract.ProjectId);
        Assert.Equal("Sample Plugin", contract.DisplayName);
        Assert.Equal(1, contract.Driver.ProtocolVersion);
        Assert.Equal(Path.Combine("projects", "sample-plugin", "Driver.ps1"), contract.Driver.Entrypoint);
        Assert.Equal("rhino-package-directory", contract.Launch.Mode);
    }

    [Fact]
    public void Driver_must_remain_inside_the_application_directory()
    {
        string repository = Path.Combine(Path.GetTempPath(), "Sample Plugin");
        ProjectContract contract = new ProjectContract
        {
            ProjectId = "sample-plugin",
            DisplayName = "Sample Plugin",
            Driver = new DriverContract { ProtocolVersion = 1, Entrypoint = "../Driver.ps1" },
            Launch = new LaunchContract { RhinoVersion = 8, Mode = "rhino-package-directory" }
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            contract.Validate(repository));

        Assert.Contains("application directory", exception.Message);
    }
}
