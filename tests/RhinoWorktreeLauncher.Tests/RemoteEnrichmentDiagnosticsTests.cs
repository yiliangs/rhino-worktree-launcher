using RhinoWorktreeLauncher;

namespace RhinoWorktreeLauncher.Tests;

public sealed class RemoteEnrichmentDiagnosticsTests
{
    [Theory]
    [InlineData("https://github.com/owner/repository.git", "github.com")]
    [InlineData("git@github.com:owner/repository.git", "github.com")]
    [InlineData("ssh://git@ssh.github.com:443/owner/repository.git", "ssh.github.com")]
    [InlineData("https://gitlab.com/owner/repository.git", "gitlab.com")]
    [InlineData("git@ssh.dev.azure.com:v3/organization/project/repository", "ssh.dev.azure.com")]
    [InlineData("C:\\repositories\\repository.git", "")]
    [InlineData("/srv/git/repository.git", "")]
    [InlineData("nonsense", null)]
    public void A_remote_URL_names_the_host_behind_it(string remoteUrl, string? host)
    {
        Assert.Equal(host, GitRemoteUrl.TryGetHost(remoteUrl));
    }

    [Theory]
    [InlineData("https://github.com/owner/repository.git", true)]
    [InlineData("git@github.com:owner/repository.git", true)]
    [InlineData("https://gitlab.com/owner/repository.git", false)]
    [InlineData("https://dev.azure.com/organization/project/_git/repository", false)]
    [InlineData("C:\\repositories\\repository.git", false)]
    // A URL in a shape this does not read is not evidence that the lookup does not apply.
    [InlineData("nonsense", true)]
    public void A_pull_request_lookup_applies_only_where_a_remote_may_name_GitHub(
        string remoteUrl,
        bool applies)
    {
        Assert.Equal(applies, GitRemoteUrl.MayHostPullRequests(new[] { remoteUrl }));
    }

    [Fact]
    public void Remotes_that_could_not_be_read_leave_the_pull_request_lookup_in_place()
    {
        Assert.True(GitRemoteUrl.MayHostPullRequests(Array.Empty<string>()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(
        "Local data shown; remote divergence unavailable",
        "git_divergence_unavailable")]
    [InlineData(
        "Local data shown; remote divergence unavailable",
        "git_divergence_unavailable",
        "git_divergence_unavailable")]
    [InlineData(
        "Local data shown; pull requests unavailable",
        "github_unavailable")]
    [InlineData(
        "Local data shown; remote enrichment unavailable",
        "git_divergence_unavailable",
        "github_unavailable")]
    // A mirror that could not be fetched is divergence being unavailable, by another step.
    [InlineData(
        "Local data shown; remote divergence unavailable",
        "git_fetch_unavailable")]
    [InlineData(
        "Local data shown; remote enrichment unavailable",
        "git_fetch_unavailable",
        "github_unavailable")]
    [InlineData(
        "Local data shown; remote enrichment unavailable",
        "remote_read_not_granted")]
    public void The_refresh_hint_names_the_remote_read_that_failed(
        string expected,
        params string[] codes)
    {
        Diagnostic[] diagnostics = codes
            .Select(code => new Diagnostic(code, code, DiagnosticSeverity.Warning))
            .ToArray();

        Assert.Equal(expected, WorktreeRefreshHint.Describe(diagnostics));
    }

    [Fact]
    public void A_refresh_that_only_reports_what_it_skipped_leaves_the_hint_empty()
    {
        Diagnostic[] diagnostics =
        {
            new Diagnostic(
                "github_not_applicable",
                "Pull requests were not read because no configured Git remote names a GitHub host.",
                DiagnosticSeverity.Info)
        };

        Assert.Equal(string.Empty, WorktreeRefreshHint.Describe(diagnostics));
    }
}
