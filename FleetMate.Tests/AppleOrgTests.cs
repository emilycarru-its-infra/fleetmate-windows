using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services.Devices;
using Xunit;

namespace FleetMate.Tests;

public class AppleOrgTests
{
    private static AppleOrgDevice Device(string serial, string org = "school", string? server = "srv-1", string? status = "ASSIGNED") =>
        new() { SerialNumber = serial, OrgId = org, Model = "MacBook Air", ProductFamily = "Mac", Status = status, AssignedServerId = server, OrderNumber = "ORD-1" };

    private static AppleOrgSnapshot Org(string name, string clientId, params AppleOrgDevice[] devices) =>
        new(new AppleOrgProfile(name, clientId), devices, new[] { new AppleOrgServer("srv-1", name, "Intune Service", "MDM") });

    [Fact]
    public void Labels_NameByService_AddProfileNameOnlyWhenTwoAreTheSameKind()
    {
        var one = AppleOrgProfile.Labels(new[] { new AppleOrgProfile("default", "SCHOOLAPI.abc") });
        Assert.Equal("Apple School Manager", one["default"]);

        var labels = AppleOrgProfile.Labels(new[]
        {
            new AppleOrgProfile("north", "BUSINESSAPI.a"),
            new AppleOrgProfile("south", "BUSINESSAPI.b"),
            new AppleOrgProfile("default", "SCHOOLAPI.c"),
        });
        Assert.Equal("Apple Business Manager (north)", labels["north"]);
        Assert.Equal("Apple School Manager", labels["default"]);
        Assert.DoesNotContain(labels.Values, v => v == "default");
    }

    [Fact]
    public void ClientAssertion_IsAnEs256JwtApplesTokenEndpointAccepts()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportPkcs8PrivateKeyPem();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        var jwt = AppleOrgService.CreateClientAssertion("SCHOOLAPI.client", "KEY123", pem, now);
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        var header = JsonNode.Parse(Decode(parts[0]))!;
        Assert.Equal("ES256", header["alg"]!.GetValue<string>());
        Assert.Equal("KEY123", header["kid"]!.GetValue<string>());

        var claims = JsonNode.Parse(Decode(parts[1]))!;
        Assert.Equal("SCHOOLAPI.client", claims["iss"]!.GetValue<string>());
        Assert.Equal("SCHOOLAPI.client", claims["sub"]!.GetValue<string>());
        Assert.Equal(AppleOrgService.TokenEndpoint, claims["aud"]!.GetValue<string>());
        Assert.Equal(1_800_000_000, claims["iat"]!.GetValue<long>());
        Assert.Equal(1_800_001_200, claims["exp"]!.GetValue<long>());

        // r||s, 64 bytes, verifiable with the public key.
        var signature = Base64UrlDecode(parts[2]);
        Assert.Equal(64, signature.Length);
        Assert.True(key.VerifyData(Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]), signature,
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void Enrich_JoinsBySerial_AndAddsNotEnrolledRows()
    {
        var intune = new[]
        {
            new IntuneDevice { Id = "i1", DeviceName = "Mac-1", SerialNumber = " c02abc ", OperatingSystem = "macOS" },
            new IntuneDevice { Id = "i2", DeviceName = "PC-1", SerialNumber = "PC1", OperatingSystem = "Windows" },
        };
        var rows = AppleOrgJoin.Enrich(DeviceListJoin.Merge(intune, Array.Empty<AutopilotDevice>()),
            new[] { Org("school", "SCHOOLAPI.x", Device("C02ABC"), Device("C02XYZ", status: "UNASSIGNED", server: null)) });

        var mac = rows.Single(r => r.Id == "i1");
        Assert.Equal("Intune Service", mac.ServiceText);
        Assert.Equal("Assigned", mac.OrgStatusText);
        Assert.Equal("ORD-1", mac.GroupOrOrderText);
        Assert.Equal("Apple School Manager", mac.Value(DeviceFacet.AppleOrganization));

        var pc = rows.Single(r => r.Id == "i2");
        Assert.Equal("Intune", pc.ServiceText);
        Assert.Equal(DeviceListRow.NotInOrganization, pc.Value(DeviceFacet.AppleOrganization));

        var orgOnly = rows.Single(r => r.Id == DeviceListRow.OrgOnlyPrefix + "C02XYZ");
        Assert.False(orgOnly.IsEnrolled);
        Assert.Equal("Not Enrolled", orgOnly.ComplianceText);
        Assert.Equal("macOS", orgOnly.PlatformText);
        Assert.Equal("Unassigned", orgOnly.OrgStatusText);
        Assert.Equal(DeviceListRow.Missing, orgOnly.ServiceText);
        Assert.Equal("No Service", orgOnly.Value(DeviceFacet.ManagementService));
        Assert.Equal("Apple", orgOnly.ManufacturerText);
    }

