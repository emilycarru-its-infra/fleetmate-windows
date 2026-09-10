using System.Net;
using FleetMate.Core.Services.Reporting;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The CLI-first path: when <c>reportmateutil</c> is installed, every read goes
/// through it with the same credential the HTTP path would send, and the
/// API's JSON decodes identically. When the binary cannot launch, HTTP takes
/// over.
/// </summary>
public class ReportMateCliTests
{
    /// <summary>A scripted CLI: records the call and answers with canned output.</summary>
    private sealed class ScriptedCli
    {
        public List<string> Arguments { get; } = new();
        public Dictionary<string, string> Credentials { get; } = new();
        public ReportMateCli.CliOutput Output { get; set; } = new(true, 0, "{}", "");

        public ReportMateCli Build(string path = @"C:\Program Files\ReportMate\reportmateutil.exe") =>
            new(path, (_, args, creds, _) =>
            {
                Arguments.Clear();
                Arguments.AddRange(args);
                Credentials.Clear();
                foreach (var (k, v) in creds) Credentials[k] = v;
                return Task.FromResult(Output);
            });
    }

    /// <summary>An HTTP transport that records the request and answers with a fixed body.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public string Body { get; set; } = "[]";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public void Locate_HonoursPinnedBinaryAndRejectsMissing()
    {
        var existing = typeof(ReportMateCliTests).Assembly.Location;
        Assert.Equal(existing, ReportMateCli.Locate(name => name == "REPORTMATE_CLI" ? existing : null)?.Path);
        Assert.Null(ReportMateCli.Locate(name => name == "REPORTMATE_CLI" ? @"C:\nonexistent\reportmateutil.exe" : null));
        Assert.Null(ReportMateCli.Locate(name => name == "REPORTMATE_CLI" ? "" : null));
    }

    [Fact]
    public async Task Devices_GoThroughTheCliWithTheServiceCredential()
    {
        var cli = new ScriptedCli
        {
            Output = new(true, 0, """{"devices":[{"serialNumber":"SER-LAB01","deviceName":"LAB-01"}],"total":1,"offset":0,"limit":100}""", ""),
        };
        using var service = new ReportMateService("https://reportmate.example.edu/", "secret", 5, null, cli.Build(), new StubHandler());
        Assert.True(service.UsesCli);

        var devices = await service.GetDevicesAsync();

        Assert.Equal(new[] { "SER-LAB01" }, devices.Select(d => d.SerialNumber));
        Assert.Equal(new[] { "devices", "--limit", "100", "--offset", "0", "--output", "json" }, cli.Arguments);
        Assert.Equal("https://reportmate.example.edu", cli.Credentials["REPORTMATE_API_URL"]);
        Assert.Equal("secret", cli.Credentials["REPORTMATE_PASSPHRASE"]);
        Assert.False(cli.Credentials.ContainsKey("REPORTMATE_TOKEN"));
    }

    [Fact]
    public async Task FleetAddresses_ComeFromTheNetworkReport()
    {
        var cli = new ScriptedCli
        {
            Output = new(true, 0, """
                [{"serialNumber":"SER-LAB01","deviceName":"LAB-01",
                  "raw":{"activeConnection":{"ipAddress":"10.1.2.3"},
                         "interfaces":[{"name":"Ethernet","ipAddresses":["fe80::1","10.1.2.3"],"isActive":true}]}},
                 {"serialNumber":"SER-LAB02","deviceName":"LAB-02","raw":{"interfaces":[]}}]
                """, ""),
        };
        using var service = new ReportMateService("https://reportmate.example.edu", "secret", 5, null, cli.Build(), new StubHandler());

        var addresses = await service.GetFleetAddressesAsync();

        Assert.Equal("10.1.2.3", addresses["SER-LAB01"].PrimaryIp);
        Assert.False(addresses.ContainsKey("SER-LAB02"));
        Assert.Equal(new[] { "module", "network", "--output", "json" }, cli.Arguments);
    }

    [Fact]
    public async Task ApiNotFoundThroughTheCli_IsNull()
    {
        var cli = new ScriptedCli { Output = new(true, 1, "", "Error: GET /api/v1/device/NOPE/installs/log -> 404 Not Found: {}") };
        using var service = new ReportMateService("https://reportmate.example.edu", "secret", 5, null, cli.Build(), new StubHandler());

        Assert.Null(await service.GetDeviceLogAsync("NOPE"));
    }

    [Fact]
    public async Task ApiRefusalThroughTheCli_DoesNotFallBackToHttp()
    {
        var cli = new ScriptedCli { Output = new(true, 1, "", "Error: GET /api/v1/installs -> 403 Forbidden: scope") };
        var http = new StubHandler();
        using var service = new ReportMateService("https://reportmate.example.edu", "secret", 5, null, cli.Build(), http);

        // GetInstallsAsync logs and returns the (empty) cache rather than throwing.
        var installs = await service.GetInstallsAsync();

        Assert.Empty(installs);
        Assert.Null(http.Last);
    }

    [Fact]
    public async Task UnlaunchableCli_FallsBackToHttp()
    {
        var cli = new ScriptedCli { Output = new(false, -1, "", "could not launch") };
        var http = new StubHandler { Body = "[]" };
        using var service = new ReportMateService("https://reportmate.example.edu", "secret", 5, null, cli.Build(), http);

        var installs = await service.GetInstallsAsync();

        Assert.Empty(installs);
        Assert.NotNull(http.Last);
        Assert.Equal("/api/v1/installs", http.Last!.RequestUri!.AbsolutePath);
        Assert.Equal("secret", http.Last.Headers.GetValues("X-Client-Passphrase").Single());
    }
}
