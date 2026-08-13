using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class PathIdentityTests
{
    [Fact]
    public void Equivalent_paths_ignore_case_and_trailing_directory_separators()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string directory = temporary.CreateDirectory("Repository/Worktree");
        string alternate = directory.ToUpperInvariant() + Path.DirectorySeparatorChar;

        Assert.True(PathIdentity.AreEquivalent(directory, alternate));
    }

    [Fact]
    public void Different_full_paths_are_not_equivalent()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string first = temporary.CreateDirectory("first");
        string second = temporary.CreateDirectory("second");

        Assert.False(PathIdentity.AreEquivalent(first, second));
    }
}
