using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace RhinoWorktreeLauncher;

public sealed class GitWorktreeScanner
{
    public IReadOnlyList<WorktreeEntry> ScanFast(ProjectManifest project) =>
        ScanCore(
            project,
            new Dictionary<string, PullRequestInfo>(StringComparer.OrdinalIgnoreCase),
            includeLocalState: false,
            includeDivergence: false);

    public IReadOnlyList<WorktreeEntry> ScanLocal(ProjectManifest project) =>
        ScanCore(
            project,
            new Dictionary<string, PullRequestInfo>(StringComparer.OrdinalIgnoreCase),
            includeLocalState: true,
            includeDivergence: false);

    public IReadOnlyList<WorktreeEntry> Scan(
        ProjectManifest project,
        IReadOnlyDictionary<string, PullRequestInfo>? pullRequests = null) =>
        ScanCore(
            project,
            pullRequests ?? new Dictionary<string, PullRequestInfo>(StringComparer.OrdinalIgnoreCase),
            includeLocalState: true,
            includeDivergence: true);

    public IReadOnlyList<WorktreeEntry> EnrichGit(
        ProjectManifest project,
        IReadOnlyList<WorktreeEntry> localEntries,
        IReadOnlyDictionary<string, PullRequestInfo>? pullRequests = null) =>
        ScanCore(
            project,
            pullRequests ?? new Dictionary<string, PullRequestInfo>(StringComparer.OrdinalIgnoreCase),
            includeLocalState: true,
            includeDivergence: true,
            localEntries.ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyList<WorktreeEntry> ScanCore(
        ProjectManifest project,
        IReadOnlyDictionary<string, PullRequestInfo> pullRequests,
        bool includeLocalState,
        bool includeDivergence,
        IReadOnlyDictionary<string, WorktreeEntry>? localEntries = null)
    {
        string primaryRoot = GetPrimaryRoot(project.RepositoryRoot);
        string comparisonCommit = includeDivergence ? GetComparisonCommit(primaryRoot) : string.Empty;
        string output = RunGit(primaryRoot, "worktree", "list", "--porcelain");
        List<WorktreeEntry> entries = Parse(
            output,
            primaryRoot,
            comparisonCommit,
            project,
            pullRequests,
            includeLocalState,
            includeDivergence,
            localEntries);
        if (includeDivergence)
            ApplyDivergenceScale(entries);
        return entries
            .OrderByDescending(entry => entry.IsPrimary)
            .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Fetch(ProjectManifest project)
    {
        string primaryRoot = GetPrimaryRoot(project.RepositoryRoot);
        _ = RunGit(primaryRoot, "fetch", "--prune", "--quiet");
    }

    public IReadOnlyDictionary<string, PullRequestInfo> GetPullRequests(ProjectManifest project)
    {
        string primaryRoot = GetPrimaryRoot(project.RepositoryRoot);
        string? output = TryRunProcess(
            primaryRoot,
            ResolveGitHubCliPath(),
            "pr",
            "list",
            "--state",
            "open",
            "--limit",
            "100",
            "--json",
            "number,headRefName,isDraft");
        if (string.IsNullOrWhiteSpace(output))
            return new Dictionary<string, PullRequestInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            PullRequestInfo[] pullRequests = JsonSerializer.Deserialize<PullRequestInfo[]>(
                output,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? Array.Empty<PullRequestInfo>();
            return pullRequests
                .Where(pullRequest => !string.IsNullOrWhiteSpace(pullRequest.HeadRefName))
                .GroupBy(pullRequest => pullRequest.HeadRefName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(pullRequest => pullRequest.Number).First(),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, PullRequestInfo>(StringComparer.OrdinalIgnoreCase);
        }
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
        ProjectManifest project,
        IReadOnlyDictionary<string, PullRequestInfo> pullRequests,
        bool includeLocalState,
        bool includeDivergence,
        IReadOnlyDictionary<string, WorktreeEntry>? localEntries)
    {
        List<WorktreeDescriptor> descriptors = new List<WorktreeDescriptor>();
        string? path = null;
        string branch = "detached";
        bool prunable = false;

        foreach (string line in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                AddDescriptor(descriptors, path, branch, prunable);
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
                AddDescriptor(descriptors, path, branch, prunable);
                path = null;
                branch = "detached";
                prunable = false;
            }
        }
        AddDescriptor(descriptors, path, branch, prunable);

        return descriptors
            .AsParallel()
            .AsOrdered()
            .Select(descriptor => CreateEntry(
                descriptor,
                primaryRoot,
                comparisonCommit,
                project,
                pullRequests,
                includeLocalState,
                includeDivergence,
                localEntries))
            .ToList();
    }

    private static void AddDescriptor(
        ICollection<WorktreeDescriptor> descriptors,
        string? path,
        string branch,
        bool prunable)
    {
        if (!string.IsNullOrWhiteSpace(path) && !prunable)
            descriptors.Add(new WorktreeDescriptor(path, branch));
    }

    private static WorktreeEntry CreateEntry(
        WorktreeDescriptor descriptor,
        string primaryRoot,
        string comparisonCommit,
        ProjectManifest project,
        IReadOnlyDictionary<string, PullRequestInfo> pullRequests,
        bool includeLocalState,
        bool includeDivergence,
        IReadOnlyDictionary<string, WorktreeEntry>? localEntries)
    {
        string fullPath = Path.GetFullPath(descriptor.Path);
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
        (int ahead, int behind) = includeDivergence
            ? GetDivergence(fullPath, comparisonCommit)
            : (0, 0);
        WorktreeEntry? localEntry = null;
        if (localEntries is not null)
            _ = localEntries.TryGetValue(fullPath, out localEntry);
        (int added, int deleted) = localEntry is not null
            ? (localEntry.LocalAdded, localEntry.LocalDeleted)
            : includeLocalState
                ? GetLocalDiff(fullPath)
                : (0, 0);
        DateTimeOffset lastActivity = localEntry?.LastActivityAt ??
            (includeLocalState ? GetLastActivity(fullPath) : DateTimeOffset.MinValue);
        _ = pullRequests.TryGetValue(descriptor.Branch, out PullRequestInfo? pullRequest);

        return new WorktreeEntry
        {
            Project = project,
            DisplayName = isPrimary ? descriptor.Branch : new DirectoryInfo(fullPath).Name,
            BranchName = descriptor.Branch,
            Path = fullPath,
            LauncherPath = launcherPath,
            LastActivityAt = lastActivity,
            AheadCount = ahead,
            BehindCount = behind,
            LocalAdded = added,
            LocalDeleted = deleted,
            PullRequestNumber = pullRequest?.Number,
            IsPullRequestDraft = pullRequest?.IsDraft == true,
            HasLocalState = localEntry?.HasLocalState == true || includeLocalState,
            HasGitState = includeDivergence,
            IsPrimary = isPrimary,
            CanLaunch = isPrimary ? File.Exists(rhinoPath) : worktreeReady
        };
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

    private static (int Added, int Deleted) GetLocalDiff(string workingDirectory)
    {
        string? output = TryRunGit(workingDirectory, "diff", "--numstat", "HEAD");
        if (string.IsNullOrWhiteSpace(output))
            return (0, 0);

        int added = 0;
        int deleted = 0;
        foreach (string line in output.Replace("\r\n", "\n").Split('\n'))
        {
            string[] columns = line.Split('\t');
            if (columns.Length < 3)
                continue;
            if (int.TryParse(columns[0], NumberStyles.None, CultureInfo.InvariantCulture, out int fileAdded))
                added += fileAdded;
            if (int.TryParse(columns[1], NumberStyles.None, CultureInfo.InvariantCulture, out int fileDeleted))
                deleted += fileDeleted;
        }
        return (added, deleted);
    }

    private static DateTimeOffset GetLastActivity(string workingDirectory)
    {
        string? output = TryRunGit(workingDirectory, "log", "-1", "--format=%ct", "HEAD");
        return long.TryParse(output?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long timestamp)
            ? DateTimeOffset.FromUnixTimeSeconds(timestamp)
            : DateTimeOffset.MinValue;
    }

    private static void ApplyDivergenceScale(IEnumerable<WorktreeEntry> entries)
    {
        WorktreeEntry[] materialized = entries.ToArray();
        int cap = Math.Max(1, materialized.Select(entry => Math.Max(entry.AheadCount, entry.BehindCount)).DefaultIfEmpty(1).Max());
        foreach (WorktreeEntry entry in materialized)
        {
            entry.AheadBarWidth = WorktreeEntry.ScaleDivergence(entry.AheadCount, cap);
            entry.BehindBarWidth = WorktreeEntry.ScaleDivergence(entry.BehindCount, cap);
        }
    }

    private static string ResolveGitHubCliPath()
    {
        string programFilesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "GitHub CLI",
            "gh.exe");
        return File.Exists(programFilesPath) ? programFilesPath : "gh";
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

    private static string RunGit(string workingDirectory, params string[] arguments) =>
        RunProcess(workingDirectory, "git", new[] { "-C", workingDirectory }.Concat(arguments).ToArray());

    private static string? TryRunProcess(
        string workingDirectory,
        string fileName,
        params string[] arguments)
    {
        try
        {
            return RunProcess(workingDirectory, fileName, arguments);
        }
        catch
        {
            return null;
        }
    }

    private static string RunProcess(
        string workingDirectory,
        string fileName,
        params string[] arguments)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException($"Could not start {fileName}.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(error.Trim());
        return output;
    }

    private sealed record WorktreeDescriptor(string Path, string Branch);
}
