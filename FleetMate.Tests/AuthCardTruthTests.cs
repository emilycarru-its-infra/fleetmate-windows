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
    public void ReportMateNamesTheSettingsItStillNeeds()
    {
        Assert.Contains("ReportMateOidcAudience", SettingsPage.ReportMateNeeds(hasUrl: true));
        Assert.DoesNotContain("API URL", SettingsPage.ReportMateNeeds(hasUrl: true));
        Assert.Contains("API URL", SettingsPage.ReportMateNeeds(hasUrl: false));
    }
}
