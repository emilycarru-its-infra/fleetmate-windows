using FleetMate.Core.Services.Tickets;
using FleetMate.Core.Models.Tickets;
using Xunit;

namespace FleetMate.Tests;

public sealed class TicketInboxSharedReadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shared-inbox-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _person = Guid.NewGuid();
    private TicketInboxStore Store(string edition, string service = "https://tickets.example.invalid/TDWebApi/api/1/tickets") =>
        new(Path.Combine(_root, edition), service, Path.Combine(_root, "shared"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task ConcurrentEditionDeltasPreserveEveryDecisionAndExplicitUnread()
    {
        var first = Store("one");
        var second = Store("two");
        await Task.WhenAll(Enumerable.Range(1, 40).Select(id => Task.Run(() =>
            (id % 2 == 0 ? first : second).SetRead(_person, new[] { id }, true))));
        Assert.Equal(40, first.LoadRead(_person).Count);
        second.SetRead(_person, new[] { 1 }, false);
        first.SetRead(_person, new[] { 41 }, true);
        Assert.False(first.LoadRead(_person).Contains(1));
        Assert.True(second.LoadRead(_person).Contains(41));
        Assert.Equal(40, second.LoadRead(_person).Count);
    }

    [Fact]
    public void PersonServiceAndAppAreIsolatedWhileContentRemainsEditionOwned()
    {
        var first = Store("one");
        first.SetRead(_person, new[] { 7 }, true);
        first.Save(_person, new TicketInboxState { Items = new() { new() { FeedId = 7, Body = "PRIVATE_CONTENT" } } });
        Assert.Contains(7, Store("two").LoadRead(_person));
        Assert.Empty(Store("two").Load(_person).Items);
        Assert.Empty(Store("two", "https://other.example.invalid/TDWebApi/api/1/tickets").LoadRead(_person));
        Assert.Empty(Store("two", "https://tickets.example.invalid/TDWebApi/api/2/tickets").LoadRead(_person));
        Assert.Empty(first.LoadRead(Guid.NewGuid()));
        var json = File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(_root, "shared"), "*.json", SearchOption.AllDirectories)));
        Assert.DoesNotContain("PRIVATE_CONTENT", json);
        Assert.DoesNotContain("example.invalid", json);
    }

    [Fact]
    public void UnboundLegacyHistoryIsPreservedWithoutGuessingItsService()
    {
        var legacy = new TicketInboxStore(Path.Combine(_root, "one"));
        var state = new TicketInboxState { Items = new() { new() { FeedId = 7, Body = "LEGACY_BODY" } } };
        state.MarkRead(7);
        legacy.Save(_person, state);
        legacy.SaveRead(_person, state);
        Assert.Empty(Store("one").Load(_person).Items);
        Assert.Empty(Store("one").LoadRead(_person));
        Assert.Contains("LEGACY_BODY", File.ReadAllText(legacy.PathFor(_person)));
        Assert.Contains(7, legacy.LoadRead(_person));
    }

    [Theory]
    [InlineData("{\"Version\":2,\"Decisions\":{}}")]
    [InlineData("broken")]
    public void InvalidOrNewerMetadataCannotBeOverwritten(string original)
    {
        var store = new TicketInboxReadStore(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(store.PathFor(_person), original);
        Assert.ThrowsAny<Exception>(() => store.SetRead(_person, new[] { 4 }, true));
        Assert.Equal(original, File.ReadAllText(store.PathFor(_person)));
    }

    [Fact]
    public void DeclaredServiceImportsMarksOnceAndNeverOverridesExplicitUnread()
    {
        const string service = "https://tickets.example.invalid/TDWebApi/api/1/tickets";
        var legacyRoot = Path.Combine(_root, "one");
        var legacy = new TicketInboxStore(legacyRoot);
        var state = new TicketInboxState();
        state.ReadIds = new() { 1, 2 };
        legacy.SaveRead(_person, state);
        var shared = Path.Combine(_root, "shared");
        var wrong = new TicketInboxStore(legacyRoot, service, shared, new string('0', 64));
        Assert.Empty(wrong.Load(_person).ReadIds);
        var bound = new TicketInboxStore(legacyRoot, service, shared, TicketInboxReadStore.ServiceKey(service));
        bound.SetRead(_person, new[] { 1 }, false);
        Assert.Equal(new[] { 2 }, bound.Load(_person).ReadIds);
        bound.SetRead(_person, new[] { 2 }, false);
        Assert.Empty(bound.Load(_person).ReadIds);
        Assert.Equal(2, legacy.LoadRead(_person).Count);
    }

    [Fact]
    public void CanonicalServiceKeysMatchAcrossCaseDefaultPortAndTrailingSlash()
    {
        Assert.Equal(TicketInboxReadStore.ServiceKey("https://tickets.example.invalid/tdwebapi/api/1/tickets"),
            TicketInboxReadStore.ServiceKey("HTTPS://TICKETS.EXAMPLE.INVALID:443/TDWebApi/API/1/Tickets/"));
        Assert.Throws<ArgumentException>(() => TicketInboxReadStore.ServiceKey("https://user@tickets.example.invalid/api/1/tickets"));
        Assert.Throws<ArgumentException>(() => TicketInboxReadStore.ServiceKey("https://tickets.example.invalid/api/1/tickets?q=1"));
    }

    [Fact]
    public async Task PeerAtomicReplacementRaisesChangeEvent()
    {
        var first = Store("one");
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = first.WatchRead(_person, () => changed.TrySetResult());
        Store("two").SetRead(_person, new[] { 4 }, true);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(4, first.LoadRead(_person));
    }
}
