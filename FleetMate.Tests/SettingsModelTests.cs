using FleetMate.Core.Config;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The Settings models the WPF and WinUI pages share.</summary>
public class SettingsModelTests
{
    [Fact]
    public void EveryModuleHasARowInTabOrder()
    {
        var rows = ModuleRows.Build(new FleetMateConfig(), new HashSet<string>(), hasEnrollmentOrganizations: false);
        Assert.Equal(AppModules.All.Select(m => m.Tag), rows.Select(r => r.Tag));
    }

    [Fact]
    public void AShownModuleWithoutSetupOffersItsSettingsSection()
    {
        var rows = ModuleRows.Build(new FleetMateConfig(), new HashSet<string>(), hasEnrollmentOrganizations: false);
        var tickets = rows.Single(r => r.Tag == "Tickets");
        Assert.False(tickets.Configured);
        Assert.Equal("Authentication", tickets.ConfigurePane);
        Assert.Contains(AppModules.NeedsMessage("Tickets"), tickets.Subtitle);
        Assert.Null(rows.Single(r => r.Tag == "Manage").ConfigurePane);
        Assert.Equal("Manage", ModuleRows.ConfigurePane("Manage"));
        Assert.Equal("Enrollment", rows.Single(r => r.Tag == AppModules.Enrollment).ConfigurePane);
    }

    [Fact]
    public void AHiddenModuleOffersNothingAndSaysItCanBeShown()
    {
        var rows = ModuleRows.Build(new FleetMateConfig(), new HashSet<string> { "Devices" }, hasEnrollmentOrganizations: false);
        var devices = rows.Single(r => r.Tag == "Devices");
        Assert.False(devices.Shown);
        Assert.Null(devices.ConfigurePane);
        Assert.Equal("Show the Devices tab", devices.ToggleTip);
        Assert.Equal(AppModules.All.Single(m => m.Tag == "Devices").Subtitle, devices.Subtitle);
    }

    [Fact]
    public void AboutDescribesEachEdition()
    {
        Assert.Contains("tickets", AppAbout.Tagline(AppEdition.TicketsMate));
        Assert.Contains("fleet", AppAbout.Tagline(AppEdition.FleetMate));
        Assert.StartsWith("FleetMate ", AppAbout.VersionInfo(AppEdition.FleetMate, typeof(AppAbout).Assembly));
    }
}
