using System.Text.Json;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.GUI.Views.Development;
using Xunit;

namespace FleetMate.Tests;

public class InboxDiscoveryTests
{
    [Fact]
    public async Task PaginationContinuesBeyondTheFirstPageAndDeduplicatesThreads()
    {
        var pages = new List<int>();
        var items = await GitHubNotificationService.ReadPagesAsync(page =>
        {
            pages.Add(page);
            var ids = page == 1 ? Enumerable.Range(1, 50) : new[] { 50, 51 };
            return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(ids.Select(id => new { id = id.ToString() })));
        });
        Assert.Equal(new[] { 1, 2 }, pages);
        Assert.Equal(51, items.Count);
    }

    [Fact]
    public async Task PaginationStopsAfterFourFullPages()
    {
        var calls = 0;
        var items = await GitHubNotificationService.ReadPagesAsync(page =>
        {
            calls++;
            return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range((page - 1) * 50, 50)
                .Select(id => new { id = id.ToString() })));
        });
        Assert.Equal(4, calls);
        Assert.Equal(200, items.Count);
    }

    [Fact]
    public async Task LaterPageFailureDoesNotPretendThePartialListIsComplete()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitHubNotificationService.ReadPagesAsync(page =>
        {
            if (page == 2) throw new InvalidOperationException("Unavailable");
            return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(1, 50)
                .Select(id => new { id = id.ToString() })));
        }));
    }

    [Fact]
    public void CachedPrAuthorRequiresTheSameRepositoryAndNumber()
    {
        var notification = new GitHubNotification { Repository = "example/app", SubjectType = "PullRequest",
            SubjectApiUrl = "https://api.github.com/repos/example/app/pulls/42" };
        UnifiedPullRequest[] requests =
        [
            new() { Source = PullRequestSource.GitHub, Container = "example", Repository = "other", Number = 42, AuthorName = "Other author" },
            new() { Source = PullRequestSource.GitHub, Container = "example", Repository = "app", Number = 42, AuthorName = "Example author" }
        ];
        var row = DevelopmentNotificationRowViewModel.Create(notification, requests);
        Assert.Contains("PR author: Example author", row.Byline);
        Assert.DoesNotContain("sender", row.Byline, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("PR author unavailable", DevelopmentNotificationRowViewModel.Create(notification, []).AuthorLabel);
    }
}
