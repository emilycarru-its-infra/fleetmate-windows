using FleetMate.Core.Config;
using FleetMate.GUI.Views.Shared;
using Xunit;

namespace FleetMate.Tests;

public class AppEditionTests
{
    [Fact]
    public void MissingOrUnknownMetadataIsFleetMate()
    {
        Assert.Same(AppEdition.FleetMate, AppEdition.FromMetadata(null));
        Assert.Same(AppEdition.FleetMate, AppEdition.FromMetadata("Something"));
        Assert.Same(AppEdition.TicketsMate, AppEdition.FromMetadata("TicketsMate"));
        Assert.Same(AppEdition.TicketsMate, AppEdition.FromMetadata("ticketsmate"));
    }

    [Fact]
    public void TheTestHostIsFleetMate()
    {
        // The test host carries no edition metadata, like the CLI.
        Assert.Same(AppEdition.FleetMate, AppEdition.Current);
        Assert.False(AppEdition.Current.IsTicketsOnly);
    }

    [Fact]
    public void FleetMateKeepsItsSettingsLocations()
    {
        Assert.Equal(@"SOFTWARE\FleetMate", AppEdition.FleetMate.UserRegistryPath);
        Assert.Equal(@"SOFTWARE\Policies\FleetMate", AppEdition.FleetMate.PolicyRegistryPath);
        Assert.EndsWith(".fleetmate", AppEdition.FleetMate.UserDirectory);
    }

    [Fact]
    public void TicketsMateKeepsSettingsOfItsOwn()
    {
        Assert.True(AppEdition.TicketsMate.IsTicketsOnly);
        Assert.Equal(@"SOFTWARE\TicketsMate", AppEdition.TicketsMate.UserRegistryPath);
        Assert.Equal(@"SOFTWARE\Policies\TicketsMate", AppEdition.TicketsMate.PolicyRegistryPath);
        Assert.EndsWith(".ticketsmate", AppEdition.TicketsMate.UserDirectory);
        Assert.EndsWith("TicketsMate", AppEdition.TicketsMate.LocalAppDataDirectory);
    }

    [Fact]
    public void TicketsMateLogsApartFromFleetMate()
    {
        Assert.Equal(Path.Combine(AppEdition.TicketsMate.LocalAppDataDirectory, "Logs"), AppEdition.TicketsMate.LogDirectory);
        Assert.EndsWith(Path.Combine("TicketsMate", "Logs"), AppEdition.TicketsMate.LogDirectory);
        Assert.Equal(AppEdition.FleetMate.UserDirectory, AppEdition.FleetMate.LogDirectory);
    }

    [Fact]
    public void TicketsMateCarriesTheTicketsTabAlone()
    {
        Assert.Equal(new[] { "Tickets" }, AppEdition.TicketsMate.Tabs(MainWindow.TabOrder));
        Assert.Equal(MainWindow.TabOrder, AppEdition.FleetMate.Tabs(MainWindow.TabOrder));
    }
}
