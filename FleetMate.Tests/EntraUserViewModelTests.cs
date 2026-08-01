using System.Windows;
using FleetMate.Core.Models.Identity;
using FleetMate.GUI.Views.Identity;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The inspector's presentation of an Entra user.
///
/// The enabled badge gets the most attention here because it was wrong in two
/// opposite directions at once: the Mac rendered every user Disabled because
/// the field was never requested, and this codebase hardcoded "● Active" so the
/// badge was decoration rather than data. Both looked fine on screen.
/// </summary>
public class EntraUserBadgeTests
{
    private static EntraUserViewModel Vm(bool? accountEnabled) => new()
    {
        User = new EntraUser
        {
            Id = "1",
            DisplayName = "Ada Lovelace",
            UserPrincipalName = "ada@example.edu",
            AccountEnabled = accountEnabled,
        },
    };

    [Fact]
    public void EnabledAccountReadsActive()
    {
        Assert.Equal("● Active", Vm(true).AccountBadgeText);
    }

    [Fact]
    public void DisabledAccountReadsDisabled()
    {
        Assert.Equal("● Disabled", Vm(false).AccountBadgeText);
    }

    [Fact]
    public void UnknownIsItsOwnState()
    {
        // Null means "Graph did not tell us", which is a different fact from
        // "the account is disabled". Collapsing them is what let a projection
        // bug masquerade as a tenant full of deactivated users.
        Assert.Equal("● Unknown", Vm(null).AccountBadgeText);
    }

    [Fact]
    public void EachStateGetsItsOwnColour()
    {
        var enabled = Vm(true).AccountBadgeBrush.ToString();
        var disabled = Vm(false).AccountBadgeBrush.ToString();
        var unknown = Vm(null).AccountBadgeBrush.ToString();

        Assert.NotEqual(enabled, disabled);
        Assert.NotEqual(enabled, unknown);
        Assert.NotEqual(disabled, unknown);
    }
}

public class EntraUserPresentationTests
{
    private static EntraUserViewModel Vm(EntraUser user) => new() { User = user };

    private static EntraUser Full() => new()
    {
        Id = "8f1e2d3c",
        DisplayName = "Ada Lovelace",
        UserPrincipalName = "ada@example.edu",
        Mail = "ada@example.edu",
        GivenName = "Ada",
        Surname = "Lovelace",
        JobTitle = "Systems Analyst",
        Department = "IT Infrastructure",
        CompanyName = "Example University",
        OfficeLocation = "Building A",
        MobilePhone = "+1 555 0100",
        EmployeeId = "E1234",
        UsageLocation = "CA",
        AccountEnabled = true,
        OnPremisesSamAccountName = "alovelace",
        OnPremisesSyncEnabled = true,
    };

    [Theory]
    [InlineData("Ada Lovelace", "AL")]
    [InlineData("ada.lovelace", "AL")]
    [InlineData("Ada", "A")]
    [InlineData("", "?")]
    public void InitialsTakeUpToTwoLetters(string name, string expected)
    {
        Assert.Equal(expected, Vm(new EntraUser { DisplayName = name }).Initials);
    }

    [Fact]
    public void SubtitleJoinsJobTitleAndDepartment()
    {
        Assert.Equal("Systems Analyst · IT Infrastructure", Vm(Full()).Subtitle);
    }

    [Fact]
    public void SubtitleOmitsMissingPartsWithoutStrandedSeparators()
    {
        var user = new EntraUser { DisplayName = "Ada", JobTitle = "Analyst" };
        Assert.Equal("Analyst", Vm(user).Subtitle);

        var bare = new EntraUser { DisplayName = "Ada" };
        Assert.Equal("", Vm(bare).Subtitle);
    }

    [Fact]
    public void SectionsMirrorTheAzurePortal()
    {
        // Someone who knows the portal should find the same field in the same
        // place here, so the section names are part of the contract.
        var titles = Vm(Full()).Sections.Select(s => s.Title).ToList();

        Assert.Equal(
            new[] { "Identity", "Job information", "Contact information", "Settings", "On-premises" },
            titles);
    }

    [Fact]
    public void EmptySectionsAreHidden()
    {
        // An estate that does not sync from on-premises should not see a panel
        // full of dashes.
        var cloudOnly = new EntraUser
        {
            Id = "1",
            DisplayName = "Ada",
            UserPrincipalName = "ada@example.edu",
        };

        var onPrem = Vm(cloudOnly).Sections.Single(s => s.Title == "On-premises");
        Assert.Equal(Visibility.Collapsed, onPrem.Visibility);

        var identity = Vm(cloudOnly).Sections.Single(s => s.Title == "Identity");
        Assert.Equal(Visibility.Visible, identity.Visibility);
    }

    [Fact]
    public void PopulatedSectionsAreVisible()
    {
        Assert.All(Vm(Full()).Sections, s => Assert.Equal(Visibility.Visible, s.Visibility));
    }

    [Fact]
    public void MissingValuesRenderAsAnEmDash()
    {
        // Blank reads as a layout gap; a dash reads as "not set".
        var row = new UserPropertyRow { Label = "Employee ID", Value = null };

        Assert.Equal("—", row.DisplayValue);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WhitespaceCountsAsMissing(string value)
    {
        Assert.Equal("—", new UserPropertyRow { Label = "X", Value = value }.DisplayValue);
    }

    [Fact]
    public void PresentValuesAreShownVerbatim()
    {
        Assert.Equal("E1234", new UserPropertyRow { Label = "Employee ID", Value = "E1234" }.DisplayValue);
    }

    [Fact]
    public void ValueBrushResolvesWithoutARunningApplication()
    {
        // Application.Current is null here, as it is in the designer. A getter
        // that throws in that case takes the whole pane down instead of
        // degrading to a readable default.
        Assert.NotNull(new UserPropertyRow { Label = "X", Value = "set" }.ValueBrush);
        Assert.NotNull(new UserPropertyRow { Label = "X", Value = null }.ValueBrush);
    }

    [Fact]
    public void IdentitySectionCarriesTheObjectId()
    {
        // The object ID is what you need to file a ticket about a user, so it
        // has to be visible rather than implied.
        var identity = Vm(Full()).Sections.Single(s => s.Title == "Identity");

        Assert.Contains(identity.Rows, r => r.Label == "Object ID" && r.Value == "8f1e2d3c");
    }

    [Fact]
    public void SettingsSectionReportsTheEnabledFlag()
    {
        var settings = Vm(Full()).Sections.Single(s => s.Title == "Settings");

        Assert.Contains(settings.Rows, r => r.Label == "Account enabled" && r.Value == "True");
    }
}
