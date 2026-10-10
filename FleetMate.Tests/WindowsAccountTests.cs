using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The signed-in address is read from several places in turn, so a broker
/// that will not surface the operating-system account (a disconnected remote
/// session) no longer leaves it unknown when the logon itself names it.
/// </summary>
public class WindowsAccountTests
{
    private static WindowsAccount.Source Gives(string name, string? upn) =>
        new(name, _ => Task.FromResult(upn));

    private static WindowsAccount.Source Throws(string name) =>
        new(name, _ => throw new InvalidOperationException("failed_to_acquire_token_silently_from_broker"));

    [Fact]
    public async Task TheLogonComesBeforeTheBroker()
    {
        var (upn, from) = await WindowsAccount.FirstAsync(new[]
        {
            Gives("Windows logon", "Operator@Example.EDU"),
            Gives("sign-in broker", "someone-else@example.edu"),
        });
        Assert.Equal("operator@example.edu", upn);
        Assert.Equal("Windows logon", from);
    }

    [Fact]
    public async Task ABrokerWithNoAccountFallsThroughToTheRegistry()
    {
        var (upn, from) = await WindowsAccount.FirstAsync(new[]
        {
            Gives("Windows logon", null),
            Throws("sign-in broker"),
            Gives("registry", "operator@example.edu"),
        });
        Assert.Equal("operator@example.edu", upn);
        Assert.Equal("registry", from);
    }

    [Fact]
    public async Task ADownLevelNameIsNotAnAddress()
    {
        var (upn, from) = await WindowsAccount.FirstAsync(new[]
        {
            Gives("Windows logon", @"AzureAD\Operator"),
            Gives("sign-in broker", "operator@example.edu"),
        });
        Assert.Equal("operator@example.edu", upn);
        Assert.Equal("sign-in broker", from);
    }

    [Fact]
    public async Task OnlyWhenEverySourceIsEmptyIsTheAddressUnknown()
    {
        var (upn, from) = await WindowsAccount.FirstAsync(new[]
        {
            Gives("Windows logon", ""),
            Throws("sign-in broker"),
            Gives("registry", null),
        });
        Assert.Null(upn);
        Assert.Null(from);
    }

    [Fact]
    public void TheBrokerSitsBetweenTheLogonAndTheRegistry()
    {
        var names = WindowsAccount.Sources(_ => Task.FromResult<string?>(null)).Select(s => s.Name).ToArray();
        Assert.Equal(new[] { "Windows logon", "sign-in broker", "registry" }, names);
        Assert.Equal(new[] { "Windows logon", "registry" }, WindowsAccount.Sources(null).Select(s => s.Name).ToArray());
    }

    [Fact]
    public async Task TheTokenSourceKeepsAnOverriddenAddressForTheHint()
    {
        var source = new EntraTokenSource("00000000-0000-0000-0000-000000000000") { UpnOverride = () => " Operator@Example.EDU " };
        Assert.Equal("operator@example.edu", await source.ResolveAccountUpnAsync());
        Assert.Equal("operator@example.edu", source.WindowsUpn());
    }

    [Theory]
    [InlineData("https://login.microsoft.com/00000000-0000-0000-0000-000000000000/fido/get", true)]
    [InlineData("https://login.microsoftonline.com/common/fido/create", true)]
    [InlineData("https://login.microsoftonline.com/common/oauth2/v2.0/authorize", false)]
    [InlineData("https://example.edu/fido/get", false)]
    [InlineData(null, false)]
    public void ThePasskeyPageIsRecognised(string? url, bool expected) =>
        Assert.Equal(expected, EntraWebSignIn.IsPasskeyPage(url));

    [Fact]
    public void ThePasskeyFailureSaysWhere()
    {
        var reason = EntraWebSignIn.PasskeyReason("https://login.microsoft.com/tenant/fido/get?x=1");
        Assert.Contains("passkey", reason);
        Assert.Contains("login.microsoft.com/tenant/fido/get", reason);
        Assert.True(EntraWebSignIn.PasskeyGiveUp < EntraWebSignIn.HeadlessTimeout);
    }
}
