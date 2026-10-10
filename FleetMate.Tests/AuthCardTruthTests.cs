using FleetMate.Core.Services.Inventory;
using FleetMate.GUI.Views.Shared;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The Authentication cards say what is actually set up and in use.</summary>
public class AuthCardTruthTests
{
    [Fact]
    public async Task SnipeWithNoAddressReportsWhyRatherThanSuccess()
    {
        using var service = new SnipeService(baseUrl: null);
        Assert.Equal("no Snipe-IT address is set", await service.CheckAccessAsync());
    }

    [Fact]
    public async Task SnipeWithAnAddressButNoSignInNamesTheMissingAudience()
    {
        // With neither an audience nor a key, every request went out with no
        // Authorization header and Snipe-IT's 401 read as a refused sign-in.
        using var service = new SnipeService("https://snipe.example.edu");
        Assert.False(service.HasCredential);
        Assert.Contains("SnipeOidcAudience", await service.CheckAccessAsync());
        // And the asset list says so instead of reading as an empty inventory.
        var ex = await Assert.ThrowsAsync<SnipeException>(() => service.GetAssetsAsync());
        Assert.Contains("SnipeOidcAudience", ex.Message);
        Assert.Contains("SnipeOidcAudience", service.LastError);
    }

    [Fact]
    public void SnipeWithAnAudienceHasACredential()
    {
        using var service = new SnipeService("https://snipe.example.edu", oidcAudience: "api://snipe");
        Assert.True(service.HasCredential);
        Assert.Null(service.MissingCredentialReason);
    }

    [Fact]
    public void ReportMateNamesTheSettingsItStillNeeds()
    {
        Assert.Contains("ReportMateOidcAudience", SettingsPage.ReportMateNeeds(hasUrl: true));
        Assert.DoesNotContain("API URL", SettingsPage.ReportMateNeeds(hasUrl: true));
        Assert.Contains("API URL", SettingsPage.ReportMateNeeds(hasUrl: false));
    }
}
