using System.Text.Json;

namespace RhinoWorktreeLauncher;

// Serializes registry mutations across launcher processes through an exclusively
// opened lock file. A waiting caller is told who holds the lock rather than blocking
// silently until some outer timeout expires, so a launch that queues behind another
// session reports that fact instead of failing as a generic timeout.
internal static class FileLock
{
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(2);

    public static async Task<FileLockHandle> AcquireAsync(
        string path,
        FileLockHolder holder,
        IProgress<FileLockWait>? waiting,
        CancellationToken cancellationToken)
    {
        string holderPath = HolderPath(path);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        DateTimeOffset nextReport = startedAt;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream? stream = TryOpen(path);
            if (stream is not null)
            {
                WriteHolder(holderPath, holder);
                return new FileLockHandle(stream, holderPath);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (waiting is not null && now >= nextReport)
            {
                nextReport = now + ReportInterval;
                waiting.Report(new FileLockWait(path, ReadHolder(holderPath), now - startedAt));
            }
            await Task.Delay(PollDelay, cancellationToken);
        }
    }

    // The metadata is advisory: it names the holder for a waiting caller's diagnostics and
    // never gates the lock itself, which stays owned by the exclusive file handle.
    public static FileLockHolder? ReadHolder(string holderPath)
    {
        try
        {
            return File.Exists(holderPath)
                ? JsonSerializer.Deserialize<FileLockHolder>(File.ReadAllText(holderPath), JsonDefaults.Read)
                : null;
        }
        // A holder writing its own metadata, or one that died mid-write, leaves the file
        // briefly unreadable. The waiting caller still reports that it is waiting; only the
        // holder's identity is unavailable, and the caller says so.
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string HolderPath(string lockPath) => lockPath + ".holder.json";

    private static FileStream? TryOpen(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void WriteHolder(string holderPath, FileLockHolder holder) => File.WriteAllText(
        holderPath,
        JsonSerializer.Serialize(holder, JsonDefaults.Write));
}

// Who is holding one lock file, recorded beside it so a waiting caller can name them.
internal sealed record FileLockHolder(
    string LaunchId,
    int ProcessId,
    string HostKind,
    DateTimeOffset AcquiredAt)
{
    public string Describe() =>
        $"launch {LaunchId} ({HostKind} host, process {ProcessId}, holding since {AcquiredAt:O})";
}

// One observation of a blocked acquisition. A null holder means the metadata was
// unavailable, which the description states rather than hiding.
internal sealed record FileLockWait(string LockPath, FileLockHolder? Holder, TimeSpan Waited)
{
    public string HolderDescription => Holder?.Describe() ??
        $"an unidentified holder of '{LockPath}'";
}

internal sealed class FileLockHandle : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _holderPath;
    private bool _disposed;

    public FileLockHandle(FileStream stream, string holderPath)
    {
        _stream = stream;
        _holderPath = holderPath;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // The metadata is deleted before the handle so no waiting caller can observe a
        // free lock still naming this holder.
        try
        {
            File.Delete(_holderPath);
        }
        // The metadata is diagnostics, and the lock release below is the correctness
        // requirement. A file another process is reading stays behind and is overwritten
        // by the next holder.
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        finally
        {
            _stream.Dispose();
        }
    }
}
