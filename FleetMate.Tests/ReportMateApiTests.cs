using System.Reflection;
using System.Text.Json;
using FleetMate.Core.Converters;
using FleetMate.Core.Models.Reporting;
using FleetMate.Core.Services.Reporting;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Pins the ReportMate v1 contract.
///
/// The v1 API is camelCase and omits fields on list endpoints, where the old
/// endpoints returned them. A decoder that is strict about either one fails at
/// runtime against a live server and passes every unit test that only feeds it
/// complete payloads — so these deliberately feed it incomplete ones.
/// </summary>
public class ReportMateDecodingTests
{
    /// <summary>The options the service actually uses, read off a real instance.</summary>
    private static JsonSerializerOptions ServiceOptions()
    {
        using var service = new ReportMateService("https://reportmate.example.edu");
        return (JsonSerializerOptions)typeof(ReportMateService)
            .GetField("_jsonOptions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(service)!;
    }

    [Fact]
    public void Device_DecodesCamelCase()
    {
        const string json = """
            {
              "id": "abc",
              "serialNumber": "C02XYZ",
              "deviceName": "LAB-01",
              "osVersion": "15.1",
              "assetTag": "A-1234"
            }
            """;

        var device = JsonSerializer.Deserialize<Device>(json, ServiceOptions());

        Assert.NotNull(device);
        Assert.Equal("C02XYZ", device!.SerialNumber);
        Assert.Equal("LAB-01", device.DeviceName);
        Assert.Equal("A-1234", device.AssetTag);
    }

    [Fact]
    public void Device_SurvivesAListPayloadThatOmitsMostFields()
    {
        // This is the shape that broke the Mac: v1 list endpoints send a thin
        // record, and a decoder requiring every field threw "missing".
        const string json = """{ "serialNumber": "C02XYZ" }""";

        var device = JsonSerializer.Deserialize<Device>(json, ServiceOptions());

        Assert.NotNull(device);
        Assert.Equal("C02XYZ", device!.SerialNumber);
        Assert.Equal(string.Empty, device.DeviceName);
        Assert.Equal(string.Empty, device.Hostname);
    }

    [Fact]
    public void Device_SurvivesAnEmptyObject()
    {
        var device = JsonSerializer.Deserialize<Device>("{}", ServiceOptions());

        Assert.NotNull(device);
        Assert.Equal(string.Empty, device!.SerialNumber);
    }

    [Fact]
    public void Device_IgnoresFieldsItDoesNotKnow()
    {
        // The API adding a field must never break an older client.
        const string json = """
            { "serialNumber": "C02XYZ", "someFutureFieldNobodyHasSeen": { "nested": [1, 2] } }
            """;

        var device = JsonSerializer.Deserialize<Device>(json, ServiceOptions());

        Assert.NotNull(device);
        Assert.Equal("C02XYZ", device!.SerialNumber);
    }

    [Fact]
    public void DevicesResponse_DecodesTheListWrapper()
    {
        const string json = """
            {
              "devices": [ { "serialNumber": "A1" }, { "serialNumber": "B2" } ],
              "total": 2,
              "offset": 0,
              "limit": 100
            }
            """;

        var wrapper = JsonSerializer.Deserialize<DevicesResponse>(json, ServiceOptions());

        Assert.NotNull(wrapper);
        Assert.Equal(2, wrapper!.Devices!.Count);
        Assert.Equal("A1", wrapper.Devices[0].SerialNumber);
        Assert.Equal(2, wrapper.Total);
    }

    [Fact]
    public void DevicesResponse_HandlesAnEmptyPage()
    {
        // Paging stops on an empty page, so this must decode rather than throw.
        var wrapper = JsonSerializer.Deserialize<DevicesResponse>(
            """{ "devices": [], "total": 0 }""", ServiceOptions());

        Assert.NotNull(wrapper);
        Assert.Empty(wrapper!.Devices!);
    }

    [Fact]
    public void InstallRecord_DecodesAndClassifiesErrors()
    {
        const string json = """
            [
              { "itemName": "Firefox", "currentStatus": "Installed" },
              { "itemName": "Chrome",  "currentStatus": "Error" }
            ]
            """;

        var installs = JsonSerializer.Deserialize<List<InstallRecord>>(json, ServiceOptions());

        Assert.NotNull(installs);
        Assert.Equal(2, installs!.Count);
        Assert.Equal("Firefox", installs[0].ItemName);
    }

    [Fact]
    public void InstallRecord_SurvivesOmittedFields()
    {
        var installs = JsonSerializer.Deserialize<List<InstallRecord>>(
            """[ { "itemName": "Firefox" } ]""", ServiceOptions());

        Assert.NotNull(installs);
        Assert.Equal("Firefox", installs![0].ItemName);
        Assert.Equal(string.Empty, installs[0].SerialNumber);
    }
}

/// <summary>
/// Guards the v1 path migration against silent regression.
///
/// A wrong path is the failure that looks like an auth problem in production —
/// a 404 surfaces as "couldn't fetch devices", which reads as a credential
/// issue — so it is worth catching at build time. This inspects the service's
/// own source rather than asserting constants against themselves.
/// </summary>
public class ReportMateEndpointTests
{
    private static string ServiceSource()
    {
        // Walk up from the test binary to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FleetMate.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var path = Path.Combine(
            dir!.FullName, "FleetMate.Core", "Services", "Reporting", "ReportMateService.cs");

        Assert.True(File.Exists(path), $"Expected the service source at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void EveryApiCallIsVersioned()
    {
        var source = ServiceSource();

        // Match the request paths, then assert none of them are unversioned.
        var calls = System.Text.RegularExpressions.Regex
            .Matches(source, @"GetAsync\(\$?""(?<path>/api/[^""?]*)")
            .Select(m => m.Groups["path"].Value)
            .ToList();

        Assert.NotEmpty(calls);

        var unversioned = calls.Where(p => !p.StartsWith("/api/v1/", StringComparison.Ordinal)).ToList();
        Assert.True(unversioned.Count == 0,
            $"Unversioned ReportMate paths found: {string.Join(", ", unversioned)}. " +
            "The live API serves /api/v1/... only; the old paths 404.");
    }

    [Fact]
    public void InstallsIsATopLevelCollection()
    {
        // v1 moved installs off the device subresource.
        var source = ServiceSource();

        Assert.Contains("/api/v1/installs", source);
        Assert.DoesNotContain("/api/devices/installs", source);
    }
}