    [Fact]
    public void Enrich_ReleasedDuplicate_ResolvesToTheHoldingOrg()
    {
        var released = Device("DUP1", org: "old") with { ReleasedFromOrg = DateTimeOffset.UtcNow.AddDays(-3) };
        var holding = Device("DUP1", org: "new");
        var rows = AppleOrgJoin.Enrich(new List<DeviceListRow>(),
            new[] { Org("old", "BUSINESSAPI.a", released), Org("new", "BUSINESSAPI.b", holding) });

        var row = Assert.Single(rows);
        Assert.Equal("new", row.Apple!.OrgId);
        Assert.Equal("Apple Business Manager (new)", row.OrgName);
    }

    [Fact]
    public void Columns_ReadMigrationPurchaseAndAdded()
    {
        var device = Device("M1") with
        {
            MigrationStatus = "STARTED",
            PurchaseSource = "RESELLER",
            AddedToOrg = new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.Zero),
        };
        var row = Assert.Single(AppleOrgJoin.Enrich(new List<DeviceListRow>(), new[] { Org("school", "SCHOOLAPI.x", device) }));
        Assert.Equal("In Progress", row.MigrationText);
        Assert.Equal("In Progress", row.Value(DeviceFacet.Migration));
        Assert.Equal("Reseller", row.PurchaseSourceText);
        Assert.StartsWith("2026-03-0", row.AddedText);
    }

    [Fact]
    public void Actions_OnlyWhenOneOrgHoldsEverySelectedDevice()
    {
        var school = new AppleOrgProfile("school", "SCHOOLAPI.x");
        var rows = AppleOrgJoin.Enrich(new List<DeviceListRow>(),
            new[] { Org("school", "SCHOOLAPI.x", Device("A"), Device("B")), Org("biz", "BUSINESSAPI.y", Device("C", org: "biz")) });
        var a = rows.Single(r => r.SerialNumber == "A");
        var b = rows.Single(r => r.SerialNumber == "B");
        var c = rows.Single(r => r.SerialNumber == "C");

        Assert.True(AppleOrgJoin.IsAvailable(AppleOrgActionKind.Assign, new[] { a, b }, school));
        Assert.True(AppleOrgJoin.IsAvailable(AppleOrgActionKind.Unassign, new[] { a, b }, school));
        Assert.Null(AppleOrgJoin.SingleOrg(new[] { a, c }));
        Assert.False(AppleOrgJoin.IsAvailable(AppleOrgActionKind.Assign, new[] { a, c }, school));
        Assert.False(AppleOrgJoin.IsAvailable(AppleOrgActionKind.Release, new[] { a }, school));
        Assert.True(AppleOrgJoin.IsAvailable(AppleOrgActionKind.Release, new[] { c }, new AppleOrgProfile("biz", "BUSINESSAPI.y")));
        Assert.False(AppleOrgJoin.IsAvailable(AppleOrgActionKind.CancelMigration, new[] { a }, school));
        Assert.True(AppleOrgJoin.IsAvailable(AppleOrgActionKind.ScheduleMigration, new[] { a }, school));
    }

    [Fact]
    public void ActivityBody_CarriesTypeDevicesServerAndDeadline()
    {
        var deadline = new DateTimeOffset(2026, 12, 1, 23, 59, 0, TimeSpan.Zero);
        var body = AppleOrgService.ActivityBody(
            new AppleOrgAction(AppleOrgActionKind.ScheduleMigration, "srv-9", deadline), new[] { "S1", "S2" });
        var data = body["data"]!;
        Assert.Equal("orgDeviceActivities", data["type"]!.GetValue<string>());
        Assert.Equal("ASSIGN_DEVICES_WITH_MDM_MIGRATION_DEADLINE", data["attributes"]!["activityType"]!.GetValue<string>());
        Assert.Equal("2026-12-01T23:59:00Z", data["attributes"]!["activityTypeMetadata"]!["mdmMigrationDeadlineDateTime"]!.GetValue<string>());
        Assert.Equal("srv-9", data["relationships"]!["mdmServer"]!["data"]!["id"]!.GetValue<string>());
        Assert.Equal(2, data["relationships"]!["devices"]!["data"]!.AsArray().Count);

        var cancel = AppleOrgService.ActivityBody(new AppleOrgAction(AppleOrgActionKind.CancelMigration), new[] { "S1" });
        Assert.Null(cancel["data"]!["relationships"]!["mdmServer"]);
        Assert.Null(cancel["data"]!["attributes"]!["activityTypeMetadata"]);
    }

    [Fact]
    public async Task Snapshot_SignsIn_PagesDevices_AndReadsAssignmentsFromServerListings()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var handler = new FakeApple();
        using var service = new AppleOrgService(new AppleOrgProfile("school", "SCHOOLAPI.x"),
            new AppleOrgCredentialStore.Secret("SCHOOLAPI.x", "K1", key.ExportPkcs8PrivateKeyPem()), handler);

        var snapshot = await service.SnapshotAsync();

        Assert.Equal(new[] { "S1", "S2" }, snapshot.Devices.Select(d => d.SerialNumber));
        Assert.Equal("srv-1", snapshot.Devices.Single(d => d.SerialNumber == "S2").AssignedServerId);
        Assert.Null(snapshot.Devices.Single(d => d.SerialNumber == "S1").AssignedServerId);
        Assert.Equal("Main Service", Assert.Single(snapshot.Servers).Name);
        Assert.Equal(1, handler.TokenRequests);
        Assert.Contains("scope=school.api", handler.TokenBody);
        Assert.Contains("client_assertion_type=urn%3Aietf%3Aparams%3Aoauth%3Aclient-assertion-type%3Ajwt-bearer", handler.TokenBody);
    }

    private sealed class FakeApple : HttpMessageHandler
    {
        public int TokenRequests;
        public string TokenBody = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url == AppleOrgService.TokenEndpoint)
            {
                TokenRequests++;
                TokenBody = await request.Content!.ReadAsStringAsync(ct);
                return Json("""{"access_token":"tok","expires_in":3600,"token_type":"Bearer"}""");
            }
            Assert.Equal("Bearer tok", request.Headers.Authorization?.ToString());
            if (url.Contains("/v1/orgDevices") && !url.Contains("cursor"))
                return Json("""{"data":[{"id":"S1","attributes":{"serialNumber":"S1","deviceModel":"iPad","productFamily":"iPad"}}],"meta":{"paging":{"nextCursor":"c2"}}}""");
            if (url.Contains("/v1/orgDevices") && url.Contains("cursor=c2"))
                return Json("""{"data":[{"id":"S2","attributes":{"serialNumber":"S2","deviceModel":"Mac","productFamily":"Mac","wifiMacAddress":["aa","bb"]}}],"meta":{"paging":{}}}""");
            if (url.EndsWith("/v1/mdmServers"))
                return Json("""{"data":[{"id":"srv-1","attributes":{"serverName":"Main Service","serverType":"MDM"}}]}""");
            if (url.Contains("/v1/mdmServers/srv-1/relationships/devices"))
                return Json("""{"data":[{"type":"orgDevices","id":"s2"}],"links":{}}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static string Decode(string part) => Encoding.UTF8.GetString(Base64UrlDecode(part));

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
