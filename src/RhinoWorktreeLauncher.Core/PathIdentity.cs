namespace RhinoWorktreeLauncher;

internal static class PathIdentity
{
    public static bool AreEquivalent(string left, string right) => string.Equals(
        Full(left),
        Full(right),
        StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the path names something inside the directory. The directory itself is not
    /// inside itself, which is what tells a worktree's build output from the worktree.
    /// </summary>
    public static bool IsUnder(string path, string directory) => Full(path).StartsWith(
        Full(directory) + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase);

    private static string Full(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
}
