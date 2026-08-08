using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RhinoWorktreeLauncher;

internal sealed class WorktreeWorkspaceManager
{
    private const int LockAttempts = 200;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(50);
    private readonly LauncherBackendOptions _options;

    public WorktreeWorkspaceManager(LauncherBackendOptions options) => _options = options;

    public async Task<WorktreeWorkspace> PrepareAsync(
        ResolvedContext context,
        CancellationToken cancellationToken)
    {
        string worktreeId = await ResolveWorktreeIdAsync(context.WorktreePath, cancellationToken);
        string workspaceRoot = Path.Combine(_options.WorkspacesDirectory, context.ProjectId, worktreeId);
        string sourceRoot = Path.Combine(workspaceRoot, "source");
        string buildRoot = Path.Combine(workspaceRoot, "build");
        string manifestPath = Path.Combine(workspaceRoot, "source-manifest.json");
        Directory.CreateDirectory(workspaceRoot);

        await using FileStream workspaceLock = await AcquireLockAsync(workspaceRoot, cancellationToken);
        SourceManifest previous = await ReadManifestAsync(manifestPath, cancellationToken);
        SourceManifest current = await CreateManifestAsync(
            context.WorktreePath,
            previous,
            sourceRoot,
            buildRoot,
            cancellationToken);
        await WriteManifestAsync(manifestPath, current, cancellationToken);

        return new WorktreeWorkspace(
            context.ProjectId,
            worktreeId,
            Path.GetFullPath(sourceRoot),
            Path.GetFullPath(buildRoot));
    }

    public async Task ClearAsync(
        string projectId,
        string worktreeId,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(Path.Combine(_options.WorkspacesDirectory, projectId, worktreeId));
        string prefix = Path.GetFullPath(_options.WorkspacesDirectory).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The workspace cache path escaped RWL application storage.");
        if (!Directory.Exists(root))
            return;

        await using FileStream workspaceLock = await AcquireLockAsync(root, cancellationToken);
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    private async Task<string> ResolveWorktreeIdAsync(
        string worktreePath,
        CancellationToken cancellationToken)
    {
        string gitDirectory = (await RunGitReadOnlyAsync(
            worktreePath,
            new[] { "rev-parse", "--path-format=absolute", "--git-dir" },
            cancellationToken)).Trim();
        string identity = Path.GetFullPath(gitDirectory)
            .TrimEnd(Path.DirectorySeparatorChar)
            .ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }

    private async Task<SourceManifest> CreateManifestAsync(
        string worktreePath,
        SourceManifest previous,
        string sourceRoot,
        string buildRoot,
        CancellationToken cancellationToken)
    {
        string listing = await RunGitReadOnlyAsync(
            worktreePath,
            new[] { "ls-files", "--cached", "--others", "--exclude-standard", "-z" },
            cancellationToken);
        Dictionary<string, SourceFileRecord> files = new Dictionary<string, SourceFileRecord>(
            StringComparer.OrdinalIgnoreCase);
        foreach (string relativePath in listing.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            string sourcePath = ResolveContainedPath(worktreePath, relativePath);
            if (!File.Exists(sourcePath))
                continue;

            string normalized = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string hash = await HashAsync(sourcePath, cancellationToken);
            files[normalized] = new SourceFileRecord(hash, File.GetLastWriteTimeUtc(sourcePath));
        }

        foreach (string removed in previous.Files.Keys.Except(files.Keys, StringComparer.OrdinalIgnoreCase))
        {
            DeleteFileIfPresent(ResolveContainedPath(sourceRoot, removed));
            DeleteFileIfPresent(ResolveContainedPath(buildRoot, removed));
        }

        foreach ((string relativePath, SourceFileRecord record) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (previous.Files.TryGetValue(relativePath, out SourceFileRecord? old) &&
                string.Equals(old.Hash, record.Hash, StringComparison.Ordinal))
            {
                continue;
            }

            string repositoryFile = ResolveContainedPath(worktreePath, relativePath);
            CopyFile(repositoryFile, ResolveContainedPath(sourceRoot, relativePath), record.LastWriteTimeUtc);
            CopyFile(repositoryFile, ResolveContainedPath(buildRoot, relativePath), record.LastWriteTimeUtc);
        }

        RemoveEmptyDirectories(sourceRoot);
        return new SourceManifest { Files = files };
    }

    private Task<string> RunGitReadOnlyAsync(
        string workingDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken) => ProcessRunner.RunAsync(
        _options.GitExecutable,
        workingDirectory,
        new[] { "--no-optional-locks", "-C", workingDirectory }.Concat(arguments),
        cancellationToken);

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void CopyFile(string source, string destination, DateTime lastWriteTimeUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        File.SetLastWriteTimeUtc(destination, lastWriteTimeUtc);
    }

    private static void DeleteFileIfPresent(string path)
    {
        if (!File.Exists(path))
            return;
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"Git returned a rooted source path '{relativePath}'.");
        string fullRoot = Path.GetFullPath(root);
        string path = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        string prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Git source path '{relativePath}' escaped its root.");
        return path;
    }

    private static void RemoveEmptyDirectories(string root)
    {
        if (!Directory.Exists(root))
            return;
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
    }

    private static async Task<SourceManifest> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return new SourceManifest();
        return JsonSerializer.Deserialize<SourceManifest>(
            await File.ReadAllTextAsync(path, cancellationToken),
            JsonDefaults.Read) ?? new SourceManifest();
    }

    private static async Task WriteManifestAsync(
        string path,
        SourceManifest manifest,
        CancellationToken cancellationToken)
    {
        string temporaryPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(manifest, JsonDefaults.Write),
                cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static async Task<FileStream> AcquireLockAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        string lockPath = workspaceRoot + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        for (int attempt = 0; attempt < LockAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException) when (attempt < LockAttempts - 1)
            {
                await Task.Delay(LockRetryDelay, cancellationToken);
            }
        }
        throw new IOException($"Could not acquire the worktree workspace lock at '{lockPath}'.");
    }

    private sealed class SourceManifest
    {
        public Dictionary<string, SourceFileRecord> Files { get; set; } = new Dictionary<string, SourceFileRecord>(
            StringComparer.OrdinalIgnoreCase);
    }

    private sealed record SourceFileRecord(string Hash, DateTime LastWriteTimeUtc);
}
