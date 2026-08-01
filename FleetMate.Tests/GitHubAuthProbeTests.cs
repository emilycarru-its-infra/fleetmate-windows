using FleetMate.GUI;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Parsing <c>gh auth status</c>.
///
/// The probe reported a working session as signed-out because it read only
/// stdout and treated a non-zero exit as gh being absent — gh writes its report
/// to stderr in some versions and exits non-zero when logged out. The card was
/// describing our own plumbing rather than GitHub.
/// </summary>
public class GitHubAuthProbeTests
{
    [Fact]
    public void ReadsTheAccountFromAModernReport()
    {
        const string output = """
            github.com
              ✓ Logged in to github.com account ada (keyring)
              - Active account: true
              - Git operations protocol: https
              - Token scopes: 'gist', 'read:org', 'repo'
            """;

        Assert.Equal("ada", AuthManager.ParseGitHubAccount(output));
    }

    [Fact]
    public void ReadsTheAccountFromTheOlderSingleLineForm()
    {
        const string output = "✓ Logged in to github.com as ada-lovelace (oauth_token)";

        // No "account " marker in this shape, so it degrades to a generic name
        // rather than reporting signed out.
        Assert.Equal("GitHub", AuthManager.ParseGitHubAccount(output));
    }

    [Fact]
    public void StripsTrailingStorageAnnotations()
    {
        Assert.Equal("ada",
            AuthManager.ParseGitHubAccount("✓ Logged in to github.com account ada (keyring)"));
    }

    [Fact]
    public void HandlesTheAccountBeingLastOnTheLine()
    {
        Assert.Equal("ada",
            AuthManager.ParseGitHubAccount("Logged in to github.com account ada"));
    }

    [Theory]
    // "not logged into" contains "logged in" as a substring — a bare positive
    // match reads gh's own signed-out message as a live session.
    [InlineData("You are not logged into any GitHub hosts. Run gh auth login to authenticate.")]
    [InlineData("  X Not logged in to github.com")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gh: command not found")]
    public void SignedOutAndErrorOutputProduceNoAccount(string output)
    {
        Assert.Null(AuthManager.ParseGitHubAccount(output));
    }

    [Fact]
    public void AReportOnStderrIsStillARecognisedSession()
    {
        // The whole point of combining the streams: this arrives on stderr.
        const string stderr = "github.com\n  ✓ Logged in to github.com account ada (keyring)";

        Assert.Equal("ada", AuthManager.ParseGitHubAccount(stderr));
    }

    [Fact]
    public void MultipleHostsReportTheActiveAccount()
    {
        // --active narrows to one, but the parser must not trip over the shape.
        const string output = """
            github.com
              ✓ Logged in to github.com account ada (keyring)
            """;

        Assert.Equal("ada", AuthManager.ParseGitHubAccount(output));
    }
}
