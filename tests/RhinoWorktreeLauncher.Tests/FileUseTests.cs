using System.ComponentModel;
using System.IO.MemoryMappedFiles;
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

    // Attribution asks the same question without an expected path, and answers in the drive
    // paths a reader can act on rather than the device paths Windows reports.
    [Fact]
    public void Mapped_files_of_one_kind_are_listed_as_drive_paths()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string artifact = temporary.PathFor("selected/Sample.rhp");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllBytes(artifact, new byte[4096]);

        using (MemoryMappedFile mapped = MemoryMappedFile.CreateFromFile(artifact, FileMode.Open))
        using (MemoryMappedViewAccessor view = mapped.CreateViewAccessor())
        {
            IReadOnlyList<string> plugIns = FileUse.MappedFilesWithExtension(Environment.ProcessId, ".rhp");

            Assert.Contains(artifact, plugIns, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                typeof(FileUseTests).Assembly.Location,
                plugIns,
                StringComparer.OrdinalIgnoreCase);
        }
    }

    // A process that cannot be opened is not a process that holds nothing, and attribution
    // may not present it as one.
    [Fact]
    public void A_process_that_cannot_be_opened_names_the_refusal()
    {
        Win32Exception exception = Assert.Throws<Win32Exception>(
            () => FileUse.MappedFilesWithExtension(-1, ".rhp"));

        Assert.Contains("refused to open process -1", exception.Message, StringComparison.Ordinal);
    }
}
