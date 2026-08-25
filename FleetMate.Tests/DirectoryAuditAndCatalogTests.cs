using FleetMate.Commands.Identity;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;
using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The audit filter.
///
/// directoryAudits answers a malformed $filter with an empty collection rather
/// than an error, and "no entries" is exactly what someone investigating a
/// deletion is afraid of seeing. So the filter is built by a pure function and
/// asserted here, instead of being trusted because the command ran without
/// throwing.
/// </summary>
public class DirectoryAuditFilterTests
{
    private static readonly DateTime Now = new(2026, 8, 24, 21, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoConstraintsMeansNoFilter()
    {
        // Not an empty string: an empty $filter is a query parameter Graph has
        // to interpret, and it rejects it.
        Assert.Null(EntraCommand.BuildAuditFilter(target: null, activity: null, days: 0, Now));
    }

    [Fact]
    public void DaysBecomesAnIso8601Floor()
    {
        var filter = EntraCommand.BuildAuditFilter(null, null, 7, Now);
        Assert.Equal("activityDateTime ge 2026-08-17T21:00:00Z", filter);
    }

    [Fact]
    public void AGuidTargetMatchesOnObjectId()
    {
        var filter = EntraCommand.BuildAuditFilter("2592274d-6c10-4daa-904e-49a47d94e5b0", null, 0, Now);
        Assert.Equal("targetResources/any(t: t/id eq '2592274d-6c10-4daa-904e-49a47d94e5b0')", filter);
    }

    [Fact]
    public void ANonGuidTargetMatchesOnDisplayName()
    {
        // Whoever is investigating usually has the name, not the id -- the id
        // died with the object.
        var filter = EntraCommand.BuildAuditFilter("Devices-Shared-Kiosk-Signage-A2003", null, 0, Now);
        Assert.Equal("targetResources/any(t: t/displayName eq 'Devices-Shared-Kiosk-Signage-A2003')", filter);
    }

    [Fact]
    public void ClausesCombineWithAnd()
    {
        var filter = EntraCommand.BuildAuditFilter("Some-Group", "Delete group", 30, Now);
        Assert.Equal(
            "activityDateTime ge 2026-07-25T21:00:00Z and "
            + "activityDisplayName eq 'Delete group' and "
            + "targetResources/any(t: t/displayName eq 'Some-Group')",
            filter);
    }

    [Fact]
    public void QuotesInAValueAreEscaped()
    {
        // An unescaped apostrophe terminates the OData string literal, and the
        // resulting filter is either rejected or silently matches nothing.
        var filter = EntraCommand.BuildAuditFilter("O'Brien's Laptop", null, 0, Now);
        Assert.Equal("targetResources/any(t: t/displayName eq 'O''Brien''s Laptop')", filter);
    }
}

/// <summary>
/// Settings Catalog matching.
///
/// Graph offers neither $search nor a useful $filter on this collection, so the
/// match runs locally and is the whole feature rather than a convenience over
/// the top of a server-side query.
/// </summary>
public class SettingsCatalogMatchTests
{
    private static SettingsCatalogDefinition Setting(
        string id, string? name = null, string? description = null, params string[] keywords) => new()
        {
            Id = id,
            DisplayName = name,
            Description = description,
            Keywords = keywords.ToList(),
        };

    [Fact]
    public void AnExactIdMatchesItself()
    {
        // The commonest use: someone holds an id Graph rejected and wants to know
        // whether it exists at all.
        var setting = Setting("device_vendor_msft_policy_config_windowslogon_hidefastuserswitching");
        Assert.True(GraphService.MatchesSettingQuery(setting, setting.Id));
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        Assert.True(GraphService.MatchesSettingQuery(Setting("device_vendor_msft_windowslogon"), "WindowsLogon"));
    }

    [Fact]
    public void ProseFieldsAreSearchedToo()
    {
        var setting = Setting("some_opaque_id", name: "Hide Lock Screen Clock", description: "Hides the clock.");
        Assert.True(GraphService.MatchesSettingQuery(setting, "lock screen"));
        Assert.True(GraphService.MatchesSettingQuery(setting, "clock"));
    }

    [Fact]
    public void KeywordsAreSearched()
    {
        Assert.True(GraphService.MatchesSettingQuery(Setting("x", keywords: "kiosk"), "kiosk"));
    }

    [Fact]
    public void EveryTermMustAppear()
    {
        // A multi-word query has to narrow. Matching on any term would return
        // most of a catalog with tens of thousands of entries.
        var setting = Setting("x", name: "Hide Lock Screen Clock");
        Assert.True(GraphService.MatchesSettingQuery(setting, "hide clock"));
        Assert.False(GraphService.MatchesSettingQuery(setting, "hide taskbar"));
    }

    [Fact]
    public void AnEmptyQueryMatchesEverything()
    {
        // `fleetmate intune settings --platform windows10` with no term is a
        // browse, not a search.
        Assert.True(GraphService.MatchesSettingQuery(Setting("anything"), null));
        Assert.True(GraphService.MatchesSettingQuery(Setting("anything"), "   "));
    }
}

/// <summary>
/// The audit actor, which is a union: an entry names a user or an application,
/// never both. Automation is always the application, and that is precisely the
/// case worth identifying.
/// </summary>
public class DirectoryAuditActorTests
{
    [Fact]
    public void AUserActorPrefersTheUpn()
    {
        var e = new DirectoryAuditEvent
        {
            InitiatedBy = new AuditInitiator
            {
                User = new AuditUser { DisplayName = "Ada Lovelace", UserPrincipalName = "ada@example.edu" },
            },
        };

        Assert.Equal("ada@example.edu", e.Actor);
        Assert.False(e.ActorIsApplication);
    }

    [Fact]
    public void AnApplicationActorIsFlagged()
    {
        var e = new DirectoryAuditEvent
        {
            InitiatedBy = new AuditInitiator
            {
                App = new AuditApp { DisplayName = "Some Lifecycle Service" },
            },
        };

        Assert.Equal("Some Lifecycle Service", e.Actor);
        Assert.True(e.ActorIsApplication);
    }

    [Fact]
    public void AnAbsentInitiatorIsUnknownRatherThanNull()
    {
        // Rendering a blank cell would read as "nobody did this".
        Assert.Equal("unknown", new DirectoryAuditEvent().Actor);
        Assert.False(new DirectoryAuditEvent().ActorIsApplication);
    }

    [Fact]
    public void TargetsJoinAndEmptyReadsAsADash()
    {
        var e = new DirectoryAuditEvent
        {
            TargetResources =
            [
                new AuditTargetResource { DisplayName = "Group-A" },
                new AuditTargetResource { Id = "id-only" },
            ],
        };

        Assert.Equal("Group-A, id-only", e.Targets);
        Assert.Equal("-", new DirectoryAuditEvent().Targets);
    }
}
