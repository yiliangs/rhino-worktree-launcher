using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace RhinoWorktreeLauncher;

public sealed class GitWorktreeScanner
{
    public IReadOnlyList<WorktreeEntry> Scan(ProjectManifest project)
    {
        string primaryRoot = GetPrimaryRoot(project.RepositoryRoot);
        string comparisonCommit = GetComparisonCommit(primaryRoot);
        string output = RunGit(primaryRoot, "worktree", "list", "--porcelain");
        List<WorktreeEntry> entries = Parse(output, primaryRoot, comparisonCommit, project);
        return entries
            .OrderByDescending(entry => entry.IsPrimary)
            .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string GetPrimaryRoot(string repositoryRoot)
    {
        string commonDirectory = RunGit(
            repositoryRoot,
            "rev-parse",
            "--path-format=absolute",
            "--git-common-dir").Trim();
        return Path.GetFullPath(Path.GetDirectoryName(commonDirectory)!);
    }

    private static string GetComparisonCommit(string primaryRoot)
    {
        string? defaultBranch = TryRunGit(
            primaryRoot,
            "symbolic-ref",
            "--short",
            "refs/remotes/origin/HEAD")?.Trim();
        string comparisonRef = string.IsNullOrWhiteSpace(defaultBranch) ? "HEAD" : defaultBranch;
        return RunGit(primaryRoot, "rev-parse", comparisonRef).Trim();
    }

    private static List<WorktreeEntry> Parse(
        string output,
        string primaryRoot,
        string comparisonCommit,
        ProjectManifest project)
    {
        List<WorktreeEntry> entries = new List<WorktreeEntry>();
        string? path = null;
        string branch = "detached";
        bool prunable = false;

        foreach (string line in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                AddEntry(entries, path, branch, primaryRoot, comparisonCommit, prunable, project);
                path = line.Substring("worktree ".Length).Trim();
                branch = "detached";
                prunable = false;
            }
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal))
            {
                branch = line.Substring("branch refs/heads/".Length).Trim();
            }
            else if (line.StartsWith("prunable ", StringComparison.Ordinal))
            {
                prunable = true;
            }
            else if (line.Length == 0)
            {
                AddEntry(entries, path, branch, primaryRoot, comparisonCommit, prunable, project);
                path = null;
                branch = "detached";
                prunable = false;
            }
        }

        AddEntry(entries, path, branch, primaryRoot, comparisonCommit, prunable, project);
        return entries;
    }

    private static void AddEntry(
        ICollection<WorktreeEntry> entries,
        string? path,
        string branch,
        string primaryRoot,
        string comparisonCommit,
        bool prunable,
        ProjectManifest project)
    {
        if (string.IsNullOrWhiteSpace(path) || prunable)
            return;

        string fullPath = Path.GetFullPath(path);
        bool isPrimary = string.Equals(
            fullPath.TrimEnd('\\'),
            primaryRoot.TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
        string launcherPath = Path.Combine(fullPath, project.WorktreeLaunch.Entrypoint);
        string rhinoPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            $"Rhino {project.PrimaryLaunch.RhinoVersion}",
            "System",
            "Rhino.exe");
        bool worktreeReady = File.Exists(launcherPath) &&
            project.Readiness.RequiredFiles.All(relativePath =>
                File.Exists(Path.Combine(fullPath, relativePath)));
        (int ahead, int behind) = GetDivergence(fullPath, comparisonCommit);

        entries.Add(new WorktreeEntry
        {
            Project = project,
            DisplayName = isPrimary ? branch : new DirectoryInfo(fullPath).Name,
            BranchName = branch,
            Path = fullPath,
            LauncherPath = launcherPath,
            LastActivityAt = GetLastActivity(fullPath),
            AheadCount = ahead,
            BehindCount = behind,
            IsPrimary = isPrimary,
            CanLaunch = isPrimary ? File.Exists(rhinoPath) : worktreeReady
        });
    }

    private static (int Ahead, int Behind) GetDivergence(
        string workingDirectory,
        string comparisonCommit)
    {
        string? output = TryRunGit(
            workingDirectory,
            "rev-list",
            "--left-right",
            "--count",
            $"{comparisonCommit}...HEAD");
        if (string.IsNullOrWhiteSpace(output))
            return (0, 0);

        string[] counts = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (counts.Length != 2 ||
            !int.TryParse(counts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int behind) ||
            !int.TryParse(counts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int ahead))
        {
            return (0, 0);
        }

        return (ahead, behind);
    }

    private static DateTimeOffset GetLastActivity(string workingDirectory)
    {
        string? output = TryRunGit(workingDirectory, "log", "-1", "--format=%ct", "HEAD");
        return long.TryParse(output?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long timestamp)
            ? DateTimeOffset.FromUnixTimeSeconds(timestamp)
            : DateTimeOffset.MinValue;
    }

    private static string? TryRunGit(string workingDirectory, params string[] arguments)
    {
        try
        {
            return RunGit(workingDirectory, arguments);
        }
        catch
        {
            return null;
        }
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(workingDirectory);
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start Git.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(error.Trim());
        return output;
    }
}
