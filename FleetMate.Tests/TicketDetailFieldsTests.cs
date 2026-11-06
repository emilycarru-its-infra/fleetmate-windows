using System.Text.Json;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The detail pane edits a ticket one field at a time, as FleetMate for Mac
/// does: each pick is a sparse PATCH of that field alone, sent only when the
/// value really changes.
/// </summary>
public class TicketDetailFieldsTests
{
    private static readonly Guid FirstPerson = new("8b1d6f0e-0000-0000-0000-000000000001");
    private static readonly Guid SecondPerson = new("8b1d6f0e-0000-0000-0000-000000000002");

    private static TdxTicket Ticket() => new()
    {
        Id = 12,
        StatusId = 5,
        PriorityId = 7,
        Classification = 46,
        FormId = 3,
        ServiceId = 9,
        TypeId = 2,
        ResponsibleGroupId = 4,
        RequestorUid = FirstPerson,
    };

    [Theory]
    [InlineData(TicketDetailFields.Status, 6)]
    [InlineData(TicketDetailFields.Priority, 8)]
    [InlineData(TicketDetailFields.Classification, 47)]
    [InlineData(TicketDetailFields.Form, 31)]
    [InlineData(TicketDetailFields.Service, 90)]
    [InlineData(TicketDetailFields.Type, 20)]
    [InlineData(TicketDetailFields.Group, 40)]
    public void ANewValueIsAPatchOfThatFieldAlone(string field, int value)
    {
        var update = TicketDetailFields.Change(Ticket(), field, value);

        Assert.NotNull(update);
        Assert.Equal(value, Assert.Single(update!, kv => kv.Key == field).Value);
        Assert.Single(update!);
    }

    [Theory]
    [InlineData(TicketDetailFields.Status, 5)]
    [InlineData(TicketDetailFields.Priority, 7)]
    [InlineData(TicketDetailFields.Classification, 46)]
    [InlineData(TicketDetailFields.Form, 3)]
    [InlineData(TicketDetailFields.Service, 9)]
    [InlineData(TicketDetailFields.Type, 2)]
    [InlineData(TicketDetailFields.Group, 4)]
    public void TheCurrentValueSendsNothing(string field, int value)
    {
        Assert.Null(TicketDetailFields.Change(Ticket(), field, value));
    }

    [Fact]
    public void AFieldTheTicketLacksIsSetWhenPicked()
    {
        var ticket = Ticket();
        ticket.ServiceId = null;

        var update = TicketDetailFields.Change(ticket, TicketDetailFields.Service, 9);

        Assert.Equal(9, update![TicketDetailFields.Service]);
    }

    [Fact]
    public void ABlankSelectionIsNeverSent()
    {
        Assert.Null(TicketDetailFields.Change(Ticket(), TicketDetailFields.Form, 0));
        Assert.Null(TicketDetailFields.Change(Ticket(), TicketDetailFields.Requestor, Guid.Empty));
        Assert.Null(TicketDetailFields.Change(Ticket(), TicketDetailFields.Group, null));
    }

    [Fact]
    public void TheRequestorIsChangedByUid()
    {
        Assert.Null(TicketDetailFields.Change(Ticket(), TicketDetailFields.Requestor, FirstPerson));

        var update = TicketDetailFields.Change(Ticket(), TicketDetailFields.Requestor, SecondPerson);

        Assert.Equal(SecondPerson, update!["RequestorUid"]);
    }

    [Fact]
    public void WithoutALoadedTicketTheChangeIsSent()
    {
        Assert.NotNull(TicketDetailFields.Change(null, TicketDetailFields.Status, 5));
    }

    [Fact]
    public void OnlyTheDetailFieldsCanBeChanged()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TicketDetailFields.Change(Ticket(), "Title", "x"));
    }

    [Fact]
    public void TheFieldChangeBecomesOneReplaceOperation()
    {
        var update = TicketDetailFields.Change(Ticket(), TicketDetailFields.Form, 31)!;

        var json = JsonSerializer.Serialize(TdxService.ToJsonPatch(update));

        Assert.Equal("""[{"op":"replace","path":"/FormID","value":31}]""", json);
    }

    [Fact]
    public void ADescriptionIsSentAsParagraphsOfEscapedHtml()
    {
        var update = TicketDetailFields.DescriptionChange("Old", "Screen <flickers>\r\nAfter R&D update");

        Assert.NotNull(update);
        Assert.Equal("<p>Screen &lt;flickers&gt;</p><p>After R&amp;D update</p>", update![TicketDetailFields.Description]);
        Assert.Equal(true, update["IsRichHtml"]);
    }

    [Fact]
    public void AccentedLettersStayAsTheyAre()
    {
        Assert.Equal("<p>Café</p>", TicketDetailFields.DescriptionHtml("Café"));
    }

    [Fact]
    public void AnUnchangedDescriptionSendsNothing()
    {
        Assert.Null(TicketDetailFields.DescriptionChange("Line one\nLine two", "  Line one\r\nLine two\n"));
    }

    [Fact]
    public void ADescriptionCanBeCleared()
    {
        var update = TicketDetailFields.DescriptionChange("Something", "   ");

        Assert.Equal("", update![TicketDetailFields.Description]);
    }

    [Fact]
    public void ChoicesKeepTheTicketsOwnValueAndSortByName()
    {
        var options = new Dictionary<int, string> { [1] = "Software", [2] = "Hardware" };

        var choices = TicketDetailFields.Choices(options, 7, "Accounts", "Service");

        Assert.Equal(new[] { "Accounts", "Hardware", "Software" }, choices.Select(c => c.Value));
        Assert.Equal(7, choices[0].Key);
    }

    [Fact]
    public void AnUnnamedCurrentValueGetsAFallbackLabel()
    {
        var choices = TicketDetailFields.Choices(new Dictionary<int, string>(), 7, null, "Form");

        Assert.Equal("Form 7", Assert.Single(choices).Value);
    }

    [Fact]
    public void NoCurrentValueAddsNoChoice()
    {
        Assert.Empty(TicketDetailFields.Choices(new Dictionary<int, string>(), null, null, "Form"));
        Assert.Empty(TicketDetailFields.Choices(new Dictionary<int, string>(), 0, "None", "Form"));
    }
}
