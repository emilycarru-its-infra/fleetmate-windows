using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The command line's fallback once no silent token exists: the operator's az session, which
/// signs in at the console only when az asks for it and only at a real console.
/// </summary>
public class CommandLineSignInTests
{
    private const string Tenant = "00000000-0000-0000-0000-000000000000";
    private const string Scope = "api://example-search/.default";
    private const string Token = "{\"accessToken\":\"az-token\",\"expires_on\":4102444800}";

    private static Task<(string, DateTimeOffset)> BrokerRefuses(string scope, CancellationToken _) =>
        throw new EntraTokenException(scope, "no silent credential from the Windows sign-in broker (operating-system account: failed_to_acquire_token_silently_from_broker)");

    private sealed class Az
    {
        public readonly List<(string Command, bool Interactive)> Calls = new();
        public Queue<AzCliTokenSource.AzResult> Answers { get; } = new();

        public Task<AzCliTokenSource.AzResult> Run(IReadOnlyList<string> args, bool interactive, CancellationToken _)
        {
            Calls.Add((string.Join(' ', args), interactive));
            return Task.FromResult(Answers.Dequeue());
        }
    }

    [Fact]
    public async Task AnExistingAzSessionServesWithoutASignIn()
    {
        var az = new Az();
        az.Answers.Enqueue(new(0, Token, ""));
        var source = new AzCliTokenSource(az.Run, () => true);

        var (token, expires) = await source.AcquireAsync(Scope, Tenant);
        Assert.Equal("az-token", token);
        Assert.Equal(2100, expires.Year);
        var (command, interactive) = Assert.Single(az.Calls);
        Assert.Equal($"account get-access-token --scope {Scope} --output json --tenant {Tenant}", command);
        Assert.False(interactive);
    }

    [Fact]
    public async Task AnExpiredSessionSignsInAtTheConsoleThenAsksAgain()
    {
        var az = new Az();
        az.Answers.Enqueue(new(1, "", "ERROR: " + Entra("50078") + ": Presented multi-factor authentication has expired"));
        az.Answers.Enqueue(new(0, "", ""));
        az.Answers.Enqueue(new(0, Token, ""));
        var source = new AzCliTokenSource(az.Run, () => true);

        Assert.Equal("az-token", (await source.AcquireAsync(Scope, Tenant)).Token);
        Assert.Equal(3, az.Calls.Count);
        Assert.Equal($"login --scope {Scope} --output none --tenant {Tenant}", az.Calls[1].Command);
        Assert.True(az.Calls[1].Interactive);
    }

    [Fact]
    public async Task WithoutAConsoleItSaysWhatToRunInstead()
    {
        var az = new Az();
        az.Answers.Enqueue(new(1, "", "Please run 'az login' to setup account."));
        var source = new AzCliTokenSource(az.Run, () => false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.AcquireAsync(Scope, Tenant));
        Assert.Contains($"az login --scope {Scope}", ex.Message);
        Assert.Single(az.Calls);
    }

    [Fact]
    public async Task AnUnrelatedAzFailureNeverPromptsASignIn()
    {
        var az = new Az();
        az.Answers.Enqueue(new(1, "", "ERROR: network unreachable"));
        var source = new AzCliTokenSource(az.Run, () => true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.AcquireAsync(Scope, Tenant));
        Assert.Single(az.Calls);
    }

    [Fact]
    public async Task TheTokenSourceFallsBackToTheCommandLineAfterTheBroker()
    {
        var source = new EntraTokenSource(Tenant)
        {
            AcquireOverride = BrokerRefuses,
            CommandLineOverride = (scope, tenant, _) =>
            {
                Assert.Equal(Scope, scope);
                Assert.Equal(Tenant, tenant);
                return Task.FromResult(("cli-token", DateTimeOffset.UtcNow.AddHours(1)));
            },
        };

        Assert.Equal("cli-token", await source.GetTokenAsync(Scope));
    }

    [Fact]
    public async Task ACommandLineFailureStillNamesTheBrokersReason()
    {
        var source = new EntraTokenSource(Tenant)
        {
            AcquireOverride = BrokerRefuses,
            CommandLineOverride = (_, _, _) => throw new InvalidOperationException("az needs you to sign in again"),
        };

        var ex = await Assert.ThrowsAsync<EntraTokenException>(() => source.GetTokenAsync(Scope));
        Assert.Contains("failed_to_acquire_token_silently_from_broker", ex.Message);
        Assert.Contains("az needs you to sign in again", ex.Message);
    }

    [Theory]
    [InlineData("50076", ": Due to a configuration change you must use multi-factor", true)]
    [InlineData("70043", ": The refresh token has expired due to sign-in frequency", true)]
    [InlineData("", "Please run 'az login' to setup account.", true)]
    [InlineData("", "ERROR: The command failed with an unexpected error", false)]
    public void SignInIsOfferedOnlyForSignInErrors(string code, string text, bool expected) =>
        Assert.Equal(expected, AzCliTokenSource.NeedsSignIn((code.Length > 0 ? Entra(code) : "") + text));

    /// <summary>An Entra error code as az prints it, such as the multi-factor-expired one.</summary>
    private static string Entra(string number) => "AADSTS" + number;

