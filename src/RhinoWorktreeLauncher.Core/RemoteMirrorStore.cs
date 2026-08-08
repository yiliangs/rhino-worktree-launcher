using System.Security.Cryptography;
using System.Text;

namespace RhinoWorktreeLauncher;

internal sealed record RemoteMirror(string RepositoryPath, string ComparisonRef);

internal sealed class RemoteMirrorStore
{
    private const int LockAttempts = 400;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(50);
    private readonly LauncherBackendOptions _options;

    public RemoteMirrorStore(LauncherBackendOptions options) => _options = options;

    public async Task<RemoteMirror> RefreshAsync(
        ProjectSnapshot project,
        CancellationToken cancellationToken)
    {
        string remoteUrl = (await RunSourceGitAsync(
            project.Registration.PrimaryCheckout,
            new[] { "config", "--get", "remote.origin.url" },
            cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(remoteUrl))
            throw new InvalidOperationException("The registered project does not define remote 'origin'.");

        Directory.CreateDirectory(_options.RemotesDirectory);
        string mirrorPath = Path.GetFullPath(Path.Combine(
            _options.RemotesDirectory,
            project.ProjectId + ".git"));
        string rootPrefix = Path.GetFullPath(_options.RemotesDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!mirrorPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The remote mirror path escaped RWL application storage.");

        await using FileStream mirrorLock = await AcquireLockAsync(mirrorPath, cancellationToken);
        if (!Directory.Exists(mirrorPath))
        {
            await ProcessRunner.RunAsync(
                _options.GitExecutable,
                _options.RemotesDirectory,
                new[] { "clone", "--mirror", "--quiet", "--no-local", remoteUrl, mirrorPath },
                cancellationToken);
        }
        else
        {
            await RunMirrorGitAsync(
                mirrorPath,
                new[] { "remote", "set-url", "origin", remoteUrl },
                cancellationToken);
            await RunMirrorGitAsync(
                mirrorPath,
                new[] { "fetch", "--prune", "--quiet", "origin" },
                cancellationToken);
        }

        string comparisonRef = (await RunMirrorGitAsync(
            mirrorPath,
            new[] { "symbolic-ref", "HEAD" },
            cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(comparisonRef))
            throw new InvalidOperationException("The remote mirror does not expose a default branch.");
        return new RemoteMirror(mirrorPath, comparisonRef);
    }

    public async Task<(int Ahead, int Behind)> GetDivergenceAsync(
        RemoteMirror mirror,
        string worktreePath,
        CancellationToken cancellationToken)
    {
        string localRef = "refs/rwl/worktrees/" + StableName(worktreePath);
        await using FileStream mirrorLock = await AcquireLockAsync(mirror.RepositoryPath, cancellationToken);
        await RunMirrorGitAsync(
            mirror.RepositoryPath,
            new[]
            {
                "fetch",
                "--quiet",
                "--no-tags",
                "--no-write-fetch-head",
                worktreePath,
                $"+HEAD:{localRef}"
            },
            cancellationToken);
        string output = await RunMirrorGitAsync(
            mirror.RepositoryPath,
            new[] { "rev-list", "--left-right", "--count", $"{mirror.ComparisonRef}...{localRef}" },
            cancellationToken);
        string[] values = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return values.Length == 2 &&
            int.TryParse(values[0], out int behind) &&
            int.TryParse(values[1], out int ahead)
            ? (ahead, behind)
            : (0, 0);
    }

    private Task<string> RunSourceGitAsync(
        string workingDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken) => ProcessRunner.RunAsync(
        _options.GitExecutable,
        workingDirectory,
        new[] { "--no-optional-locks", "-C", workingDirectory }.Concat(arguments),
        cancellationToken);

    private Task<string> RunMirrorGitAsync(
        string mirrorPath,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken) => ProcessRunner.RunAsync(
        _options.GitExecutable,
        _options.RemotesDirectory,
        new[] { "--git-dir", mirrorPath }.Concat(arguments),
        cancellationToken);

    private static string StableName(string path)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }

    private static async Task<FileStream> AcquireLockAsync(
        string mirrorPath,
        CancellationToken cancellationToken)
    {
        string lockPath = mirrorPath + ".rwl.lock";
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
        throw new IOException($"Could not acquire the remote mirror lock at '{lockPath}'.");
    }
}
