using System.Text.Json;
using FleetMate.Core.Services.Teams;
using Xunit;

namespace FleetMate.Tests;

public sealed class TeamsSavedThreadsTests : IDisposable
{
    private static readonly TeamsStorageIdentity Identity = new TeamsStorageIdentity(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly TeamsStorageIdentity OtherIdentity = new TeamsStorageIdentity(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "teams-saved-" + Guid.NewGuid().ToString("N"));
    private static TeamsSearchHit Hit(string channel = "channel-1", string id = "thread-1") => new(
        id, "team-1", "Example Team", channel, "General", "Example topic", "Useful information", "Example author",
        "2026-11-01T12:00:00Z", "2026-11-02T12:00:00Z", 1, new[] { "Example author" }, 0, "https://teams.microsoft.com/l/message/example");

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public void SavedReferencesSurviveRelaunchWithoutDuplicatingAndKeepCollections()
    {
        var store = new TeamsSavedThreads("https://search.example.org/", Identity, _directory);
        store.Save(Hit(), "Reference");
        store.Save(Hit(), "Handovers");
        store.Save(Hit("channel-2"), "Reference");
        var saved = new TeamsSavedThreads("HTTPS://SEARCH.EXAMPLE.ORG/", Identity, _directory).Load();
        Assert.Equal(2, saved.Count);
        Assert.Contains(saved, item => item.Hit.ChannelId == "channel-1" && item.Collection == "Handovers");
        Assert.Single(store.Remove(Hit()));
    }

    [Fact]
    public void AccountAndConnectionChangesNeverShowAnotherScopesSavedMetadata()
    {
        new TeamsSavedThreads("https://search.example.org", Identity, _directory).Save(Hit());
        Assert.Empty(new TeamsSavedThreads("https://search.example.org", OtherIdentity, _directory).Load());
        Assert.Empty(new TeamsSavedThreads("https://other.example.org", Identity, _directory).Load());
        Assert.Throws<ArgumentException>(() => new TeamsSavedThreads("https://search.example.org", new TeamsStorageIdentity(Guid.Empty, Guid.Empty), _directory));
    }

    [Fact]
    public void TenantAndPrincipalPartitionEvenWhenTheDisplayAccountIsTheSame()
    {
        Directory.CreateDirectory(_directory);
        var legacyScope = "https://search.example.org\nmember@example.org";
        var legacyKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(legacyScope))).ToLowerInvariant();
        File.WriteAllText(Path.Combine(_directory, legacyKey + ".json"), JsonSerializer.Serialize(new[] { new TeamsSavedThread(Hit(), DateTimeOffset.UtcNow) }));
        var store = new TeamsSavedThreads("https://search.example.org", Identity, _directory);
        Assert.Empty(store.Load());
        store.Save(Hit());
        var otherTenant = Identity with { TenantId = Guid.Parse("44444444-4444-4444-4444-444444444444") };
        Assert.Empty(new TeamsSavedThreads("https://search.example.org", otherTenant, _directory).Load());
        Assert.Empty(new TeamsSavedThreads("https://search.example.org", OtherIdentity, _directory).Load());
        var otherEndpointStore = new TeamsSavedThreads("https://other.example.org", Identity, _directory);
        otherEndpointStore.Save(Hit());
        Assert.Equal(3, Directory.GetFiles(_directory, "*.json").Length);
    }

    [Fact]
    public void SavedStateContainsReferencesOnlyAndSanitizesLegacyHitsBeforeDisplayAndRewrite()
    {
        var store = new TeamsSavedThreads("https://search.example.org", Identity, _directory);
        var hit = Hit() with { Snippet = "BODY_SHOULD_NOT_BE_STORED", AuthorDisplayName = "PRIVATE_AUTHOR", Participants = new[] { "PRIVATE_PARTICIPANT" }, Subject = new string('x', 300) };
        var saved = Assert.Single(store.Save(hit));
        Assert.Equal(160, saved.Hit.Subject!.Length);
        var path = Assert.Single(Directory.GetFiles(_directory, "*.json"));
        void AssertReferenceOnly(string text)
        {
            Assert.DoesNotContain("BODY_SHOULD_NOT_BE_STORED", text);
            Assert.DoesNotContain("PRIVATE_AUTHOR", text);
            Assert.DoesNotContain("PRIVATE_PARTICIPANT", text);
            using var json = JsonDocument.Parse(text);
            var value = json.RootElement.GetProperty("saved")[0].GetProperty("hit");
            Assert.False(value.TryGetProperty("snippet", out _));
            Assert.False(value.TryGetProperty("participants", out _));
            Assert.False(value.TryGetProperty("authorDisplayName", out _));
        }
        AssertReferenceOnly(File.ReadAllText(path));
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { new TeamsSavedThread(hit, DateTimeOffset.UtcNow) }));
        var legacy = Assert.Single(store.Load());
        Assert.Empty(legacy.Hit.Snippet);
        Assert.Empty(legacy.Hit.Participants);
        Assert.Null(legacy.Hit.AuthorDisplayName);
        store.SetFollowedChannels(new[] { "channel-1" });
        AssertReferenceOnly(File.ReadAllText(path));
        File.WriteAllText(path, "{\"version\":2,\"saved\":[],\"following\":[]}");
        Assert.Throws<InvalidDataException>(() => store.Save(Hit()));
        Assert.Contains("\"version\":2", File.ReadAllText(path));
    }

    [Fact]
    public void CorruptCollectionsAreReportedAndNeverOverwrittenBySaving()
    {
        var store = new TeamsSavedThreads("https://search.example.org", Identity, _directory);
        store.Save(Hit());
        var path = Assert.Single(Directory.GetFiles(_directory, "*.json"));
        File.WriteAllText(path, "broken data");
        Assert.ThrowsAny<JsonException>(() => store.Save(Hit("channel-2")));
        Assert.Equal("broken data", File.ReadAllText(path));
    }

    [Fact]
    public void FollowingAndBookmarksShareStateAndOlderBookmarkListsStillLoad()
    {
        var store = new TeamsSavedThreads("https://search.example.org", Identity, _directory);
        store.Save(Hit(), "Reference");
        var path = Assert.Single(Directory.GetFiles(_directory, "*.json"));
        File.WriteAllText(path, JsonSerializer.Serialize(store.Load()));
        store.SetFollowedChannels(new[] { "c1", "c2", "c1" });
        Assert.Equal(new[] { "c1", "c2" }, store.FollowedChannels());
        Assert.Equal("Reference", Assert.Single(store.Load()).Collection);
        store.Save(Hit("channel-2"));
        store.Remove(Hit());
        Assert.Equal(new[] { "c1", "c2" }, store.FollowedChannels());
        Assert.Empty(new TeamsSavedThreads("https://search.example.org", OtherIdentity, _directory).FollowedChannels());
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
