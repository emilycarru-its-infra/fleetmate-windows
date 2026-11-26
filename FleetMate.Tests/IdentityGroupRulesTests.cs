using FleetMate.Core.Identity;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;
using Xunit;

namespace FleetMate.Tests;

/// <summary>How the Identity tab shows device groups, as on FleetMate for Mac. Synthetic values only.</summary>
public class IdentityGroupRulesTests
{
    private static readonly EntraGroup Lab = new() { DisplayName = "Devices-Lab-North", SecurityEnabled = true };
    private static readonly IntuneDevice[] Members =
    {
        new() { DeviceName = "LAB-01", OperatingSystem = "Windows" },
        new() { DeviceName = "LAB-02", OperatingSystem = "macOS" },
    };

    [Fact]
    public void ADeviceNameFindsTheGroupsItIsIn()
    {
        Assert.True(IdentitySearch.GroupMatches(Lab, "lab-02", Members));
        Assert.False(IdentitySearch.GroupMatches(Lab, "lab-02"));
        Assert.False(IdentitySearch.GroupMatches(Lab, "studio", Members));
    }

    [Fact]
    public void FilteringNarrowsMembersUnlessTheGroupMatchedByName()
    {
        Assert.Equal(new[] { "LAB-02" }, IdentitySearch.VisibleMembers(Members, "02").Select(d => d.DeviceName));
        Assert.Equal(2, IdentitySearch.VisibleMembers(Members, "north").Count);
        Assert.Equal(2, IdentitySearch.VisibleMembers(Members, null).Count);
    }

    [Fact]
    public void BadgesNameTheGroupKinds()
    {
        var group = new EntraGroup { SecurityEnabled = true, MailEnabled = true, GroupTypes = new() { "Unified", "DynamicMembership" } };
        Assert.Equal(new[] { "Security", "Mail", "M365", "Dynamic" }, IdentitySearch.Badges(group));
        Assert.Empty(IdentitySearch.Badges(new EntraGroup()));
    }

    [Theory]
    [InlineData("Windows", "Windows")]
    [InlineData("macOS", "macOS")]
    [InlineData("iPadOS", "Mobile")]
    [InlineData("Android", "Mobile")]
    [InlineData(null, "Other")]
    public void DevicesGroupByPlatformForTheirIcon(string? os, string platform) =>
        Assert.Equal(platform, IdentitySearch.Platform(os));

    [Fact]
    public void TheCountSaysHowManyShow()
    {
        Assert.Equal("12 groups", IdentitySearch.CountText(12, 12));
        Assert.Equal("1 group", IdentitySearch.CountText(1, 1));
        Assert.Equal("3 of 12", IdentitySearch.CountText(3, 12));
    }
}
