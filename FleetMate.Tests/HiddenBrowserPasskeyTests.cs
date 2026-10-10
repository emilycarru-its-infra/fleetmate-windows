using FleetMate.Core.Services;
using Microsoft.Identity.Client;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// A hidden sign-in must never raise the operating system's passkey dialog,
/// and a broker refusal must say why.
/// </summary>
public class HiddenBrowserPasskeyTests
{
    [Fact]
    public void WebAuthnIsRefusedForBothCeremonies()
    {
        var script = EntraWebSignIn.WebAuthnBlockScript;
        Assert.Contains("CredentialsContainer.prototype, 'get'", script);
        Assert.Contains("CredentialsContainer.prototype, 'create'", script);
        Assert.Contains("NotAllowedError", script);
        Assert.Contains("isUserVerifyingPlatformAuthenticatorAvailable", script);
        Assert.Contains("isConditionalMediationAvailable", script);
    }

    [Fact]
    public void TheBrokersRefusalCarriesItsDetail()
    {
        var ex = new MsalServiceException("failed_to_acquire_token_silently_from_broker",
            "Failed to acquire token silently. Wam Status: 3, Error code: 3399614476\nmore");
        var detail = EntraTokenSource.BrokerDetail(ex);
        Assert.StartsWith("failed_to_acquire_token_silently_from_broker", detail);
        Assert.Contains("Error code: 3399614476", detail);
        Assert.DoesNotContain("more", detail);
    }
}