    [Theory]
    [InlineData("api://x/.default & calc")]
    [InlineData("api://x/.default\" --debug")]
    [InlineData("api://x/.default|whoami")]
    [InlineData("api://x/%PATH%")]
    [InlineData("")]
    public async Task AScopeOutsideTheAllowlistNeverReachesAz(string scope)
    {
        var az = new Az();
        var source = new AzCliTokenSource(az.Run, () => true);
        await Assert.ThrowsAsync<ArgumentException>(() => source.AcquireAsync(scope, Tenant));
        Assert.Empty(az.Calls);
    }

    [Theory]
    [InlineData("contoso.onmicrosoft.com & calc")]
    [InlineData("-contoso.example")]
    [InlineData("tenant;whoami")]
    public async Task ATenantOutsideTheAllowlistNeverReachesAz(string tenant)
    {
        var az = new Az();
        var source = new AzCliTokenSource(az.Run, () => true);
        await Assert.ThrowsAsync<ArgumentException>(() => source.AcquireAsync(Scope, tenant));
        Assert.Empty(az.Calls);
    }

    [Theory]
    [InlineData(Tenant, true)]
    [InlineData("contoso.onmicrosoft.com", true)]
    [InlineData("organizations", true)]
    [InlineData("a b", false)]
    [InlineData("x\"y", false)]
    public void TenantsFollowTheSignInRule(string tenant, bool expected) =>
        Assert.Equal(expected, AzCliTokenSource.IsValidTenant(tenant));

    [Fact]
    public void AzIsNeverTakenFromARelativeOrEmptyPathEntry()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\Tools" : "/opt/tools";
        var seen = new List<string>();
        var path = string.Join(Path.PathSeparator, "", ".", "relative" + Path.DirectorySeparatorChar + "bin", root);
        var found = AzCliTokenSource.FindAz(path, candidate => { seen.Add(candidate); return candidate.StartsWith(root); }, Array.Empty<string>());

        Assert.NotNull(found);
        Assert.True(Path.IsPathFullyQualified(found!));
        Assert.StartsWith(root, found);
        Assert.All(seen, candidate => Assert.True(Path.IsPathFullyQualified(candidate), candidate));
    }

    [Fact]
    public void NoAzOnAFullyQualifiedPathMeansNone() =>
        Assert.Null(AzCliTokenSource.FindAz(string.Join(Path.PathSeparator, ".", ""), _ => true, Array.Empty<string>()));
}

public class WorkingDirectoryEnvTests
{
    [Fact]
    public void AWorkingDirectoryEnvCannotChooseTheTenantOrWhereTokensGo()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(file, new[]
            {
                "GRAPH_TENANT_ID=11111111-1111-1111-1111-111111111111",
                "GRAPH_CLIENT_ID=22222222-2222-2222-2222-222222222222",
                "REPORTMATE_URL=https://collector.example.test",
                "SNIPE_URL=https://collector.example.test",
                "TDX_BASE_URL=collector.example.test",
            });
            var config = new FleetMate.Core.Config.FleetMateConfig();
            FleetMate.Core.Config.FleetMateConfig.LoadEnvFile(file, config, fromWorkingDirectory: true);
            Assert.NotEqual("11111111-1111-1111-1111-111111111111", config.Graph?.TenantId);
            Assert.NotEqual("22222222-2222-2222-2222-222222222222", config.Graph?.ClientId);
            Assert.NotEqual("https://collector.example.test", config.ReportMateUrl);
            Assert.NotEqual("https://collector.example.test", config.SnipeUrl);

            var trusted = new FleetMate.Core.Config.FleetMateConfig();
            FleetMate.Core.Config.FleetMateConfig.LoadEnvFile(file, trusted);
            Assert.Equal("https://collector.example.test", trusted.ReportMateUrl);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("graph_tenant_id")]
    [InlineData(" Graph_Tenant_Id ")]
    [InlineData("GRAPH_TENANT_\u0130D")]
    [InlineData("GRAPH\u2010TENANT_ID")]
    [InlineData("REPORTMATE_URL")]
    [InlineData("TEAMS_API_SCOPE")]
    public void NoCasingOrLookAlikeGetsATenantOrHostPastTheAllowlist(string key)
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, key + "=11111111-1111-1111-1111-111111111111\n");
            var config = new FleetMate.Core.Config.FleetMateConfig();
            FleetMate.Core.Config.FleetMateConfig.LoadEnvFile(file, config, fromWorkingDirectory: true);
            Assert.Null(config.Graph?.TenantId);
            Assert.NotEqual("11111111-1111-1111-1111-111111111111", config.ReportMateUrl);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("snipe_api_key", "SNIPE_API_KEY")]
    [InlineData("  TDX_BEID ", "TDX_BEID")]
    [InlineData("GRAPH_TENANT_\u0130D", null)]
    [InlineData("K\u212AY", null)]
    [InlineData("A-B", null)]
    public void KeysAreNormalizedOnceOrRejected(string raw, string? expected) =>
        Assert.Equal(expected, FleetMate.Core.Config.FleetMateConfig.NormalizeEnvKey(raw));
}
