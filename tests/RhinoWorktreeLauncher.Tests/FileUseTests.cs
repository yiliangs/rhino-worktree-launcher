using System.Reflection;
using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class FileUseTests
{
    [Fact]
    public void A_loaded_assembly_is_attributed_to_its_process()
    {
        // The test host holds this assembly as a handleless mapped image — the exact
        // shape a loaded Rhino plug-in has inside the Rhino process.
        string loadedAssembly = typeof(FileUseTests).Assembly.Location;

        Assert.True(FileUse.IsFileMappedByProcess(Environment.ProcessId, loadedAssembly));
    }

    [Fact]
    public void An_unmapped_file_is_not_attributed()
    {
        string unmapped = Path.Combine(Path.GetTempPath(), $"rwl-fileuse-{Guid.NewGuid():N}.dll");
        File.WriteAllBytes(unmapped, new byte[] { 1, 2, 3 });
        try
        {
            Assert.False(FileUse.IsFileMappedByProcess(Environment.ProcessId, unmapped));
        }
        finally
        {
            File.Delete(unmapped);
        }
    }

    [Fact]
    public void An_exited_process_holds_nothing()
    {
        Assert.False(FileUse.IsFileMappedByProcess(-1, typeof(FileUseTests).Assembly.Location));
    }
}
