namespace RhinoWorktreeLauncher;

public sealed class ContextResolver
{
    private readonly ProjectCatalog _catalog;

    public ContextResolver(ProjectCatalog catalog) => _catalog = catalog;

    public async Task<CommandResult<ResolvedContext>> ResolveAsync(
        string path,
        CancellationToken cancellationToken)
    {
        string workingDirectory = ResolveExistingDirectory(path);
        string gitCommonDirectory;
        string worktreePath;
        try
        {
            gitCommonDirectory = Path.GetFullPath((await ProcessRunner.RunAsync(
                "git",
                workingDirectory,
                new[] { "-C", workingDirectory, "rev-parse", "--path-format=absolute", "--git-common-dir" },
                cancellationToken)).Trim());
            worktreePath = Path.GetFullPath((await ProcessRunner.RunAsync(
                "git",
                workingDirectory,
                new[] { "-C", workingDirectory, "rev-parse", "--show-toplevel" },
                cancellationToken)).Trim());
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            return CommandResult<ResolvedContext>.Failure(new Diagnostic(
                "not_git_worktree",
                $"'{path}' is not inside a Git worktree: {exception.Message}"));
        }

        IReadOnlyList<ProjectRegistration> registrations =
            await _catalog.LoadRegistrationsAsync(cancellationToken);
        ProjectRegistration? registration = registrations.FirstOrDefault(candidate =>
            SamePath(candidate.GitCommonDirectory, gitCommonDirectory));
        if (registration is null)
        {
            string candidateManifestPath = Path.Combine(worktreePath, ProjectManifest.DefaultFileName);
            if (IsCompatibleManifest(candidateManifestPath))
            {
                return CommandResult<ResolvedContext>.Failure(new Diagnostic(
                    "project_registration_required",
                    $"This is a compatible RWL repository but it is not registered. Run `rwl project register \"{worktreePath}\"` and review the repository-owned driver before approving that trust decision."));
            }
            return CommandResult<ResolvedContext>.Failure(new Diagnostic(
                "project_not_registered",
                $"The Git repository containing '{path}' is not registered."));
        }

        string manifestPath = Path.Combine(worktreePath, registration.ManifestRelativePath);
        ProjectManifest manifest;
        try
        {
            manifest = ProjectManifest.Load(manifestPath);
        }
        catch (Exception exception)
        {
            return CommandResult<ResolvedContext>.Failure(new Diagnostic(
                "worktree_contract_unavailable",
                $"The selected worktree contract could not be loaded: {exception.Message}"));
        }

        if (!string.Equals(manifest.ProjectId, registration.ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult<ResolvedContext>.Failure(new Diagnostic(
                "project_identity_mismatch",
                $"Worktree manifest projectId '{manifest.ProjectId}' does not match registered project '{registration.ProjectId}'."));
        }

        return CommandResult<ResolvedContext>.Success(new ResolvedContext(
            registration.ProjectId,
            manifest.DisplayName,
            registration.GitCommonDirectory,
            registration.PrimaryCheckout,
            worktreePath,
            SamePath(worktreePath, registration.PrimaryCheckout),
            manifest));
    }

    private static string ResolveExistingDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath) ? Path.GetDirectoryName(fullPath)! : fullPath;
    }

    private static bool IsCompatibleManifest(string manifestPath)
    {
        try
        {
            _ = ProjectManifest.Load(manifestPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool SamePath(string left, string right) => string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase);
}
