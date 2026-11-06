using System.Net;
using System.Text;
using FleetMate.Core.Config;
using FleetMate.Core.Models;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The TeamDynamix credential check must agree with the ticket list. It used
/// to run a search that answers any failure with an empty list, so a refused
/// or missing sign-in showed as Valid "signed in as SSO" while the list said
/// Not Signed In.
/// </summary>
public class TdxAuthProbeTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static FleetMateConfig Config() => new()
    {
        Tdx = new TdxConfig { BaseUrl = "https://service.example.com", AppId = 7 },
    };

    private static async Task<AuthSystemStatus> ProbeAsync(HttpStatusCode status, string body)
    {
        var config = Config();
        using var tdx = new TdxService(config.Tdx!, new StubHandler(_ => Json(body, status)));
        tdx.SetSsoToken("synthetic-token", DateTime.UtcNow.AddHours(1), "operator@example.com", "Operator");
        var auth = new AuthManager(config, AppEdition.TicketsMate);

        await auth.ProbeAllAsync(null, tdx, null, null);
        return auth.Systems[AuthSystemId.Tdx];
    }

    [Fact]
    public async Task ARefusedSearchIsAFailureNotValid()
    {
        var status = await ProbeAsync(HttpStatusCode.Unauthorized, """{"Message":"Authorization has been denied."}""");

        Assert.Equal(AuthStateKind.Failed, status.State.Kind);
    }

    [Fact]
    public async Task AnAnsweredSearchIsValidAsTheSignedInPerson()
    {
        var status = await ProbeAsync(HttpStatusCode.OK, "[]");

        Assert.Equal(AuthStateKind.Valid, status.State.Kind);
        Assert.Equal("Operator", status.User);
    }
}
