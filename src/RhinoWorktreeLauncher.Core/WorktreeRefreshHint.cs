namespace RhinoWorktreeLauncher;

/// <summary>
/// The one sentence a surface shows after a worktree refresh that lost something. Divergence
/// and pull-request state are two independent remote reads, so the hint names the read that
/// failed instead of reporting the whole remote as unavailable on the strength of either one.
/// Divergence fails at either of two steps, fetching the mirror or counting against it, and
/// both are the one read being unavailable. The strings live here, once, because the
/// diagnostic codes they answer to live in Core.
/// </summary>
public static class WorktreeRefreshHint
{
    private const string Divergence = "Local data shown; remote divergence unavailable";
    private const string PullRequests = "Local data shown; pull requests unavailable";
    private const string Remote = "Local data shown; remote enrichment unavailable";

    /// <summary>
    /// The hint for one refresh, or an empty string when the refresh degraded nothing. Only
    /// warnings and worse are read: a diagnostic that records what a refresh skipped is not a
    /// degradation and has nothing to say in a one-line status surface.
    /// </summary>
    public static string Describe(IReadOnlyList<Diagnostic> diagnostics)
    {
        bool divergence = false;
        bool pullRequests = false;
        bool other = false;
        foreach (Diagnostic diagnostic in diagnostics)
        {
            if (diagnostic.Severity < DiagnosticSeverity.Warning)
                continue;
            switch (diagnostic.Code)
            {
                // A mirror that could not be fetched and a worktree whose counts could not be
                // read are the same loss reported at two steps of the one divergence read.
                case "git_fetch_unavailable":
                case "git_divergence_unavailable":
                    divergence = true;
                    break;
                case "github_unavailable":
                    pullRequests = true;
                    break;
                default:
                    other = true;
                    break;
            }
        }

        if (other || divergence && pullRequests)
            return Remote;
        if (divergence)
            return Divergence;
        return pullRequests ? PullRequests : string.Empty;
    }
}
