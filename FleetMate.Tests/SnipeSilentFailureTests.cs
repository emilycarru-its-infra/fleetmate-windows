using System.Net;
using System.Text;
using FleetMate.Commands.Inventory;
using FleetMate.Commands.Shared;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Inventory;
using FleetMate.GUI.Views.Reporting;
using FleetMate.GUI.Views.Shared.Widgets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// A Snipe-IT call that cannot get its data says why. A sign-in that failed,
/// Snipe-IT's own error envelope, or a web page in place of the API all used to
/// come back as an empty list: "0 assets", "[]" with exit 0, and nothing logged.
/// </summary>
public class SnipeSilentFailureTests
{
    private const string Rows = """{"total":1,"rows":[{"id":7,"asset_tag":"A-7","name":"LAB-07","status_label":{"id":1,"name":"Deployed","status_meta":"deployed"}}]}""";

    private sealed class Answer : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly string _type;
        public int Calls { get; private set; }

        public Answer(HttpStatusCode status, string body, string type = "application/json")
        {
            _status = status;
            _body = body;
            _type = type;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                RequestMessage = request,
                Content = new StringContent(_body, Encoding.UTF8, _type),
            });
        }
    }

    private static SnipeService Service(HttpMessageHandler transport) =>
        new("snipe.example.edu", apiKey: null, cacheMinutes: 5, oidcAudience: "00000000-0000-0000-0000-00000000aaaa", transport);

    private static HttpResponseMessage Response(HttpStatusCode status, string body, string type = "application/json") =>
        new(status)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://snipe.example.edu/api/v1/hardware"),
            Content = new StringContent(body, Encoding.UTF8, type),
        };

    [Fact]
    public async Task AListComesBackAsItIs()
    {
        Assert.Equal(Rows, await SnipeService.ReadListBodyAsync(Response(HttpStatusCode.OK, Rows)));
    }

    [Fact]
    public async Task SnipeItsErrorEnvelopeWithA200IsAFailure()
    {
        var ex = await Assert.ThrowsAsync<SnipeException>(() => SnipeService.ReadListBodyAsync(
            Response(HttpStatusCode.OK, """{"status":"error","messages":"Unauthorized.","payload":null}""")));
        Assert.Contains("Unauthorized.", ex.Message);
    }

    [Fact]
    public async Task AWebPageInPlaceOfTheApiNamesTheAddress()
    {
        var ex = await Assert.ThrowsAsync<SnipeException>(() => SnipeService.ReadListBodyAsync(
            Response(HttpStatusCode.OK, "<!DOCTYPE html><html><body>Sign in</body></html>", "text/html")));
        Assert.Contains("web page", ex.Message);
        Assert.Contains("SnipeUrl", ex.Message);
    }

    [Fact]
    public async Task ARefusalSaysTheSignInWasRefused()
    {
        var ex = await Assert.ThrowsAsync<SnipeException>(() => SnipeService.ReadListBodyAsync(
            Response(HttpStatusCode.Unauthorized, """{"error":"Unauthorized or unauthenticated."}""")));
        Assert.Contains("refused the sign-in (401)", ex.Message);
        Assert.Contains("Unauthorized or unauthenticated.", ex.Message);
    }

    [Fact]
    public async Task AssetsReturnTheRowsSnipeItSent()
    {
        using var snipe = Service(new Answer(HttpStatusCode.OK, Rows));
        var assets = await snipe.GetAssetsAsync();
        Assert.Equal("A-7", Assert.Single(assets).AssetTag);
        Assert.Null(snipe.LastError);
    }

    [Fact]
    public async Task AssetsThrowInsteadOfReadingAsAnEmptyInventory()
    {
        using var snipe = Service(new Answer(HttpStatusCode.OK, """{"status":"error","messages":"Unauthorized."}"""));
        var ex = await Assert.ThrowsAsync<SnipeException>(() => snipe.GetAssetsAsync());
        Assert.Contains("Unauthorized.", ex.Message);
        Assert.Equal(ex.Message, snipe.LastError);
    }

    [Fact]
    public async Task StatusLabelsThrowInsteadOfReadingAsNone()
    {
        using var snipe = Service(new Answer(HttpStatusCode.Forbidden, """{"status":"error","messages":"Forbidden"}"""));
        var ex = await Assert.ThrowsAsync<SnipeException>(() => snipe.GetStatusLabelsAsync());
        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task ATokenThatCannotBeHadIsASignInFailure()
    {
        var source = new EntraTokenSource("00000000-0000-0000-0000-000000000000")
        {
            AcquireOverride = (scope, _) => throw new EntraTokenException(scope, "no silent credential from the Windows sign-in broker"),
        };
        using var snipe = Service(new EntraBearerHandler("00000000-0000-0000-0000-00000000aaaa", () => source));

        var ex = await Assert.ThrowsAsync<SnipeException>(() => snipe.GetAssetsAsync());
        Assert.StartsWith("Sign-in failed:", ex.Message);
        Assert.Contains("no silent credential", ex.Message);
        Assert.Equal("Sign-in failed", WidgetCatalog.AssetsFailureHeadline(ex.Message));
    }

    [Fact]
    public async Task TheAccessCheckRejectsA200WithoutAList()
    {
        using var snipe = Service(new Answer(HttpStatusCode.OK, "<html>login</html>", "text/html"));
        Assert.Contains("web page", await snipe.CheckAccessAsync());
    }

    [Fact]
    public async Task OlderListCallsKeepTheirReasonForTheCli()
    {
        using var snipe = Service(new Answer(HttpStatusCode.OK, """{"status":"error","messages":"Unauthorized."}"""));

        var exitCode = Environment.ExitCode;
        try
        {
            var rows = await SnipeCommand.ListOrReport(snipe, () => snipe.GetLocationsAsync());
            Assert.Null(rows);
            Assert.Equal(1, Environment.ExitCode);
            Assert.Contains("Unauthorized.", snipe.LastError);
        }
        finally
        {
            Environment.ExitCode = exitCode;
        }
    }

    [Fact]
    public async Task TheCliPassesRowsThroughWhenTheCallWorks()
    {
        using var snipe = Service(new Answer(HttpStatusCode.OK, Rows));
        var rows = await SnipeCommand.ListOrReport(snipe, () => snipe.GetAssetsAsync());
        Assert.NotNull(rows);
        Assert.Single(rows!);
    }

    [Fact]
    public async Task StatusReportsSnipeItDisconnectedWithTheReason()
    {
        using var snipe = Service(new Answer(HttpStatusCode.Unauthorized, """{"error":"Unauthorized or unauthenticated."}"""));
        var status = await SnipeStatus.CheckAsync(snipe);
        Assert.NotNull(status.Error);
        Assert.Contains("401", status.Error);
    }

    [Fact]
    public void StatusCountsAssetsByStatusMeta()
    {
        SnipeAsset With(string meta) => new() { StatusLabel = new SnipeStatusLabel { StatusMeta = meta } };
        var status = SnipeStatus.From(new[] { With("deployed"), With("deployed"), With("deployable"), With("archived") }, 3);
        Assert.Equal((4, 2, 1, 1, 3), (status.TotalAssets, status.Deployed, status.Ready, status.Archived, status.Locations));
        Assert.Null(status.Error);
    }

    [Theory]
    [InlineData(null, "No asset data")]
    [InlineData("Sign-in failed: Could not acquire an Entra token", "Sign-in failed")]
    [InlineData("Snipe-IT refused the sign-in (401)", "Sign-in failed")]
    [InlineData("Snipe-IT answered 500 Internal Server Error", "Could not load assets")]
    public void TheInventoryWidgetsSayWhatWentWrong(string? reason, string expected)
    {
        Assert.Equal(expected, WidgetCatalog.AssetsFailureHeadline(reason));
    }

    [Fact]
    public void TheReportingPageShowsAFailedSignIn()
    {
        var hint = ReportingPage.SetupHint(hasUrl: true, audience: "api://reportmate", signInError: "Could not acquire an Entra token");
        Assert.StartsWith("Sign-in failed: Could not acquire an Entra token", hint);
    }
}
