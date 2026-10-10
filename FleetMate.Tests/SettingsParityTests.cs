using FleetMate.Core.Config;
using FleetMate.Core.Services;
using FleetMate.GUI.Views.Shared;
using Xunit;

namespace FleetMate.Tests;

/// <summary>Enabled Modules, text size and the az/gh sign-in commands from Settings.</summary>
public class SettingsParityTests
{
    [Fact]
    public void ModulesFollowTheTabBar() =>
        Assert.Equal(MainWindow.TabOrder, AppModules.Tabs.Select(m => m.Tag));

    [Fact]
    public void HiddenModulesRoundTripInTabBarOrder()
    {
        var hidden = AppModules.ParseHidden("tickets; Devices,unknown");
        Assert.Equal(new[] { "Devices", "Tickets" }, hidden.OrderBy(t => Array.IndexOf(MainWindow.TabOrder, t)));
        Assert.Equal("Devices;Tickets", AppModules.FormatHidden(hidden));
        Assert.Empty(AppModules.ParseHidden(null));
        Assert.Empty(AppModules.ParseHidden("  "));
    }

    [Fact]
    public void EveryTabCanNeverBeHidden()
    {
        Assert.Empty(AppModules.ParseHidden(string.Join(";", MainWindow.TabOrder)));

        var allButTickets = AppModules.ParseHidden(string.Join(";", MainWindow.TabOrder.Where(t => t != "Tickets")));
        Assert.Same(allButTickets, AppModules.WithModule(allButTickets, "Tickets", shown: false));
        Assert.Equal(new[] { "Tickets" }, AppModules.Visible(MainWindow.TabOrder, allButTickets));
    }

    [Fact]
    public void ShowingAndHidingAModule()
    {
        var hidden = AppModules.WithModule(AppModules.ParseHidden(null), "Manage", shown: false);
        Assert.Contains("Manage", hidden);
        Assert.DoesNotContain("Manage", AppModules.Visible(MainWindow.TabOrder, hidden));
        Assert.Empty(AppModules.WithModule(hidden, "manage", shown: true));
    }

    [Fact]
    public void ModulesNeedTheirEndpoint()
    {
        var config = new FleetMateConfig();
        Assert.False(AppModules.IsConfigured("Inventory", config));
        Assert.True(AppModules.IsConfigured("Manage", config));
        Assert.True(AppModules.IsConfigured("Development", config));
        config.SnipeUrl = "https://inventory.example.com";
        Assert.True(AppModules.IsConfigured("Inventory", config));
    }

    [Theory]
    [InlineData(null, 1.0)]
    [InlineData("1.15", 1.15)]
    [InlineData("0.5", 0.9)]
    [InlineData("9", 1.6)]
    [InlineData("1.13", 1.15)]
    [InlineData("nonsense", 1.0)]
    public void TextScaleIsClampedToTheMacRange(string? stored, double expected) =>
        Assert.Equal(expected, AppTextScale.Parse(stored), precision: 3);

    [Fact]
    public void TextScaleReadoutAndStorage()
    {
        Assert.Equal("115%", AppTextScale.Label(1.15));
        Assert.Equal("1.15", AppTextScale.Format(1.15));
        Assert.True(AppTextScale.IsDefault(1.0));
        Assert.False(AppTextScale.IsDefault(1.05));
    }

    [Theory]
    [InlineData(null, "login")]
    [InlineData("", "login")]
    [InlineData("00000000-0000-0000-0000-000000000000", "login --tenant 00000000-0000-0000-0000-000000000000")]
    [InlineData("contoso.onmicrosoft.com", "login --tenant contoso.onmicrosoft.com")]
    [InlineData("x & calc", "login")]
    [InlineData("-o json", "login")]
    public void AzLoginOnlyPassesATenantThatLooksLikeOne(string? tenant, string expected) =>
        Assert.Equal(expected, CliSignIn.AzLoginArguments(tenant));
}
