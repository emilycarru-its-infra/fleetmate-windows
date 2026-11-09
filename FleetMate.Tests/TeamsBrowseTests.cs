using FleetMate.Core.Services.Teams;
using Xunit;

namespace FleetMate.Tests;

public class TeamsBrowseTests
{
    private static TeamsSearchHit Hit(string id) => new(id, "team", "Example Team", "channel", "General", null, "Example", null,
        "2026-11-01T12:00:00Z", "2026-11-02T12:00:00Z", 0, Array.Empty<string>(), 0, null);

    [Fact]
    public void BrowseRequestIsScopedPagedAndDoesNotSendAWildcardQuery()
    {
        using var service = new TeamsSearchService("https://search.example.org", "api://example");
        var uri = service.BuildBrowseUri(new TeamsBrowseQuery(Channel: "19:a@thread", Author: "Example Person", Since: "2026-11-01", Limit: 50, Offset: 100));
        Assert.Equal("https://search.example.org/v1/teams/threads?channel=19%3Aa%40thread&author=Example%20Person&since=2026-11-01&limit=50&offset=100", uri.OriginalString);
        Assert.Throws<TeamsSearchException>(() => service.BuildBrowseUri(new TeamsBrowseQuery()));
        Assert.Throws<TeamsSearchException>(() => service.BuildBrowseUri(new TeamsBrowseQuery(Team: "t", Offset: -1)));
        Assert.Throws<TeamsSearchException>(() => service.BuildBrowseUri(new TeamsBrowseQuery(Team: "t", Limit: 101)));
        Assert.Throws<TeamsSearchException>(() => service.BuildBrowseUri(new TeamsBrowseQuery(Team: "t", Since: "yesterday")));
    }

    [Fact]
    public void AppendingPagesPreservesOrderAndRejectsAMixedIndexSnapshot()
    {
        var state = new TeamsBrowseState();
        Assert.True(state.Apply(new TeamsBrowseResponse(null, "v1", 3, new[] { Hit("3"), Hit("2") }, 2), false));
        Assert.False(state.Apply(new TeamsBrowseResponse(null, "v2", 4, new[] { Hit("4") }, null), true));
        Assert.Equal(new[] { "3", "2" }, state.Results.Select(hit => hit.ThreadId));
        Assert.Equal(2, state.NextOffset);
        Assert.True(state.Apply(new TeamsBrowseResponse(null, "v1", 3, new[] { Hit("2"), Hit("1") }, null), true));
        Assert.Equal(new[] { "3", "2", "1" }, state.Results.Select(hit => hit.ThreadId));
        Assert.Null(state.NextOffset);
        Assert.True(state.Apply(new TeamsBrowseResponse(null, "v2", 4, new[] { Hit("4") }, 1), false));
        Assert.Equal("4", Assert.Single(state.Results).ThreadId);
    }
}
