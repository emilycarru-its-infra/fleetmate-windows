using System.Net;
using System.Text;
using FleetMate.Core.Config;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Teams;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The Teams search client against a stubbed transport: what it puts on the
/// wire, how it decodes the API's JSON, and how each refusal surfaces.
/// </summary>
public class TeamsSearchServiceTests
{
    private const string Base = "https://search.example.org/api/";
    private const string Scope = "api://example-search/.default";

    private sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string?> Authorizations { get; } = new();
        public List<string?> Purposes { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Authorizations.Add(request.Headers.Authorization?.ToString());
            Purposes.Add(request.Headers.TryGetValues("X-Vantage-Purpose", out var p) ? string.Join(",", p) : null);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>A token source whose broker hands out numbered tokens and records the scopes asked for.</summary>
    private static EntraTokenSource Tokens(List<string> scopes)
    {
        var n = 0;
        return new EntraTokenSource("00000000-0000-0000-0000-000000000000")
        {
            AcquireOverride = (scope, _) =>
            {
                scopes.Add(scope);
                n++;
                return Task.FromResult(($"token-{n}", DateTimeOffset.UtcNow.AddHours(1)));
            },
        };
    }

    private static TeamsSearchService Service(Stub stub, List<string>? scopes = null)
    {
        var source = Tokens(scopes ?? new List<string>());
        return new TeamsSearchService(Base, Scope, stub, () => source);
    }

    private const string SearchBody = """
    {"query":"printer","as_of":"2026-11-04T12:00:00Z","total":42,"results":[
      {"thread_id":"1730000000000","team_id":"team-1","team_name":"Service Desk","channel_id":"19:abc@thread.tacv2",
       "channel_name":"General","subject":null,"snippet":"The printer on level 2 is jammed",
       "author_display_name":"Alex Example","created_at":"2026-11-01T17:30:00.123Z",
       "last_activity_at":"2026-11-02T09:00:00Z","reply_count":3,"participants":["Alex Example","Sam Example"],
       "score":12.5,"web_url":"https://teams.example.org/l/message/1"}
    ]}
    """;

    // ── Configuration ──

    [Theory]
    [InlineData(null, Scope)]
    [InlineData(Base, "  ")]
    public async Task UnconfiguredServiceRefusesWithoutARequest(string? baseUrl, string? scope)
    {
        using var service = new TeamsSearchService(baseUrl, scope);
        Assert.False(service.IsConfigured);
        var ex = await Assert.ThrowsAsync<TeamsSearchException>(() => service.GetChannelsAsync());
        Assert.Equal(TeamsSearchFailure.NotConfigured, ex.Failure);
    }

    [Fact]
    public void RegistryAndPolicyValuesSetTheTeamsKeys()
    {
        var values = new Dictionary<string, object?> { ["TeamsApiUrl"] = "https://search.example.org", ["TeamsApiScope"] = Scope, ["TeamsApiPurpose"] = "purpose.other" };
        var config = new FleetMateConfig();
        FleetMateConfig.ApplyRegistryValues(name => values.GetValueOrDefault(name), config, fromPolicy: true);
        Assert.Equal("https://search.example.org", config.TeamsApiUrl);
        Assert.Equal(Scope, config.TeamsApiScope);
        Assert.Equal("purpose.other", config.TeamsApiPurpose);
    }

    [Fact]
    public void SnakeCaseYamlKeysSetTheTeamsKeys()
    {
        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var config = deserializer.Deserialize<FleetMateConfig>("teams_api_url: https://search.example.org\nteams_api_scope: api://example-search\n");
        Assert.Equal("https://search.example.org", config.TeamsApiUrl);
        Assert.Equal("api://example-search", config.TeamsApiScope);
    }

    [Fact]
    public async Task BrowseUsesTheSameIdentityAndPurposeAndDecodesPagination()
    {
        var stub = new Stub(HttpStatusCode.OK, """
            {"as_of":"2026-11-01T00:00:00Z","index_version":"snapshot-1","total":0,"results":[],"next_offset":null}
            """);
        using var service = Service(stub);
        var response = (await service.BrowseAsync(new TeamsBrowseQuery(Channel: "channel-1"))).Value;
        Assert.Equal("snapshot-1", response.IndexVersion);
        Assert.Null(response.NextOffset);
        Assert.Equal("/api/v1/teams/threads", stub.Requests.Single().RequestUri!.AbsolutePath);
        Assert.Equal("Bearer token-1", stub.Authorizations.Single());
        Assert.Equal(TeamsSearchService.DefaultPurpose, stub.Purposes.Single());
    }

    [Fact]
    public async Task BrowseRefusesAnUnversionedResponse()
    {
        using var service = Service(new Stub(HttpStatusCode.OK, """
            {"total":0,"results":[]}
            """));
        var error = await Assert.ThrowsAsync<TeamsSearchException>(() => service.BrowseAsync(new TeamsBrowseQuery(Team: "team-1")));
        Assert.Equal(TeamsSearchFailure.InvalidResponse, error.Failure);
    }

    // ── URL building ──

    [Fact]
    public void SearchUriCarriesEveryFilterEncoded()
    {
        using var service = Service(new Stub(HttpStatusCode.OK, "{}"));
        var uri = service.BuildSearchUri(new TeamsSearchQuery(
            "  C++ & \"wifi\" #help ", "team-1", "19:abc@thread.tacv2", "Alex", "2026-10-01", "2026-11-01", 50));
        Assert.Equal("https://search.example.org/api/v1/teams/search?q=C%2B%2B%20%26%20%22wifi%22%20%23help"
                     + "&team=team-1&channel=19%3Aabc%40thread.tacv2&author=Alex&since=2026-10-01&until=2026-11-01&limit=50",
            uri.OriginalString);
    }

    [Fact]
    public void SearchUriOmitsUnsetFilters()
    {
        using var service = Service(new Stub(HttpStatusCode.OK, "{}"));
        Assert.Equal("https://search.example.org/api/v1/teams/search?q=vpn",
            service.BuildSearchUri(new TeamsSearchQuery("vpn", Team: "")).OriginalString);
    }

    [Theory]
    [InlineData("   ", null, null, null)]
    [InlineData("x", 0, null, null)]
    [InlineData("x", 101, null, null)]
    [InlineData("x", null, "2026-13-01", null)]
    [InlineData("x", null, null, "yesterday")]
    public void SearchRejectsBadArguments(string text, int? limit, string? since, string? until)
    {
        using var service = Service(new Stub(HttpStatusCode.OK, "{}"));
        var ex = Assert.Throws<TeamsSearchException>(() =>
            service.BuildSearchUri(new TeamsSearchQuery(text, Since: since, Until: until, Limit: limit)));
        Assert.Equal(TeamsSearchFailure.InvalidArgument, ex.Failure);
    }

    [Fact]
    public void ThreadUriEncodesIdAndRequiresChannel()
    {
        using var service = Service(new Stub(HttpStatusCode.OK, "{}"));
        Assert.Equal("https://search.example.org/api/v1/teams/threads/17%2F30?channel=19%3Aabc%40thread.tacv2",
            service.BuildThreadUri("17/30", "19:abc@thread.tacv2").OriginalString);
        Assert.Throws<TeamsSearchException>(() => service.BuildThreadUri("1", " "));
        Assert.Throws<TeamsSearchException>(() => service.BuildThreadUri("", "c"));
    }

    // ── Requests ──

    [Fact]
    public async Task SearchSendsBearerForConfiguredScopeAndDecodes()
    {
        var stub = new Stub(HttpStatusCode.OK, SearchBody);
        var scopes = new List<string>();
        using var service = Service(stub, scopes);

        var payload = await service.SearchAsync(new TeamsSearchQuery("printer", Limit: 5));

        var request = Assert.Single(stub.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v1/teams/search", request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer token-1", stub.Authorizations[0]);
        Assert.Equal(new[] { Scope }, scopes);
        Assert.Equal("purpose.its_teams_channel_search", stub.Purposes[0]);

        var response = payload.Value;
        Assert.Equal(42, response.Total);
        Assert.Equal("2026-11-04T12:00:00Z", response.AsOf);
        var hit = Assert.Single(response.Results);
        Assert.Equal("1730000000000", hit.ThreadId);
        Assert.Equal("General", hit.ChannelName);
        Assert.Null(hit.Subject);
        Assert.Equal(3, hit.ReplyCount);
        Assert.Equal(2, hit.Participants.Count);
        Assert.Equal("https://teams.example.org/l/message/1", hit.WebUrl);
        Assert.Equal(SearchBody, payload.Raw);
    }

    [Fact]
    public async Task ThreadDecodesMessagesOldestFirst()
    {
        var stub = new Stub(HttpStatusCode.OK, """
        {"thread_id":"t1","team_id":"team-1","team_name":"Service Desk","channel_id":"c1","channel_name":"General",
         "subject":"Printer","web_url":null,"messages":[
          {"message_id":"t1","reply_to_id":null,"author_display_name":"Alex Example","created_at":"2026-11-01T17:30:00Z",
           "edited_at":null,"text":"Jammed again","attachments":[],"web_url":null},
          {"message_id":"m2","reply_to_id":"t1","author_display_name":null,"created_at":"2026-11-01T18:00:00Z",
           "edited_at":"2026-11-01T18:05:00Z","text":"Fixed","attachments":[{"name":"photo.jpg","url":null}],"web_url":null}
        ]}
        """);
        using var service = Service(stub);

        var thread = (await service.GetThreadAsync("t1", "c1")).Value;

        Assert.Equal("?channel=c1", Assert.Single(stub.Requests).RequestUri!.Query);
        Assert.Equal("Printer", thread.Subject);
        Assert.Equal(new[] { "t1", "m2" }, thread.Messages.Select(m => m.MessageId));
        Assert.Equal("t1", thread.Messages[1].ReplyToId);
        Assert.Null(thread.Messages[1].AuthorDisplayName);
        Assert.Equal(new TeamsAttachment("photo.jpg", null), Assert.Single(thread.Messages[1].Attachments));
    }

    [Fact]
    public async Task ChannelsDecode()
    {
        var stub = new Stub(HttpStatusCode.OK, """
        {"as_of":null,"channels":[{"team_id":"team-1","team_name":"Service Desk","channel_id":"c1","channel_name":"General",
          "membership_type":"standard","threads":120,"last_activity_at":null},
         {"team_id":"team-2","team_name":"Projects","channel_id":"c2","channel_name":"Shared work",
          "membership_type":"shared","threads":4,"last_activity_at":"2026-11-03T08:00:00Z"}]}
        """);
        using var service = Service(stub);

        var response = (await service.GetChannelsAsync()).Value;

        Assert.Equal("/api/v1/teams/channels", Assert.Single(stub.Requests).RequestUri!.AbsolutePath);
        Assert.Null(response.AsOf);
        Assert.Equal(new[] { "standard", "shared" }, response.Channels.Select(c => c.MembershipType));
        Assert.Equal(120, response.Channels[0].Threads);
    }

    // ── Errors ──

    [Fact]
    public async Task UnauthorizedRetriesOnceWithAFreshToken()
    {
        var stub = new Stub(HttpStatusCode.Unauthorized, """{"detail":"expired"}""");
        var scopes = new List<string>();
        using var service = Service(stub, scopes);

        var ex = await Assert.ThrowsAsync<TeamsSearchException>(() => service.GetChannelsAsync());

        Assert.Equal(TeamsSearchFailure.Unauthorized, ex.Failure);
        Assert.Equal(new[] { "Bearer token-1", "Bearer token-2" }, stub.Authorizations);
        Assert.Equal(2, scopes.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, TeamsSearchFailure.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable, TeamsSearchFailure.IndexNotReady)]
    [InlineData(HttpStatusCode.NotFound, TeamsSearchFailure.NotFound)]
    public async Task RefusalsMapToTheirOwnFailures(HttpStatusCode status, TeamsSearchFailure expected)
    {
        var stub = new Stub(status, """{"detail":"no"}""");
        using var service = Service(stub);

        var ex = await Assert.ThrowsAsync<TeamsSearchException>(() => service.GetChannelsAsync());

        Assert.Equal(expected, ex.Failure);
        Assert.Single(stub.Requests);
        if (expected == TeamsSearchFailure.Forbidden) Assert.Contains("not authorized", ex.Message);
    }

    [Fact]
    public async Task ConfiguredPurposeIsSentOnEveryRequestIncludingTheRetry()
    {
        var stub = new Stub(HttpStatusCode.Unauthorized, "{}");
        var source = Tokens(new List<string>());
        using var service = new TeamsSearchService(Base, Scope, stub, () => source, purpose: "purpose.other");
        await Assert.ThrowsAsync<TeamsSearchException>(() => service.GetChannelsAsync());
        Assert.Equal(new[] { "purpose.other", "purpose.other" }, stub.Purposes);
    }

    [Theory]
    [InlineData("purpose_not_granted", TeamsSearchFailure.PurposeNotGranted)]
    [InlineData("access_denied", TeamsSearchFailure.Forbidden)]
    public async Task ForbiddenDistinguishesPurposeFromAccess(string detail, TeamsSearchFailure expected)
    {
        using var service = Service(new Stub(HttpStatusCode.Forbidden, $"{{\"detail\":\"{detail}\"}}"));
        var ex = await Assert.ThrowsAsync<TeamsSearchException>(() => service.GetChannelsAsync());
        Assert.Equal(expected, ex.Failure);
    }

    [Fact]
    public async Task OtherFailuresCarrySanitisedDetail()
    {
        using var service = Service(new Stub((HttpStatusCode)422, "{\"detail\":\"bad \\u001b[31mlimit\"}"));
        var ex = await Assert.ThrowsAsync<TeamsSearchException>(() => service.SearchAsync(new TeamsSearchQuery("x")));
        Assert.Equal(TeamsSearchFailure.Http, ex.Failure);
        Assert.Equal("Teams search API returned HTTP 422: bad [31mlimit", ex.Message);
    }

    [Fact]
    public async Task MalformedBodyIsAnInvalidResponse()
    {
        using var service = Service(new Stub(HttpStatusCode.OK, """{"channels":"nope"}"""));
        var ex = await Assert.ThrowsAsync<TeamsSearchException>(() => service.GetChannelsAsync());
        Assert.Equal(TeamsSearchFailure.InvalidResponse, ex.Failure);
    }

    // ── Display helpers ──

    [Fact]
    public void TerminalSafeStripsEscapesAndBidiButKeepsNewlines()
    {
        Assert.Equal("ok]0;title\nline2txt\tend", TeamsText.TerminalSafe("ok\u001B]0;title\u0007\r\nline2\u202Etxt\tend"));
        Assert.Equal("a b c", TeamsText.OneLine("a\n\n  b\tc"));
    }

    [Fact]
    public void LocalTimeParsesApiTimestamps()
    {
        var utc = TimeZoneInfo.Utc;
        Assert.Equal("2026-11-01 17:30", TeamsText.LocalTime("2026-11-01T17:30:00.123Z", utc));
        Assert.Equal("2026-11-01 17:30", TeamsText.LocalTime("2026-11-01T17:30:00+00:00", utc));
        Assert.Equal("2026-11-01 17:30", TeamsText.LocalTime("2026-11-01T17:30:00", utc));
        Assert.Equal("2026-11-01 10:30", TeamsText.LocalTime("2026-11-01T17:30:00Z",
            TimeZoneInfo.CreateCustomTimeZone("minus7", TimeSpan.FromHours(-7), "minus7", "minus7")));
        Assert.Equal("-", TeamsText.LocalTime(null));
        Assert.Equal("soon", TeamsText.LocalTime("soon"));
    }
}
