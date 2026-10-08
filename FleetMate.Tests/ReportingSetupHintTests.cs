using FleetMate.GUI.Views.Reporting;
using Xunit;

namespace FleetMate.Tests;

/// <summary>A Reporting page that cannot read names the FleetMate setting to fill in.</summary>
public class ReportingSetupHintTests
{
    [Fact]
    public void NoAddressNamesTheUrlAndAudience()
    {
        var hint = ReportingPage.SetupHint(hasUrl: false, audience: null);
        Assert.Contains("ReportMate API URL", hint);
        Assert.Contains("ReportMateOidcAudience", hint);
        Assert.DoesNotContain("passphrase", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoAudienceNamesTheAudience()
    {
        var hint = ReportingPage.SetupHint(hasUrl: true, audience: null);
        Assert.Contains("ReportMateOidcAudience", hint);
        Assert.DoesNotContain("API URL", hint);
    }

    [Fact]
    public void AFailedTokenPointsAtTheStatusCard()
    {
        Assert.Contains("Authentication Status", ReportingPage.SetupHint(hasUrl: true, audience: "api://reportmate"));
    }
}
