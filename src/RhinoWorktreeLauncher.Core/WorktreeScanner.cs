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
        IProgress<WorktreeRefreshProgress>? progress,
        CancellationToken cancellationToken)
    {
        CommandResult<ProjectWorktrees> listed = await ListLocalAsync(project, cancellationToken);
        if (!listed.Succeeded || listed.Value is null)
            return listed;

        progress?.Report(new WorktreeRefreshProgress(
            WorktreeRefreshStage.LocalList,
            listed.Value));
        CommandResult<ProjectWorktrees> local = await EnrichLocalAsync(
            listed.Value,
            cancellationToken);
        if (!local.Succeeded || local.Value is null)
            return local;

        progress?.Report(new WorktreeRefreshProgress(WorktreeRefreshStage.Local, local.Value));
        if (!includeRemote)
            return local;

        RemoteRefresh remote = await RefreshRemoteAsync(project, cancellationToken);
        CommandResult<ProjectWorktrees> enriched = await EnrichRemoteAsync(
            local.Value,
            local.Diagnostics,
            remote,
            cancellationToken);
        if (enriched.Value is not null && !SameWorktrees(local.Value.Worktrees, enriched.Value.Worktrees))
        {
            progress?.Report(new WorktreeRefreshProgress(
                WorktreeRefreshStage.Remote,
                enriched.Value));
        }
        return enriched;
    }

    private async Task<CommandResult<ProjectWorktrees>> ListLocalAsync(
        ProjectSnapshot project,
        CancellationToken cancellationToken)
    {
        try
        {
            string primary = project.Registration.PrimaryCheckout;
            string listing = await RunGitAsync(
                primary,
                new[] { "worktree", "list", "--porcelain" },
                cancellationToken);
            List<WorktreeSnapshot> worktrees = new List<WorktreeSnapshot>();
            foreach (WorktreeDescriptor descriptor in Parse(listing))
                worktrees.Add(CreateListedSnapshot(project, descriptor));

            return CommandResult<ProjectWorktrees>.Success(new ProjectWorktrees(
                project,
                Order(worktrees)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return CommandResult<ProjectWorktrees>.Failure(new Diagnostic(
                "git_scan_failed",
                exception.Message));
        }
    }

    private static WorktreeSnapshot CreateListedSnapshot(
        ProjectSnapshot project,
        WorktreeDescriptor descriptor)
    {
        string path = Path.GetFullPath(descriptor.Path);
        bool isPrimary = ContextResolver.SamePath(path, project.Registration.PrimaryCheckout);
        bool canLaunch = BuildProfileResolver.IsAvailable(
            path,
            project.Registration.BuildProfile);

        return new WorktreeSnapshot(
            project.ProjectId,
            isPrimary ? descriptor.Branch : new DirectoryInfo(path).Name,
            descriptor.Branch,
            path,
            DateTimeOffset.MinValue,
            0,
            0,
            0,
            0,
            null,
            false,
            isPrimary,
            project.Registration.BuildProfile.LaunchMode,
            canLaunch,
            false,
            false);
    }

    private async Task<CommandResult<ProjectWorktrees>> EnrichLocalAsync(
        ProjectWorktrees listed,
        CancellationToken cancellationToken)
    {
        List<Diagnostic> diagnostics = new List<Diagnostic>();
        List<WorktreeSnapshot> worktrees = new List<WorktreeSnapshot>();
        foreach (WorktreeSnapshot worktree in listed.Worktrees)
        {
            try
            {
                (int added, int deleted) = await GetLocalDiffAsync(
                    worktree.Path,
                    cancellationToken);
                DateTimeOffset lastActivity = await GetLastActivityAsync(
                    worktree.Path,
                    cancellationToken);
                worktrees.Add(worktree with
                {
                    LastActivityAt = lastActivity,
                    LocalAdded = added,
                    LocalDeleted = deleted,
                    HasLocalState = true
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                diagnostics.Add(new Diagnostic(
                    "local_state_unavailable",
                    $"Could not read local state for '{worktree.Path}': {exception.Message}",
                    DiagnosticSeverity.Warning));
                worktrees.Add(worktree);
            }
        }

        return CommandResult<ProjectWorktrees>.Success(
            new ProjectWorktrees(listed.Project, Order(worktrees)),
            diagnostics);
    }

    private async Task<RemoteRefresh> RefreshRemoteAsync(
        ProjectSnapshot project,
        CancellationToken cancellationToken)
    {
        if (!project.Registration.Access.ReadRemote)
        {
            return new RemoteRefresh(
                null,
                new Dictionary<string, PullRequestRecord>(StringComparer.OrdinalIgnoreCase),
                new[]
                {
                    new Diagnostic(
                        "remote_read_not_granted",
                        "Remote refresh is disabled for this project. Enable remote read in Config to synchronize remote metadata.",
                        DiagnosticSeverity.Warning)
                });
        }

        Task<RemoteMirrorResult> mirrorTask = RefreshMirrorAsync(project, cancellationToken);
        Task<PullRequestResult> pullRequestTask = LoadPullRequestsAsync(
            project.Registration.PrimaryCheckout,
            cancellationToken);
        await Task.WhenAll(mirrorTask, pullRequestTask);
        RemoteMirrorResult mirror = await mirrorTask;
        PullRequestResult pullRequests = await pullRequestTask;
        return new RemoteRefresh(
            mirror.Mirror,
            pullRequests.PullRequests,
            mirror.Diagnostics.Concat(pullRequests.Diagnostics).ToArray());
    }

    private async Task<RemoteMirrorResult> RefreshMirrorAsync(
        ProjectSnapshot project,
        CancellationToken cancellationToken)
    {
        try
        {
            return new RemoteMirrorResult(
                await _remoteMirrors.RefreshAsync(project, cancellationToken),
                Array.Empty<Diagnostic>());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new RemoteMirrorResult(
                null,
                new[]
                {
                    new Diagnostic(
                        "git_fetch_unavailable",
                        exception.Message,
                        DiagnosticSeverity.Warning)
                });
        }
    }

    private async Task<CommandResult<ProjectWorktrees>> EnrichRemoteAsync(
        ProjectWorktrees local,
        IReadOnlyList<Diagnostic> localDiagnostics,
        RemoteRefresh remote,
        CancellationToken cancellationToken)
    {
        List<Diagnostic> diagnostics = new List<Diagnostic>(localDiagnostics);
        diagnostics.AddRange(remote.Diagnostics);
        List<WorktreeSnapshot> worktrees = new List<WorktreeSnapshot>();
        foreach (WorktreeSnapshot worktree in local.Worktrees)
        {
            int ahead = 0;
            int behind = 0;
            bool hasGitState = false;
            if (remote.Mirror is not null)
            {
                try
                {
                    (ahead, behind) = await _remoteMirrors.GetDivergenceAsync(
                        remote.Mirror,
                        worktree.Path,
                        cancellationToken);
                    hasGitState = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    diagnostics.Add(new Diagnostic(
                        "git_divergence_unavailable",
                        exception.Message,
                        DiagnosticSeverity.Warning));
                }
            }

            _ = remote.PullRequests.TryGetValue(
                worktree.BranchName,
                out PullRequestRecord? pullRequest);
            worktrees.Add(worktree with
            {
                AheadCount = ahead,
                BehindCount = behind,
                PullRequestNumber = pullRequest?.Number,
                IsPullRequestDraft = pullRequest?.IsDraft == true,
                HasGitState = hasGitState
            });
        }
        ApplyDivergenceScale(worktrees);

        return CommandResult<ProjectWorktrees>.Success(
            new ProjectWorktrees(local.Project, Order(worktrees)),
            diagnostics);
    }

    private async Task<PullRequestResult> LoadPullRequestsAsync(
        string primary,
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
            return new PullRequestResult(
                records
                    .Where(record => !string.IsNullOrWhiteSpace(record.HeadRefName))
                    .GroupBy(record => record.HeadRefName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.OrderByDescending(record => record.Number).First(),
                        StringComparer.OrdinalIgnoreCase),
                Array.Empty<Diagnostic>());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new PullRequestResult(
                new Dictionary<string, PullRequestRecord>(StringComparer.OrdinalIgnoreCase),
                new[]
                {
                    new Diagnostic(
                        "github_unavailable",
                        exception.Message,
                        DiagnosticSeverity.Warning)
                });
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

    private static IReadOnlyList<WorktreeSnapshot> Order(IEnumerable<WorktreeSnapshot> worktrees) => worktrees
        .OrderByDescending(worktree => worktree.IsPrimary)
        .ThenBy(worktree => worktree.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static bool SameWorktrees(
        IReadOnlyList<WorktreeSnapshot> left,
        IReadOnlyList<WorktreeSnapshot> right) => left.SequenceEqual(right);

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

    private sealed record RemoteRefresh(
        RemoteMirror? Mirror,
        IReadOnlyDictionary<string, PullRequestRecord> PullRequests,
        IReadOnlyList<Diagnostic> Diagnostics);

    private sealed record RemoteMirrorResult(
        RemoteMirror? Mirror,
        IReadOnlyList<Diagnostic> Diagnostics);

    private sealed record PullRequestResult(
        IReadOnlyDictionary<string, PullRequestRecord> PullRequests,
        IReadOnlyList<Diagnostic> Diagnostics);

    private sealed record WorktreeDescriptor(string Path, string Branch);

    private sealed class PullRequestRecord
    {
        public int Number { get; init; }
        public string HeadRefName { get; init; } = string.Empty;
        public bool IsDraft { get; init; }
    }
}
