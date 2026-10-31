using System.Net;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using FleetMate.GUI.Views.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Ticket people: who a comment can notify, the contact routes, and the feed's
/// "Notified:" line. Every fixture is synthetic, and nothing here reaches a server.
/// </summary>
public class TicketPeopleTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            return _respond(request);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static TdxService Service(StubHandler handler)
    {
        var service = new TdxService(new TdxConfig { BaseUrl = "https://service.example.com", AppId = 7 }, handler);
        service.SetSsoToken("synthetic-token", DateTime.UtcNow.AddHours(1), "operator@example.com", "Operator");
        return service;
    }

    private static readonly Guid PersonUid = Guid.Parse("00000000-0000-4000-8000-000000000001");

    [Fact]
    public void PersonAttributesKeepOnlyUidValues()
    {
        var ticket = JsonSerializer.Deserialize<TdxTicket>($$"""
            {"ID": 1, "Attributes": [
              {"ID": 10, "Name": "Primary Contact", "Value": "{{PersonUid}}", "ValueText": "A Person"},
              {"ID": 11, "Name": "Sample Choice", "Value": 1001, "ValueText": "Choice A"},
              {"ID": 12, "Name": "Sample Text", "Value": "Some free text"}
            ]}
            """)!;

        var attribute = Assert.Single(TicketNotifyRecipients.PersonAttributes(ticket));
        Assert.Equal("Primary Contact", attribute.Name);
        Assert.Equal(PersonUid, attribute.PersonUid);
    }

    [Fact]
    public void GroupMemberDecodesNotificationFlagAndDepartment()
    {
        var members = JsonSerializer.Deserialize<List<TdxPerson>>("""
            [{"UID": "6f9619ff-8b86-d011-b42d-00c04fc964ff", "FullName": "Member One", "PrimaryEmail": "one@example.invalid",
              "DefaultAccountName": "Admin", "IsGroupNotified": false}]
            """)!;

        Assert.False(members[0].IsGroupNotified);
        Assert.Equal("one@example.invalid · Admin", members[0].Subtitle);
    }

    [Fact]
    public void RecipientsMergeByEmailAndExpandTheGroupToNotifiedMembers()
    {
        var ticket = new TdxTicket
        {
            Id = 1,
            RequestorName = "Pat Example", RequestorEmail = "pat@example.invalid",
            ResponsibleFullName = "Sam Example", ResponsibleEmail = "sam@example.invalid",
            ResponsibleGroupId = 5, ResponsibleGroupName = "Sample Group",
        };
        var members = new List<TdxPerson>
        {
            new() { FullName = "Sam Example", PrimaryEmail = "sam@example.invalid" },
            new() { FullName = "Quiet Member", PrimaryEmail = "quiet@example.invalid", IsGroupNotified = false },
            new() { FullName = "No Flag", PrimaryEmail = "noflag@example.invalid" },
        };
        var contacts = new List<TdxPerson>
        {
            new() { FullName = "Pat Example", PrimaryEmail = "PAT@example.invalid" },
            new() { FullName = "Lee Example", PrimaryEmail = "lee@example.invalid" },
            new() { FullName = "No Address" },
        };
        var attributes = new List<AttributePerson>
        {
            new("Primary Contact", new TdxPerson { FullName = "Lee Example", PrimaryEmail = "lee@example.invalid" }),
        };

        var recipients = TicketNotifyRecipients.Build(ticket, members, contacts, attributes);

        Assert.Equal(new[]
        {
            "Sample Group (Responsible Group)",
            "Pat Example (Requestor, Contact)",
            "Sam Example (Responsible)",
            "Lee Example (Contact, Primary Contact)",
        }, recipients.Select(r => r.Label));
        Assert.Equal(new[] { "sam@example.invalid", "noflag@example.invalid" }, recipients[0].Emails);

        var emails = TicketNotifyRecipients.SelectedEmails(recipients, new HashSet<string> { "group:5", "sam@example.invalid", "pat@example.invalid" });
        Assert.Equal(new[] { "sam@example.invalid", "noflag@example.invalid", "pat@example.invalid" }, emails);
    }

    [Fact]
    public void AGroupNobodyIsNotifiedByIsNotOffered()
    {
        var ticket = new TdxTicket { Id = 1, ResponsibleGroupId = 5, ResponsibleGroupName = "Sample Group" };
        var members = new List<TdxPerson> { new() { PrimaryEmail = "quiet@example.invalid", IsGroupNotified = false } };

        Assert.Empty(TicketNotifyRecipients.Build(ticket, members, null, null));
    }

    [Fact]
    public async Task CommentSendsTheSelectedEmailsInNotify()
    {
        var handler = new StubHandler(_ => Json("""{"ID": 9}"""));
        using var service = Service(handler);

        await service.AddCommentAsync(42, "Hello", notify: new[] { "pat@example.invalid", "sam@example.invalid" });

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://service.example.com/TDWebApi/api/7/tickets/42/feed", request.RequestUri!.ToString());
        using var json = JsonDocument.Parse(body!);
        Assert.Equal(new[] { "pat@example.invalid", "sam@example.invalid" },
            json.RootElement.GetProperty("Notify").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task ContactRoutesUseTheTicketAndPersonPaths()
    {
        var handler = new StubHandler(request => request.Method == HttpMethod.Get
            ? Json($$"""[{"UID": "{{PersonUid}}", "FullName": "Lee Example", "PrimaryEmail": "lee@example.invalid"}]""")
            : new HttpResponseMessage(HttpStatusCode.OK));
        using var service = Service(handler);

        var contacts = await service.GetTicketContactsAsync(42);
        await service.AddTicketContactAsync(42, PersonUid);
        await service.RemoveTicketContactAsync(42, PersonUid);
        await service.GetGroupMembersAsync(5);

        Assert.Equal("Lee Example", Assert.Single(contacts).FullName);
        Assert.Equal(new[]
        {
            "GET https://service.example.com/TDWebApi/api/7/tickets/42/contacts",
            $"POST https://service.example.com/TDWebApi/api/7/tickets/42/contacts/{PersonUid}",
            $"DELETE https://service.example.com/TDWebApi/api/7/tickets/42/contacts/{PersonUid}",
            "GET https://service.example.com/TDWebApi/api/groups/5/members",
        }, handler.Requests.Select(r => $"{r.Request.Method} {r.Request.RequestUri}"));
        Assert.Null(handler.Requests[1].Body);
    }

    [Fact]
    public async Task ARejectedContactChangeThrows()
    {
        var handler = new StubHandler(_ => Json("""{"Message": "denied"}""", HttpStatusCode.Forbidden));
        using var service = Service(handler);

        var ex = await Assert.ThrowsAsync<TdxRequestException>(() => service.AddTicketContactAsync(42, PersonUid));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("Pat Example &lt;pat@example.invalid&gt;", "Notified: Pat Example <pat@example.invalid>")]
    public void NotifiedLineShowsOnlyWhenTheListHasNames(string? list, string expected)
    {
        var item = new FeedDisplayItem { NotificationRecipients = FeedDisplayItem.NotifiedLine(list) };

        Assert.Equal(expected, item.NotificationRecipients);
        Assert.Equal(expected.Length > 0, item.NotificationVisibility == System.Windows.Visibility.Visible);
    }
}
