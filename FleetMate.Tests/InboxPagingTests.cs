using System.Text.Json;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.GUI.Views.Development;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The GitHub Inbox reads past its first page, keeps each thread once, and
/// labels a pull request thread with that PR's author. Synthetic values only.
/// </summary>
public class InboxPagingTests
{
    private static byte[] Page(IEnumerable<int> ids) =>
        JsonSerializer.SerializeToUtf8Bytes(ids.Select(id => new { id = id.ToString() }));

    [Fact]
    public async Task ReadingContinuesPastAFullFirstPageAndKeepsEachThreadOnce()
    {
        var pages = new List<int>();
        var threads = await GitHubNotificationService.ReadPagesAsync(page =>
        {
            pages.Add(page);
            return Task.FromResult(Page(page == 1 ? Enumerable.Range(1, 50) : new[] { 50, 51 }));
        });

        Assert.Equal(new[] { 1, 2 }, pages);
        Assert.Equal(51, threads.Count);
    }

    [Fact]
    public async Task ReadingStopsAfterFourFullPages()
    {
        var calls = 0;
        var threads = await GitHubNotificationService.ReadPagesAsync(page =>
        {
            calls++;
            return Task.FromResult(Page(Enumerable.Range((page - 1) * 50, 50)));
        });

        Assert.Equal(GitHubNotificationService.MaxPages, calls);
        Assert.Equal(200, threads.Count);
    }

    [Fact]
    public async Task AShortFirstPageIsTheWholeList()
    {
        var calls = 0;
        var threads = await GitHubNotificationService.ReadPagesAsync(_ =>
        {
            calls++;
            return Task.FromResult(Page(Enumerable.Range(1, 3)));
        });

        Assert.Equal(1, calls);
        Assert.Equal(3, threads.Count);
    }

    [Fact]
    public async Task ALaterPageFailingFailsTheRead()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitHubNotificationService.ReadPagesAsync(page =>
            page == 2 ? throw new InvalidOperationException("Unavailable") : Task.FromResult(Page(Enumerable.Range(1, 50)))));
    }

    [Fact]
    public void APullRequestThreadNamesTheAuthorOfThatRepositorysPullRequest()
    {
        var notification = new GitHubNotification
        {
            Repository = "example/app",
            SubjectType = "PullRequest",
            SubjectApiUrl = "https://api.github.com/repos/example/app/pulls/42",
        };
        UnifiedPullRequest[] pullRequests =
        {
            new() { Source = PullRequestSource.GitHub, Container = "example", Repository = "other", Number = 42, AuthorName = "Other Author" },
            new() { Source = PullRequestSource.GitHub, Container = "example", Repository = "app", Number = 42, AuthorName = "Example Author" },
        };

        var row = DevelopmentNotificationRowViewModel.Create(notification, pullRequests);

        Assert.Equal("Example Author", row.PullRequestAuthor);
        Assert.Contains("PR author: Example Author", row.Byline);
        Assert.Equal("PR author unavailable",
            DevelopmentNotificationRowViewModel.Create(notification, Array.Empty<UnifiedPullRequest>()).AuthorLabel);
    }

    [Fact]
    public void OtherThreadsCarryNoAuthorLabel()
    {
        var issue = new GitHubNotification
        {
            Repository = "example/app",
            SubjectType = "Issue",
            SubjectApiUrl = "https://api.github.com/repos/example/app/issues/42",
        };
        var row = DevelopmentNotificationRowViewModel.Create(issue, Array.Empty<UnifiedPullRequest>());

        Assert.Null(row.PullRequestAuthor);
        Assert.DoesNotContain("PR author", row.Byline);
    }
}
