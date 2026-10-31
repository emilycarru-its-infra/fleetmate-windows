using System.Text.Json;
using FleetMate.Core.Models.Tickets;
using FleetMate.GUI.Views.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Launch state: the ticket snapshot shown before sign-in, the sign-in step
/// progress over an empty list, and the Activity bar's Add Comment label.
/// Synthetic values only.
/// </summary>
public sealed class TicketLaunchStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fm-snapshot-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void SnapshotRoundTripsTicketFields()
    {
        var tickets = JsonSerializer.Deserialize<List<TdxTicket>>("""
            [{"ID": 42, "Title": "Laptop", "StatusName": "Open", "ResponsibleGroupName": "Sample Group",
              "ModifiedDate": "2026-10-30T11:00:00Z"}]
            """)!;
        var snapshot = new TicketSnapshot(_dir);

        snapshot.Save(tickets);
        var back = snapshot.Load();

        var ticket = Assert.Single(back);
        Assert.Equal(42, ticket.Id);
        Assert.Equal("Laptop", ticket.Title);
        Assert.Equal("Open", ticket.StatusName);
        Assert.Equal("Sample Group", ticket.ResponsibleGroupName);
        Assert.Equal(tickets[0].ModifiedDate, ticket.ModifiedDate);
        Assert.Equal(Path.Combine(_dir, "snapshot.json"), snapshot.FilePath);
    }

    [Fact]
    public async Task SnapshotSavesInTheBackgroundFromACopy()
    {
        var snapshot = new TicketSnapshot(_dir);
        var tickets = new List<TdxTicket> { new() { Id = 1, Title = "One" } };

        var save = snapshot.SaveInBackground(tickets);
        tickets.Add(new TdxTicket { Id = 2 });
        await save;

        Assert.Equal(new[] { 1 }, snapshot.Load().Select(t => t.Id));
    }

    [Fact]
    public void AMissingOrDamagedSnapshotLoadsEmpty()
    {
        var snapshot = new TicketSnapshot(_dir);
        Assert.Empty(snapshot.Load());

        Directory.CreateDirectory(_dir);
        File.WriteAllText(snapshot.FilePath, "{not json");
        Assert.Empty(snapshot.Load());
    }

    [Fact]
    public void SignInStepsFillTheBarInOrderWithTheirLabels()
    {
        var steps = Enum.GetValues<TdxSignInStep>();
        Assert.Equal(new[]
        {
            "Finding your account",
            "Connecting to Microsoft",
            "Signing in to TeamDynamix",
            "Finishing sign-in",
            "Loading tickets",
        }, steps.Select(s => s.Label()));

        var fractions = steps.Select(s => s.Fraction()).ToList();
        Assert.Equal(fractions.OrderBy(f => f), fractions);
        Assert.All(fractions, f => Assert.InRange(f, 0.01, 0.99));
    }

    [Theory]
    [InlineData(false, null, 0, false)]
    [InlineData(true, null, 0, true)]
    [InlineData(false, TdxSignInStep.ConnectingToMicrosoft, 0, true)]
    [InlineData(false, TdxSignInStep.LoadingTickets, 0, true)]
    [InlineData(true, TdxSignInStep.FindingAccount, 12, false)]
    public void ProgressShowsOnlyOverAnEmptyList(bool loading, TdxSignInStep? step, int count, bool shown)
    {
        Assert.Equal(shown, TicketsPageSync.ShowLoading(loading, step, count));
    }

    [Theory]
    [InlineData(false, "", "Add Comment")]
    [InlineData(false, "   ", "Add Comment")]
    [InlineData(false, "Half a thought", "Add Comment (Draft)")]
    [InlineData(true, "Half a thought", "Add Comment")]
    public void AddCommentSaysDraftOnlyWhenClosedOverUnsentText(bool open, string draft, string expected)
    {
        Assert.Equal(expected, TicketsPageSync.AddCommentLabel(open, draft));
    }
}
