using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class RemoteMirrorStoreTests
{
    [Fact]
    public async Task Clear_preserves_non_coordinated_deletion_while_a_mirror_lock_is_held()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string remotesDirectory = temporary.CreateDirectory("launcher/remotes");
        string mirrorPath = temporary.CreateDirectory("launcher/remotes/repository.git");
        temporary.WriteFile("launcher/remotes/repository.git/cache.txt", "remote");
        string mirrorFile = temporary.PathFor("launcher/remotes/repository.git/cache.txt");
        string lockPath = mirrorPath + ".rwl.lock";
        RemoteMirrorStore store = new RemoteMirrorStore(new LauncherBackendOptions
        {
            RemotesDirectory = remotesDirectory
        });

        using FileStream heldLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        Task clear = store.ClearAsync("repository", CancellationToken.None);

        await clear;

        Assert.False(File.Exists(mirrorFile));
        Assert.False(Directory.Exists(mirrorPath));
        Assert.True(Directory.Exists(remotesDirectory));
    }

    [Fact]
    public async Task Clearing_a_missing_mirror_does_not_create_remote_storage()
    {
        using TemporaryDirectory temporary = new TemporaryDirectory();
        string remotesDirectory = temporary.PathFor("launcher/remotes");
        RemoteMirrorStore store = new RemoteMirrorStore(new LauncherBackendOptions
        {
            RemotesDirectory = remotesDirectory
        });

        await store.ClearAsync("repository", CancellationToken.None);

        Assert.False(Directory.Exists(remotesDirectory));
    }
}
