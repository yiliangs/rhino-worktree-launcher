using System.IO;

namespace RhinoWorktreeLauncher;

internal static class LauncherStoragePaths
{
    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RhinoWorktreeLauncher");

    public static string ProjectCatalogPath { get; } = Path.Combine(DataRoot, "projects.json");
    public static string SnapshotCachePath { get; } = Path.Combine(DataRoot, "snapshot.json");
    public static string WebViewUserDataFolder { get; } = Path.Combine(DataRoot, "WebView2");

    public static void EnsureDataRoot() => Directory.CreateDirectory(DataRoot);
}
