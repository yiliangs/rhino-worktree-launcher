using System.Reflection;
using System.Text;

namespace RhinoWorktreeLauncher;

internal static class ProjectDriverScaffolder
{
    internal const string LegacyDriverRelativePath = "tools/rhino-worktree/Driver.ps1";
    private const string DriverResourceName = "RhinoWorktreeLauncher.Templates.Driver.ps1";
    private const string NatalieDriverResourceName = "RhinoWorktreeLauncher.Templates.Natalie.Driver.ps1";

    public static async Task<ProjectDriverCreation> CreateAsync(
        string repositoryPath,
        string driverPath,
        string? legacyEntrypoint,
        string projectId,
        CancellationToken cancellationToken)
    {
        string repositoryRoot = Path.GetFullPath(repositoryPath);
        string destination = Path.GetFullPath(driverPath);
        if (!Directory.Exists(repositoryRoot))
            throw new DirectoryNotFoundException($"Repository directory was not found at '{repositoryRoot}'.");
        if (File.Exists(destination))
            return new ProjectDriverCreation(repositoryRoot, destination, false);

        string? source = await FindLegacyDriverAsync(
            repositoryRoot,
            string.IsNullOrWhiteSpace(legacyEntrypoint) ||
                legacyEntrypoint.StartsWith("projects", StringComparison.OrdinalIgnoreCase)
                    ? LegacyDriverRelativePath
                    : legacyEntrypoint,
            cancellationToken);
        string contents = source is null
            ? await ReadDriverTemplateAsync(projectId, cancellationToken)
            : await File.ReadAllTextAsync(source, cancellationToken);
        await WriteNewFileAsync(destination, contents, cancellationToken);
        return new ProjectDriverCreation(repositoryRoot, destination, true);
    }

    private static async Task<string?> FindLegacyDriverAsync(
        string repositoryRoot,
        string relativePath,
        CancellationToken cancellationToken)
    {
        if (Path.IsPathRooted(relativePath))
            return null;

        string listing;
        try
        {
            listing = await ProcessRunner.RunAsync(
                "git",
                repositoryRoot,
                new[] { "-C", repositoryRoot, "worktree", "list", "--porcelain" },
                cancellationToken);
        }
        catch
        {
            listing = $"worktree {repositoryRoot}";
        }

        foreach (string line in listing.Replace("\r\n", "\n").Split('\n'))
        {
            if (!line.StartsWith("worktree ", StringComparison.Ordinal))
                continue;
            string worktree = Path.GetFullPath(line["worktree ".Length..].Trim());
            string rootPrefix = worktree.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(worktree, relativePath));
            if (candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static async Task<string> ReadDriverTemplateAsync(
        string projectId,
        CancellationToken cancellationToken)
    {
        Assembly assembly = typeof(ProjectDriverScaffolder).Assembly;
        string resourceName = string.Equals(projectId, "natalie", StringComparison.OrdinalIgnoreCase)
            ? NatalieDriverResourceName
            : DriverResourceName;
        await using Stream stream = assembly.GetManifestResourceStream(resourceName) ??
            throw new InvalidOperationException("The embedded RWL driver template is unavailable.");
        using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task WriteNewFileAsync(
        string path,
        string contents,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, contents, new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
