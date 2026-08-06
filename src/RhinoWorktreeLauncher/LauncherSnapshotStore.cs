using System.IO;
using System.Text.Json;

namespace RhinoWorktreeLauncher;

internal sealed class LauncherSnapshotStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public LauncherSnapshotDto? Load()
    {
        try
        {
            if (!File.Exists(LauncherStoragePaths.SnapshotCachePath))
                return null;

            LauncherSnapshotDto? snapshot = JsonSerializer.Deserialize<LauncherSnapshotDto>(
                File.ReadAllText(LauncherStoragePaths.SnapshotCachePath),
                JsonOptions);
            return snapshot?.SchemaVersion == CurrentSchemaVersion
                ? snapshot
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void Save(LauncherSnapshotDto snapshot)
    {
        LauncherStoragePaths.EnsureDataRoot();
        string temporaryPath = LauncherStoragePaths.SnapshotCachePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(temporaryPath, LauncherStoragePaths.SnapshotCachePath, true);
    }
}
