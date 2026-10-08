using System.Text.Json;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using FleetMate.GUI.Views.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// TDX's full update replaces the ticket: a field missing from the body is
/// cleared. Setting a parent must echo everything else back unchanged.
/// </summary>
public class TdxTicketParityTests
{
    private static JsonElement Ticket(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void FullUpdateEchoesTheTicketAndOverridesOnlyTheParent()
    {
        var ticket = Ticket("""
            {"ID": 10, "TypeID": 3, "Classification": 46, "Title": "Projector", "Description": "<p>x</p>",
             "StatusID": 5, "PriorityID": 7, "ResponsibleUid": "8b1d6f0e-0000-0000-0000-000000000001",
             "ParentID": 0, "StatusName": "Open"}
            """);

        var body = TdxFullUpdate.Build(ticket, new Dictionary<string, object?> { ["ParentID"] = 42 });

        Assert.Equal(42, body["ParentID"]);
        Assert.Equal("Projector", ((JsonElement)body["Title"]!).GetString());
        Assert.Equal(5, ((JsonElement)body["StatusID"]!).GetInt32());
        Assert.Equal(true, body["IsRichHtml"]);
        Assert.False(body.ContainsKey("StatusName"));
        Assert.False(body.ContainsKey("ID"));
    }

    [Fact]
    public void FullUpdateCanClearTheParent()
    {
        var body = TdxFullUpdate.Build(Ticket("""{"TypeID": 1, "Title": "t", "ParentID": 9}"""),
            new Dictionary<string, object?> { ["ParentID"] = null });

        Assert.True(body.ContainsKey("ParentID"));
        Assert.Null(body["ParentID"]);
    }

    [Fact]
    public void FullUpdateKeepsExplicitNulls()
    {
        var body = TdxFullUpdate.Build(Ticket("""{"TypeID": 1, "Title": "t", "ResponsibleUid": null}"""));

        Assert.True(body.ContainsKey("ResponsibleUid"));
        Assert.Null(body["ResponsibleUid"]);
    }

    [Fact]
    public void ClassificationNamesMatchMac()
    {
        Assert.Equal("Service Request", TdxClassification.Name(46));
        Assert.Equal("Major Incident", TdxClassification.Name(77));
        Assert.Equal(7, TdxClassification.All.Count);
    }

    [Fact]
    public void CreateRequestOmitsUnsetClassificationAndRichHtml()
    {
        var json = JsonSerializer.Serialize(new CreateTicketRequest { TypeId = 1, Title = "t" });

        Assert.DoesNotContain("Classification", json);
        Assert.DoesNotContain("IsRichHtml", json);
    }

    [Fact]
    public void DescriptionBecomesEncodedParagraphs()
    {
        Assert.Equal("<p>a &lt;b&gt;</p><p>&nbsp;</p><p>c</p>", CreateTicketDialog.HtmlParagraphs("a <b>\r\n\nc"));
    }
}
