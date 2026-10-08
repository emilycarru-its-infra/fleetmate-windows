using System.Net;
using System.Security.Cryptography;
using System.Text;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services.Devices;
using Xunit;

namespace FleetMate.Tests;

public class ActivationLockTests
{
    [Theory]
    [InlineData(true, "MDM", AppleActivationLock.MdmLock, "Enabled — MDM")]
    [InlineData(true, "user", AppleActivationLock.UserLock, "Enabled — User")]
    [InlineData(true, null, AppleActivationLock.Enabled, "Enabled")]
    [InlineData(true, "NONE", AppleActivationLock.Enabled, "Enabled")]
    [InlineData(false, "NONE", AppleActivationLock.Disabled, "Disabled")]
    [InlineData(null, "MDM", AppleActivationLock.Unknown, "Unknown")]
    public void From_MapsTheOrganizationsAnswer(bool? isLocked, string? lockType, AppleActivationLock expected, string column)
    {
        var state = AppleActivationLocks.From(isLocked, lockType);
        Assert.Equal(expected, state);
        Assert.Equal(column, state.ColumnText());
    }

    [Fact]
    public void DetailText_SaysWhatClearingNeeds()
    {
        Assert.Equal("Enabled — MDM lock (bypass code escrowed; clearing doesn't need the owner)", AppleActivationLock.MdmLock.DetailText());
        Assert.Equal("Enabled — User lock (needs the owner's Apple Account)", AppleActivationLock.UserLock.DetailText());
        Assert.True(AppleActivationLock.Enabled.IsLocked());
        Assert.False(AppleActivationLock.Unknown.IsLocked());
        Assert.False(AppleActivationLock.Disabled.IsLocked());
    }

    [Fact]
    public async Task ServerError_ReadsAsUnknown_NeverDisabled()
    {
        using var service = Service(new FakeApple(HttpStatusCode.InternalServerError, ""));
        Assert.Equal(AppleActivationLock.Unknown, await service.ActivationLockAsync("SER1"));
    }

    [Fact]
    public async Task EmptyAnswer_ReadsAsUnknown()
    {
        using var service = Service(new FakeApple(HttpStatusCode.OK, """{"data":{"id":"SER1","attributes":{}}}"""));
        Assert.Equal(AppleActivationLock.Unknown, await service.ActivationLockAsync("SER1"));
    }

    [Fact]
    public async Task LockedAnswer_ReadsTheKind_FromTheActivationLockEndpoint()
    {
        var handler = new FakeApple(HttpStatusCode.OK, """{"data":{"id":"SER1","attributes":{"isLocked":true,"lockType":"MDM"}}}""");
        using var service = Service(handler);
        Assert.Equal(AppleActivationLock.MdmLock, await service.ActivationLockAsync("SER1"));
        Assert.EndsWith("/v1/orgDevices/SER1/activationLockStatus", handler.LastUrl);
    }

    [Fact]
    public void Column_ReadsDashUntilTheDeviceIsRead()
    {
        var serial = "COLTEST" + Guid.NewGuid().ToString("N")[..6];
        var row = new DeviceListRow(null, null).WithApple(
            new AppleOrgDevice { SerialNumber = serial, OrgId = "school" }, null, "Apple School Manager");

        Assert.Equal(DeviceListRow.Missing, row.ActivationLockText);
        Assert.False(row.ActivationLockIsLocked);

        ActivationLockCache.Set(serial.ToLowerInvariant(), AppleActivationLock.UserLock);
        Assert.Equal("Enabled — User", row.ActivationLockText);
        Assert.True(row.ActivationLockIsLocked);

        Assert.Equal(DeviceListRow.Missing, new DeviceListRow(new IntuneDevice { Id = "x", SerialNumber = serial }, null).ActivationLockText);
    }

    private static AppleOrgService Service(HttpMessageHandler handler)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new AppleOrgService(new AppleOrgProfile("school", "SCHOOLAPI.x"),
            new AppleOrgCredentialStore.Secret("SCHOOLAPI.x", "K1", key.ExportPkcs8PrivateKeyPem()), handler);
    }

    private sealed class FakeApple(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string LastUrl = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url == AppleOrgService.TokenEndpoint)
                return Task.FromResult(Json(HttpStatusCode.OK, """{"access_token":"tok","expires_in":3600,"token_type":"Bearer"}"""));
            LastUrl = url;
            // A 5xx is retried with backoff before the read gives up as Unknown.
            return Task.FromResult(Json(status, body));
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string text) =>
            new(code) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}
