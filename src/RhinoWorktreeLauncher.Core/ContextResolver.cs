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
            PathIdentity.AreEquivalent(candidate.GitCommonDirectory, gitCommonDirectory));
        if (registration is null)
        {
            return CommandResult<ResolvedContext>.Failure(new Diagnostic(
                "project_registration_required",
                $"The Git repository containing '{path}' is not registered. Add it in RWL or run `rwl project register \"{worktreePath}\"`."));
        }

        return CommandResult<ResolvedContext>.Success(new ResolvedContext(
            registration.ProjectId,
            registration.DisplayName,
            registration.GitCommonDirectory,
            registration.PrimaryCheckout,
            worktreePath,
            PathIdentity.AreEquivalent(worktreePath, registration.PrimaryCheckout),
            registration.RhinoVersion,
            registration.BuildProfile));
    }

    private static string ResolveExistingDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath) ? Path.GetDirectoryName(fullPath)! : fullPath;
    }
}
