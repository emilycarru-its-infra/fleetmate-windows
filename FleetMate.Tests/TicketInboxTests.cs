using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The ticket Inbox: which feed entries belong in it, and how a pass scans,
/// paces, saves and resumes. Every fixture is synthetic.
/// </summary>
public sealed class TicketInboxTests : IDisposable
{
    private static readonly Guid Me = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
    private static readonly Guid Other = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly TicketInboxPerson Person = new(Me, "Pat Example", "pat@example.invalid");
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fm-inbox-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static TdxFeedEntry Entry(int id, string? notified, Guid? by = null, DateTime? created = null) => new()
    {
        Id = id,
        Body = $"<p>Body {id}</p>",
        CreatedUid = by ?? Other,
        CreatedFullName = "Someone",
        CreatedDate = created ?? Now.AddHours(-1),
        NotifiedList = notified,
    };

    private static TdxTicket Ticket(int id, DateTime modified) => new() { Id = id, Title = $"Ticket {id}", ModifiedDate = modified };

    [Theory]
    [InlineData("A B <a@example.invalid>, Pat Example <pat@example.invalid>", true)]
    [InlineData("A B, pat example", true)]
    [InlineData("A B; Pat Example", true)]
    [InlineData("Someone <PAT@EXAMPLE.INVALID>", true)]
    [InlineData("Pat Examples", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void MatchesTheFullNameOrTheEmail(string? list, bool expected)
    {
        Assert.Equal(expected, TicketInboxMatch.NamesPerson(list, Person));
    }

    [Fact]
    public void SelectKeepsNotifiedEntriesFromOthersInsideTheWindow()
    {
        var cutoff = Now - TicketInboxMatch.Window;
        var items = TicketInboxMatch.Select(Ticket(42, Now), new[]
        {
            Entry(1, "Pat Example <pat@example.invalid>"),
            Entry(2, "Someone Else"),
            Entry(3, "Pat Example", by: Me),
            Entry(4, "Pat Example", created: cutoff.AddDays(-1)),
        }, Person, cutoff);

        var item = Assert.Single(items);
        Assert.Equal(1, item.FeedId);
        Assert.Equal(42, item.TicketId);
        Assert.Equal("Body 1", item.Body);
    }

    [Fact]
    public void StateRoundTripsAndPrunesReadMarksForItemsThatAgedOut()
    {
        var store = new TicketInboxStore(_dir);
        var state = new TicketInboxState();
        state.ReplaceTicket(42, new[] { new TicketInboxItem { FeedId = 1, TicketId = 42, Created = Now } });
        state.MarkRead(1);
        state.TicketModified[42] = Now;
        state.ScannedThrough = Now;
        store.Save(Me, state);
        store.SaveRead(Me, state);

        var loaded = store.Load(Me);
        Assert.True(loaded.IsRead(1));
        Assert.Equal(0, loaded.UnreadCount);
        Assert.Equal(Now, loaded.ScannedThrough);
        Assert.Empty(store.Load(Other).Items);

        loaded.Prune(Now.AddDays(1));
        Assert.Empty(loaded.Items);
        Assert.Empty(loaded.ReadIds);
        Assert.Empty(loaded.TicketModified);
    }

    private sealed class FakeApi : ITicketInboxApi
    {
        public List<TdxTicket> Tickets { get; set; } = new();
        public Dictionary<int, List<TdxFeedEntry>> Feeds { get; } = new();
        public List<TicketSearchRequest> Searches { get; } = new();
        public List<int> FeedReads { get; } = new();
        public int? CancelAfterFeeds { get; set; }
        public CancellationTokenSource? Cancel { get; set; }

        public Task<List<TdxTicket>> SearchTicketsOrThrowAsync(TicketSearchRequest search, CancellationToken ct = default)
        {
            Searches.Add(search);
            return Task.FromResult(Tickets.ToList());
        }

        public Task<List<TdxFeedEntry>> GetTicketFeedOrThrowAsync(int ticketId, CancellationToken ct = default)
        {
            FeedReads.Add(ticketId);
            if (CancelAfterFeeds is { } n && FeedReads.Count >= n) Cancel?.Cancel();
            return Feeds.TryGetValue(ticketId, out var feed)
                ? Task.FromResult(feed)
                : throw new TdxRequestException("read the ticket feed", "404");
        }
    }

    [Fact]
    public async Task FirstPassSearchesNinetyDaysNewestFirstPacedAndSkipsUnchangedTicketsNextTime()
    {
        var api = new FakeApi
        {
            Tickets = { Ticket(1, Now.AddDays(-3)), Ticket(2, Now.AddHours(-2)) },
        };
        api.Feeds[1] = new() { Entry(10, "Pat Example", created: Now.AddDays(-3)) };
        api.Feeds[2] = new() { Entry(20, "Someone Else") };
        var delays = new List<TimeSpan>();
        var store = new TicketInboxStore(_dir);
        var scanner = new TicketInboxScanner(api, store, () => Now, (d, _) => { delays.Add(d); return Task.CompletedTask; });
        var state = new TicketInboxState();
        var progress = new List<TicketInboxProgress>();

        await scanner.RunPassAsync(Person, state, new SyncProgress(progress.Add));

        Assert.Equal(Now - TimeSpan.FromDays(90), api.Searches[0].ModifiedDateFrom);
        Assert.Null(api.Searches[0].ResponsibleGroupIds);
        Assert.Equal(new[] { 2, 1 }, api.FeedReads);
        Assert.All(delays, d => Assert.Equal(TimeSpan.FromSeconds(1.2), d));
        Assert.Equal(2, delays.Count);
        Assert.Equal(new[] { 10 }, state.Items.Select(i => i.FeedId));
        Assert.Equal(Now, state.ScannedThrough);
        Assert.Equal(new TicketInboxProgress(2, 2), progress[^1]);
        Assert.Equal(new[] { 10 }, store.Load(Me).Items.Select(i => i.FeedId));

        // Next pass: ten minutes of overlap, and only the ticket that changed is read again.
        api.FeedReads.Clear();
        api.Tickets = new() { Ticket(1, Now.AddDays(-3)), Ticket(2, Now.AddMinutes(30)) };
        var later = new TicketInboxScanner(api, store, () => Now.AddHours(1), (_, _) => Task.CompletedTask);
        await later.RunPassAsync(Person, state);

        Assert.Equal(Now - TimeSpan.FromMinutes(10), api.Searches[1].ModifiedDateFrom);
        Assert.Equal(new[] { 2 }, api.FeedReads);
    }

    [Fact]
    public async Task AQuitMidPassResumesWithoutRereadingFinishedTickets()
    {
        var api = new FakeApi { Tickets = { Ticket(1, Now.AddHours(-1)), Ticket(2, Now.AddHours(-2)), Ticket(3, Now.AddHours(-3)) } };
        foreach (var id in new[] { 1, 2, 3 }) api.Feeds[id] = new() { Entry(id * 10, "Pat Example") };
        var store = new TicketInboxStore(_dir);
        using var cts = new CancellationTokenSource();
        api.Cancel = cts;
        api.CancelAfterFeeds = 1;
        var scanner = new TicketInboxScanner(api, store, () => Now, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.RunPassAsync(Person, new TicketInboxState(), ct: cts.Token));

        var saved = store.Load(Me);
        Assert.Null(saved.ScannedThrough);
        Assert.Equal(new[] { 10 }, saved.Items.Select(i => i.FeedId));

        api.FeedReads.Clear();
        api.CancelAfterFeeds = null;
        await new TicketInboxScanner(api, store, () => Now, (_, _) => Task.CompletedTask).RunPassAsync(Person, saved);

        Assert.Equal(new[] { 2, 3 }, api.FeedReads);
        Assert.Equal(new[] { 10, 20, 30 }, saved.Items.Select(i => i.FeedId).OrderBy(i => i));
    }

    [Fact]
    public async Task AFeedThatFailsIsRetriedAndHoldsTheScanPoint()
    {
        var api = new FakeApi { Tickets = { Ticket(1, Now.AddHours(-1)) } };
        var store = new TicketInboxStore(_dir);
        var state = new TicketInboxState();

        await new TicketInboxScanner(api, store, () => Now, (_, _) => Task.CompletedTask).RunPassAsync(Person, state);

        Assert.Null(state.ScannedThrough);
        Assert.False(state.TicketModified.ContainsKey(1));
    }

    [Fact]
    public void ReadMarksLiveInTheirOwnFileThatAScannerSaveNeverTouches()
    {
        var store = new TicketInboxStore(_dir);
        var window = new TicketInboxState();
        window.ReplaceTicket(42, new[] { new TicketInboxItem { FeedId = 1, TicketId = 42, Created = Now } });
        window.MarkRead(1);
        store.SaveRead(Me, window);

        // The helper saves its own copy, which knows nothing of the window's marks.
        var helper = new TicketInboxState();
        helper.ReplaceTicket(42, new[] { new TicketInboxItem { FeedId = 1, TicketId = 42, Created = Now } });
        store.Save(Me, helper);

        Assert.True(store.Load(Me).IsRead(1));
        Assert.DoesNotContain("readIds", File.ReadAllText(store.PathFor(Me)));
    }

    [Fact]
    public async Task OnlyOneProcessScansAPersonAtATime()
    {
        var api = new FakeApi { Tickets = { Ticket(1, Now.AddHours(-1)) } };
        api.Feeds[1] = new() { Entry(10, "Pat Example") };
        var store = new TicketInboxStore(_dir);
        var scanner = new TicketInboxScanner(api, store, () => Now, (_, _) => Task.CompletedTask);

        using (var held = store.TryLock(Me))
        {
            Assert.NotNull(held);
            Assert.Null(store.TryLock(Me));
            Assert.False(await scanner.TryRunPassAsync(Person, new TicketInboxState()));
            Assert.Empty(api.Searches);
        }

        Assert.True(await scanner.TryRunPassAsync(Person, new TicketInboxState()));
        Assert.Single(api.Searches);
        using var again = store.TryLock(Me);
        Assert.NotNull(again);
    }

    private sealed class SyncProgress(Action<TicketInboxProgress> report) : IProgress<TicketInboxProgress>
    {
        public void Report(TicketInboxProgress value) => report(value);
    }
}
