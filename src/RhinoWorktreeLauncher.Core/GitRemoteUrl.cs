namespace RhinoWorktreeLauncher;

/// <summary>
/// What a configured Git remote URL says about the service behind it. Divergence is read from
/// an RWL mirror of any remote, but a pull-request lookup only applies to GitHub, so a scan
/// asks this before spending a GitHub CLI invocation on a remote that cannot answer it.
/// </summary>
internal static class GitRemoteUrl
{
    private static readonly char[] PathSeparators = { '/', '\\' };

    /// <summary>
    /// Whether a pull-request lookup can apply to any of these remote URLs. It can when one of
    /// them names a GitHub host, and it can also when a URL names nothing this can read, since
    /// an unreadable URL is not evidence that the lookup does not apply. An empty list carries
    /// the same unknown: the configured remotes could not be read.
    /// </summary>
    public static bool MayHostPullRequests(IReadOnlyList<string> remoteUrls) =>
        remoteUrls.Count == 0 || remoteUrls.Any(MayHostPullRequests);

    /// <summary>
    /// The host a Git remote URL names. An empty string means the URL names a local or network
    /// path, which has no service behind it. Null means the URL is in a shape this does not
    /// read, so its host is unknown rather than absent.
    /// </summary>
    public static string? TryGetHost(string remoteUrl)
    {
        string trimmed = remoteUrl.Trim();
        if (trimmed.Length == 0)
            return null;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? absolute))
            return absolute.Host;

        // The scp-like shape git accepts, [user@]host:path, is not a URI.
        int colon = trimmed.IndexOf(':');
        int separator = trimmed.IndexOfAny(PathSeparators);
        if (colon > 0 && (separator < 0 || colon < separator))
        {
            string authority = trimmed[..colon];
            return authority[(authority.LastIndexOf('@') + 1)..];
        }

        return separator < 0 ? null : string.Empty;
    }

    private static bool MayHostPullRequests(string remoteUrl)
    {
        string? host = TryGetHost(remoteUrl);
        return host is null || IsGitHubHost(host);
    }

    private static bool IsGitHubHost(string host) =>
        string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase);
}
