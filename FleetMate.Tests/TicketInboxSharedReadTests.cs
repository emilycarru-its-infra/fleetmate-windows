using FleetMate.Core.Services.Tickets;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Config;
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
        Assert.DoesNotContain(1, first.LoadRead(_person));
        Assert.Contains(41, second.LoadRead(_person));
        Assert.Equal(40, second.LoadRead(_person).Count);
    }

    [Fact]
    public async Task ReplacementRetriesTransientWindowsReadersWithoutRemovingTheLastGoodFile()
    {
        var store = new TicketInboxReadStore(Path.Combine(_root, "shared"));
        store.SetRead(_person, new[] { 1 }, true);
        using var held = new FileStream(store.PathFor(_person), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var write = Task.Run(() => store.SetRead(_person, new[] { 2 }, true));
        await Task.Delay(150);
        Assert.False(write.IsCompleted);
        Assert.Contains(1, store.Load(_person));
        held.Dispose();
        var result = await write.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.Equal(new[] { 1, 2 }, result.OrderBy(id => id));
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

    [Fact]
    public void OversizedMetadataIsPreservedWithoutDeserializingOrRewriting()
    {
        Directory.CreateDirectory(_root);
        var store = new TicketInboxReadStore(_root);
        using (var file = File.Create(store.PathFor(_person))) file.SetLength(TicketInboxReadStore.MaximumDocumentBytes + 1);
        Assert.Throws<InvalidDataException>(() => store.Load(_person));
        Assert.Throws<InvalidDataException>(() => store.SetRead(_person, new[] { 4 }, true));
        Assert.Equal(TicketInboxReadStore.MaximumDocumentBytes + 1, new FileInfo(store.PathFor(_person)).Length);
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
        state.ScannedThrough = DateTime.UtcNow;
        state.Items.Add(new() { FeedId = 2, Body = "EDITION_OWNED_BODY" });
        legacy.Save(_person, state);
        var original = File.ReadAllText(legacy.PathFor(_person));
        var shared = Path.Combine(_root, "shared");
        var wrong = new TicketInboxStore(legacyRoot, service, shared, new string('0', 64));
        Assert.Empty(wrong.Load(_person).ReadIds);
        var bound = new TicketInboxStore(legacyRoot, service, shared, TicketInboxReadStore.ServiceKey(service));
        bound.SetRead(_person, new[] { 1 }, false);
        var adopted = bound.Load(_person);
        Assert.Equal(new[] { 2 }, adopted.ReadIds);
        Assert.Equal(state.ScannedThrough, adopted.ScannedThrough);
        Assert.Equal("EDITION_OWNED_BODY", Assert.Single(adopted.Items).Body);
        Assert.Equal(original, File.ReadAllText(legacy.PathFor(_person)));
        Assert.Empty(Store("two").Load(_person).Items);
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

    [Theory]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    public void ManagedBadgeDefaultSupportsPolicyStrings(string value, bool expected)
    {
        var config = new FleetMateConfig();
        FleetMateConfig.ApplyRegistryValues(name => name == "TicketsBadgesEnabled" ? value : null, config, true);
        Assert.Equal(expected, config.TicketsBadgesEnabled);
    }

    [Fact]
    public void MarkAllUsesOnlyDisplayedIdsAndKeepsPeerUnreadDecisions()
    {
        var one = Store("one");
        var two = Store("two");
        one.SetRead(_person, new[] { 1, 2 }, true);
        two.SetRead(_person, new[] { 2 }, false);
        one.SetRead(_person, new[] { 3, 4 }, true);
        Assert.DoesNotContain(2, two.LoadRead(_person));
        Assert.Equal(new[] { 1, 3, 4 }, two.LoadRead(_person).OrderBy(id => id));
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
