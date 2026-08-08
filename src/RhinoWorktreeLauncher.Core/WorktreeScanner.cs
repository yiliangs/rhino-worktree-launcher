using System.Globalization;
using System.Text.Json;

namespace RhinoWorktreeLauncher;

internal sealed class WorktreeScanner
{
    private readonly LauncherBackendOptions _options;
    private readonly RemoteMirrorStore _remoteMirrors;

    public WorktreeScanner(LauncherBackendOptions options)
    {
        _options = options;
        _remoteMirrors = new RemoteMirrorStore(options);
    }

    public async Task<CommandResult<ProjectWorktrees>> ScanAsync(
        ProjectSnapshot project,
        bool includeRemote,
        CancellationToken cancellationToken)
    {
        List<Diagnostic> diagnostics = new List<Diagnostic>();
        string primary = project.Registration.PrimaryCheckout;

        RemoteMirror? remoteMirror = null;
        if (includeRemote && !project.Registration.Access.ReadRemote)
        {
            diagnostics.Add(new Diagnostic(
                "remote_read_not_granted",
                "Remote refresh is disabled for this project. Enable remote read in project settings to synchronize remote metadata.",
                DiagnosticSeverity.Warning));
        }
        else if (includeRemote)
        {
            try
            {
                remoteMirror = await _remoteMirrors.RefreshAsync(project, cancellationToken);
            }
            catch (Exception exception)
            {
                diagnostics.Add(new Diagnostic(
                    "git_fetch_unavailable",
                    exception.Message,
                    DiagnosticSeverity.Warning));
            }
        }

        Dictionary<string, PullRequestRecord> pullRequests = includeRemote && project.Registration.Access.ReadRemote
            ? await LoadPullRequestsAsync(primary, diagnostics, cancellationToken)
            : new Dictionary<string, PullRequestRecord>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string listing = await RunGitAsync(
                primary,
                new[] { "worktree", "list", "--porcelain" },
                cancellationToken);
            List<WorktreeSnapshot> worktrees = new List<WorktreeSnapshot>();
            foreach (WorktreeDescriptor descriptor in Parse(listing))
            {
                WorktreeSnapshot snapshot = await CreateSnapshotAsync(
                    project,
                    descriptor,
                    remoteMirror,
                    pullRequests,
                    cancellationToken);
                worktrees.Add(snapshot);
            }
            ApplyDivergenceScale(worktrees);

            return CommandResult<ProjectWorktrees>.Success(
                new ProjectWorktrees(
                    project,
                    worktrees
                        .OrderByDescending(worktree => worktree.IsPrimary)
                        .ThenBy(worktree => worktree.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ToArray()),
                diagnostics);
        }
        catch (Exception exception)
        {
            diagnostics.Add(new Diagnostic("git_scan_failed", exception.Message));
            return CommandResult<ProjectWorktrees>.Failure(diagnostics.ToArray());
        }
    }

    private async Task<WorktreeSnapshot> CreateSnapshotAsync(
        ProjectSnapshot project,
        WorktreeDescriptor descriptor,
        RemoteMirror? remoteMirror,
        IReadOnlyDictionary<string, PullRequestRecord> pullRequests,
        CancellationToken cancellationToken)
    {
        string path = Path.GetFullPath(descriptor.Path);
        bool isPrimary = ContextResolver.SamePath(path, project.Registration.PrimaryCheckout);
        (int added, int deleted) = await GetLocalDiffAsync(path, cancellationToken);
        (int ahead, int behind) = remoteMirror is null
            ? (0, 0)
            : await _remoteMirrors.GetDivergenceAsync(remoteMirror, path, cancellationToken);
        DateTimeOffset lastActivity = await GetLastActivityAsync(path, cancellationToken);
        _ = pullRequests.TryGetValue(descriptor.Branch, out PullRequestRecord? pullRequest);
        bool canLaunch = project.Registration.BuildProfile.IsConfigured;

        return new WorktreeSnapshot(
            project.ProjectId,
            isPrimary ? descriptor.Branch : new DirectoryInfo(path).Name,
            descriptor.Branch,
            path,
            lastActivity,
            ahead,
            behind,
            added,
            deleted,
            pullRequest?.Number,
            pullRequest?.IsDraft == true,
            isPrimary,
            canLaunch);
    }

