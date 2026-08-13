using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class RemoteMirrorStoreTests
{
    [Fact]
    public async Task Clear_waits_for_the_same_mirror_lock_used_by_refresh_and_divergence()
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

        Assert.False(clear.IsCompleted);
        Assert.True(File.Exists(mirrorFile));
        heldLock.Dispose();
        await clear.WaitAsync(TimeSpan.FromSeconds(5));

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
