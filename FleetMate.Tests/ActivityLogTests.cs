using System.Net;
using FleetMate.Core.Services.Activity;
using Xunit;

namespace FleetMate.Tests;

public class ActivityLogTests
{
    private const string Serial = "TESTSERIAL0001";
    private const string DeviceId = "0b4f2c1e-9a7d-4e21-8c3b-5f6a7d8e9f01";

    [Fact]
    public void MasksSerialsConsistently()
    {
        var masker = new ActivityMasker(new[] { Serial });
        Assert.Equal("Erase SERIAL-1", masker.Mask($"Erase {Serial}"));
        Assert.Equal("/hardware/byserial/SERIAL-1", masker.Mask($"/hardware/byserial/{Serial.ToLowerInvariant()}"));
        Assert.Equal("/device/SERIAL-2", masker.Mask("/device/PF3ABCD9"));
        Assert.Equal("again SERIAL-1 and SERIAL-2", masker.Mask($"again {Serial} and PF3ABCD9"));
    }

    [Fact]
    public void MasksUdidsHardwareAddressesAndEmails()
    {
        var masker = new ActivityMasker();
        Assert.Equal("/managedDevices/UDID-1/wipe", masker.Mask($"/managedDevices/{DeviceId}/wipe"));
        Assert.Equal("udid UDID-2", masker.Mask("udid 00008030-001A2D3E0C38802E"));
        Assert.Equal("mac MAC-1 and MAC-1", masker.Mask("mac a4:83:e7:12:34:56 and A4-83-E7-12-34-56"));
        Assert.Equal("/users/USER-1/devices", masker.Mask("/users/someone@example.org/devices"));
    }

    [Fact]
    public void LeavesOrdinaryPathsAlone()
    {
        var masker = new ActivityMasker();
        Assert.Equal("/v1.0/deviceManagement/managedDevices", masker.Mask("/v1.0/deviceManagement/managedDevices"));
        Assert.Equal("Sync devices", masker.Mask("Sync devices"));
        Assert.Equal("HTTP 404", masker.Mask("HTTP 404"));
    }

    [Fact]
    public void MasksPrivateHostsOnly()
    {
        var masker = new ActivityMasker();
        Assert.Equal("graph.microsoft.com", masker.MaskHost("graph.microsoft.com"));
        Assert.Equal("host-1", masker.MaskHost("inventory.example.org"));
        Assert.Equal("host-1", masker.MaskHost("INVENTORY.example.org"));
    }

    [Fact]
    public void ReadsSerialsFromQueryBeforeDroppingIt()
    {
        var url = new Uri($"https://graph.microsoft.com/v1.0/deviceManagement/managedDevices?$filter=serialNumber%20eq%20'{Serial}'");
        Assert.Equal(new[] { Serial }, ActivityMasker.SerialsInQuery(url));
    }

    [Fact]
    public void RecordsNoQueryAndFilesUnderTheAction()
    {
        var log = new ActivityLog(capacity: 10);
        log.Remember(Serial, DeviceId.ToUpperInvariant());
        var action = log.Begin("Erase devices", "Microsoft Graph");
        log.Record("Microsoft Graph", "post",
            new Uri($"https://graph.microsoft.com/v1.0/deviceManagement/managedDevices/{DeviceId}/wipe?token=secret"),
            204, DateTimeOffset.Now, TimeSpan.FromMilliseconds(200), action: action);
        log.Finish(action, null);

        var entry = log.Snapshot()[0];
        Assert.Single(entry.Requests);
        Assert.Equal("POST", entry.Requests[0].Method);
        Assert.DoesNotContain("secret", entry.Requests[0].Path);
        Assert.Equal(new[] { Serial }, entry.Serials);
        Assert.Single(ActivityLog.Filter(log.Snapshot(), "testserial00"));
        Assert.Equal("OK", entry.Result);

        var text = ActivityMasker.Export(log.Snapshot());
        Assert.DoesNotContain(Serial, text);
        Assert.DoesNotContain("0b4f2c1e", text);
        Assert.Contains("SERIAL-1", text);
    }

    [Fact]
    public void UnclaimedRequestsGroupIntoOneBackgroundRow()
    {
        var log = new ActivityLog(capacity: 10);
        var now = DateTimeOffset.Now;
        for (var i = 0; i < 3; i++)
            log.Record("GitHub", "GET", new Uri("https://api.github.com/graphql"), 200, now, TimeSpan.FromMilliseconds(100));
        var actions = log.Snapshot();
        Assert.Single(actions);
        Assert.True(actions[0].IsBackground);
        Assert.Equal(3, actions[0].Requests.Count);
    }

    [Fact]
    public void KeepsOnlyTheNewestActions()
    {
        var log = new ActivityLog(capacity: 3);
        for (var n = 0; n < 5; n++) log.Finish(log.Begin($"Action {n}", "Inventory"), null);
        Assert.Equal(new[] { "Action 2", "Action 3", "Action 4" }, log.Snapshot().Select(a => a.Title));
    }

    [Fact]
    public async Task HandlerRecordsUnderTheRunningAction()
    {
        var log = new ActivityLog(capacity: 10);
        using var client = new HttpClient(new ActivityLogHandler("Inventory", new Answer(HttpStatusCode.NotFound), log));
        await log.RunAsync("Look up", "Inventory", new[] { Serial }, async () =>
            (await client.GetAsync("https://inventory.example.org/api/hardware/byserial/X")).StatusCode);

        var action = Assert.Single(log.Snapshot());
        Assert.Equal("Look up", action.Title);
        Assert.Equal(404, Assert.Single(action.Requests).Status);
        Assert.Equal("Failed", action.Result);
    }

    private sealed class Answer(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}
