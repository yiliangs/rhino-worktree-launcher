using System.IO;

namespace RhinoWorktreeLauncher;

internal static class LauncherStoragePaths
{
    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RhinoWorktreeLauncher");

    public static string ProjectCatalogPath { get; } = Path.Combine(DataRoot, "projects.json");
    public static void EnsureDataRoot() => Directory.CreateDirectory(DataRoot);
}