    private async Task<Dictionary<string, PullRequestRecord>> LoadPullRequestsAsync(
        string primary,
        ICollection<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            string output = await ProcessRunner.RunAsync(
                _options.GitHubExecutable,
                primary,
                new[] { "pr", "list", "--state", "open", "--limit", "100", "--json", "number,headRefName,isDraft" },
                cancellationToken);
            PullRequestRecord[] records = JsonSerializer.Deserialize<PullRequestRecord[]>(
                output,
                JsonDefaults.Read) ?? Array.Empty<PullRequestRecord>();
            return records
                .Where(record => !string.IsNullOrWhiteSpace(record.HeadRefName))
                .GroupBy(record => record.HeadRefName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(record => record.Number).First(),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception)
        {
            diagnostics.Add(new Diagnostic(
                "github_unavailable",
                exception.Message,
                DiagnosticSeverity.Warning));
            return new Dictionary<string, PullRequestRecord>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<(int Added, int Deleted)> GetLocalDiffAsync(
        string path,
        CancellationToken cancellationToken)
    {
        string output = await RunGitAsync(path, new[] { "diff", "--numstat", "HEAD" }, cancellationToken);
        int added = 0;
        int deleted = 0;
        foreach (string line in Lines(output))
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

    private async Task<DateTimeOffset> GetLastActivityAsync(
        string path,
        CancellationToken cancellationToken)
    {
        string output = await RunGitAsync(path, new[] { "log", "-1", "--format=%ct", "HEAD" }, cancellationToken);
        return long.TryParse(output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long timestamp)
            ? DateTimeOffset.FromUnixTimeSeconds(timestamp)
            : DateTimeOffset.MinValue;
    }

    private Task<string> RunGitAsync(
        string workingDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken) => ProcessRunner.RunAsync(
            _options.GitExecutable,
            workingDirectory,
            new[] { "--no-optional-locks", "-C", workingDirectory }.Concat(arguments),
            cancellationToken);

    private static IEnumerable<WorktreeDescriptor> Parse(string output)
    {
        string? path = null;
        string branch = "detached";
        bool prunable = false;
        foreach (string line in Lines(output).Append(string.Empty))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                if (path is not null && !prunable)
                    yield return new WorktreeDescriptor(path, branch);
                path = line["worktree ".Length..].Trim();
                branch = "detached";
                prunable = false;
            }
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal))
            {
                branch = line["branch refs/heads/".Length..].Trim();
            }
            else if (line.StartsWith("prunable ", StringComparison.Ordinal))
            {
                prunable = true;
            }
            else if (line.Length == 0 && path is not null)
            {
                if (!prunable)
                    yield return new WorktreeDescriptor(path, branch);
                path = null;
            }
        }
    }

    private static IEnumerable<string> Lines(string value) =>
        value.Replace("\r\n", "\n").Split('\n');

    private static void ApplyDivergenceScale(IReadOnlyList<WorktreeSnapshot> worktrees)
    {
        int cap = Math.Max(
            1,
            worktrees.Select(worktree => Math.Max(worktree.AheadCount, worktree.BehindCount))
                .DefaultIfEmpty(1)
                .Max());
        foreach (WorktreeSnapshot worktree in worktrees)
        {
            worktree.AheadBarWidth = WorktreeSnapshot.ScaleDivergence(worktree.AheadCount, cap);
            worktree.BehindBarWidth = WorktreeSnapshot.ScaleDivergence(worktree.BehindCount, cap);
        }
    }

    private sealed record WorktreeDescriptor(string Path, string Branch);

    private sealed class PullRequestRecord
    {
        public int Number { get; init; }
        public string HeadRefName { get; init; } = string.Empty;
        public bool IsDraft { get; init; }
    }
}
