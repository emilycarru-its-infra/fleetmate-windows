using System.Text.Json;
using FleetMate.Core.Services.Teams;
using Xunit;

namespace FleetMate.Tests;

public sealed class TeamsSavedThreadsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "teams-saved-" + Guid.NewGuid().ToString("N"));
    private static TeamsSearchHit Hit(string channel = "channel-1", string id = "thread-1") => new(
        id, "team-1", "Example Team", channel, "General", "Example topic", "Useful information", "Example author",
        "2026-11-01T12:00:00Z", "2026-11-02T12:00:00Z", 1, new[] { "Example author" }, 0, "https://teams.microsoft.com/l/message/example");

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public void SavedReferencesSurviveRelaunchWithoutDuplicatingAndKeepCollections()
    {
        var store = new TeamsSavedThreads("https://search.example.org/", "member@example.org", _directory);
        store.Save(Hit(), "Reference");
        store.Save(Hit(), "Handovers");
        store.Save(Hit("channel-2"), "Reference");
        var saved = new TeamsSavedThreads("HTTPS://SEARCH.EXAMPLE.ORG/", "MEMBER@example.org", _directory).Load();
        Assert.Equal(2, saved.Count);
        Assert.Contains(saved, item => item.Hit.ChannelId == "channel-1" && item.Collection == "Handovers");
        Assert.Single(store.Remove(Hit()));
    }

    [Fact]
    public void AccountAndConnectionChangesNeverShowAnotherScopesSavedMetadata()
    {
        new TeamsSavedThreads("https://search.example.org", "first@example.org", _directory).Save(Hit());
        Assert.Empty(new TeamsSavedThreads("https://search.example.org", "second@example.org", _directory).Load());
        Assert.Empty(new TeamsSavedThreads("https://other.example.org", "first@example.org", _directory).Load());
        Assert.Throws<ArgumentException>(() => new TeamsSavedThreads("https://search.example.org", "", _directory));
    }

    [Fact]
    public void CorruptCollectionsAreReportedAndNeverOverwrittenBySaving()
    {
        var store = new TeamsSavedThreads("https://search.example.org", "member@example.org", _directory);
        store.Save(Hit());
        var path = Assert.Single(Directory.GetFiles(_directory, "*.json"));
        File.WriteAllText(path, "broken data");
        Assert.ThrowsAny<JsonException>(() => store.Save(Hit("channel-2")));
        Assert.Equal("broken data", File.ReadAllText(path));
    }

    [Fact]
    public void FollowingAndBookmarksShareStateAndOlderBookmarkListsStillLoad()
    {
        var store = new TeamsSavedThreads("https://search.example.org", "member@example.org", _directory);
        store.Save(Hit(), "Reference");
        var path = Assert.Single(Directory.GetFiles(_directory, "*.json"));
        File.WriteAllText(path, JsonSerializer.Serialize(store.Load()));
        store.SetFollowedChannels(new[] { "c1", "c2", "c1" });
        Assert.Equal(new[] { "c1", "c2" }, store.FollowedChannels());
        Assert.Equal("Reference", Assert.Single(store.Load()).Collection);
        store.Save(Hit("channel-2"));
        store.Remove(Hit());
        Assert.Equal(new[] { "c1", "c2" }, store.FollowedChannels());
        Assert.Empty(new TeamsSavedThreads("https://search.example.org", "other@example.org", _directory).FollowedChannels());
        Assert.Throws<ArgumentException>(() => store.SetFollowedChannels(Enumerable.Range(0, 501).Select(index => "c" + index)));
        Assert.Equal(new[] { "c1", "c2" }, store.FollowedChannels());
    }

    [Fact]
    public void CopyContainsMessageContextAndSuppressesUnsafeStructuredLinks()
    {
        var root = new TeamsMessage("root", null, "Example author", "2026-11-01T12:00:00Z", null, "Use this information.",
            new[] { new TeamsAttachment("document", "javascript:alert(1)") }, null);
        var thread = new TeamsThread("root", "team", "Example Team", "channel", "General", "Example topic", "https://teams.microsoft.com/l/message/example", new[] { root });
        var text = TeamsThreadCopy.Thread(thread, false, TeamsLinkPolicy.DefaultHosts);
        Assert.Contains("Example author", text);
        Assert.Contains("Use this information.", text);
        Assert.Contains("Source: https://teams.microsoft.com/", text);
        Assert.DoesNotContain("javascript:", text);
        Assert.StartsWith("# Example topic", TeamsThreadCopy.Thread(thread, true, TeamsLinkPolicy.DefaultHosts));
    }

    [Fact]
    public void ChannelFilterMatchesWordsAcrossTeamAndChannelNames()
    {
        var channels = new[]
        {
            new TeamsChannel("t1", "Example Support", "c1", "General", "standard", 1, null),
            new TeamsChannel("t2", "Example Projects", "c2", "General", "standard", 2, null),
        };
        Assert.Equal("c1", Assert.Single(TeamsChannelScope.Filter(channels, "SUPPORT general")).ChannelId);
        Assert.Empty(TeamsChannelScope.Filter(channels, "nonexistent"));
        Assert.Equal(2, TeamsChannelScope.Filter(channels, "").Count);
    }
}
