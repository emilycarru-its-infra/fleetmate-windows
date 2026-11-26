using FleetMate.Core.Development;
using FleetMate.Core.Devices;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Reporting;
using FleetMate.Core.Shared;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The Devices logic both Windows apps share: list columns and counts, which
/// actions a selection gets and how they are worded, the detail sections,
/// the compliance report, the graph cards and the demo fixtures.
/// </summary>
public class DevicesCoreTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static IntuneDevice Intune(string id, string os, string? serial = null, string compliance = "compliant") => new()
    {
        Id = id, DeviceName = "Device " + id, OperatingSystem = os, SerialNumber = serial ?? "SN-" + id, ComplianceState = compliance,
    };

    private static DeviceListRow Row(IntuneDevice? intune, AutopilotDevice? autopilot = null) => new(intune, autopilot);

    // MARK: - Columns

    [Fact]
    public void Columns_DefaultsShowEveryRequiredColumnAndNoOptionalOne()
    {
        var visible = DeviceListColumns.Visible(null).Select(c => c.Header).ToList();
        Assert.Equal(DeviceListColumns.All.Where(c => !c.Optional).Select(c => c.Header), visible);
    }

    [Fact]
    public void Columns_LayoutShowsChosenOptionalColumnsInSavedOrder()
    {
        var order = DeviceListColumns.Move(DeviceListColumns.DefaultOrder, "Serial", -1);
        var layout = DeviceListColumns.Layout(new[] { "Model", "Name" }, order);
        Assert.Equal(new[] { "Model" }, layout.Shown);
        var visible = DeviceListColumns.Visible(layout).Select(c => c.Header).ToList();
        Assert.Equal("Serial", visible[0]);
        Assert.Contains("Model", visible);
        Assert.DoesNotContain("Manufacturer", visible);
    }

    [Fact]
    public void Columns_MoveStopsAtEitherEnd()
    {
        var order = new[] { "A", "B", "C" };
        Assert.Equal(order, DeviceListColumns.Move(order, "A", -1));
        Assert.Equal(order, DeviceListColumns.Move(order, "C", +1));
        Assert.Equal(new[] { "B", "A", "C" }, DeviceListColumns.Move(order, "A", +1));
    }

    [Fact]
    public void Columns_SortByTextIgnoringCaseAndByLastSyncTime()
    {
        var a = Intune("1", "Windows"); a.DeviceName = "beta"; a.LastSyncDateTime = Now.AddDays(-1);
        var b = Intune("2", "Windows"); b.DeviceName = "Alpha"; b.LastSyncDateTime = Now;
        var rows = new[] { Row(a), Row(b) };
        Assert.Equal(new[] { "Alpha", "beta" }, DeviceListColumns.Sort(rows, "Name", false).Select(r => r.NameText));
        Assert.Equal(new[] { "2", "1" }, DeviceListColumns.Sort(rows, "Last Sync", true).Select(r => r.Id));
        // Activation Lock cannot sort: the order stays.
        Assert.Equal(new[] { "1", "2" }, DeviceListColumns.Sort(rows, "Activation Lock", false).Select(r => r.Id));
    }

    [Fact]
    public void Columns_OfferOnlyOptionalColumnsSomeRowFills()
    {
        var rows = new List<DeviceListRow> { Row(Intune("1", "Windows")) };
        Assert.False(DeviceListColumns.CanFill("Model", rows));
        Assert.False(DeviceListColumns.CanFill("Migration", rows));
        Assert.True(DeviceListColumns.CanFill("Model", new List<DeviceListRow>()));
        rows[0].Intune!.Model = "Laptop";
        Assert.True(DeviceListColumns.CanFill("Model", rows));
    }

    // MARK: - Text

    [Fact]
    public void Count_SaysWhatTheListShows()
    {
        Assert.Equal("12 devices", DeviceListText.Count(12, 12, null, 0));
        Assert.Equal("3 of 12 devices", DeviceListText.Count(3, 12, null, 0));
        var lookup = new SerialLookup.Result(new List<DeviceListRow>(), 4, new List<string> { "X" });
        Assert.Equal("4 of 5 looked-up serials found, 1 unknown", DeviceListText.Count(5, 12, lookup, 5));
    }

    [Fact]
    public void SelectedNames_ListThreeThenSummarise()
    {
        var rows = Enumerable.Range(1, 4).Select(i => Row(Intune(i.ToString(), "Windows"))).ToList();
        Assert.Equal("Device 1, Device 2, Device 3", DeviceListText.SelectedNames(rows.Take(3).ToList()));
        Assert.Equal("Device 1, Device 2 and 2 more...", DeviceListText.SelectedNames(rows));
        Assert.Equal("Filters (2)", DeviceListText.FiltersButton(2));
        Assert.Equal("Serials", DeviceListText.SerialsButton(null));
        Assert.Equal("1 serial", DeviceListText.ParsedSerials(1));
    }

    // MARK: - Action sections

    [Fact]
    public void Sections_IntuneNeedsEveryDeviceEnrolled_WindowsCardsNeedEveryDeviceWindows()
    {
        var windows = Row(Intune("1", "Windows"));
        var mac = Row(Intune("2", "macOS"));
        var registered = Row(null, new AutopilotDevice { Id = "ap", SerialNumber = "SN-9" });

        var both = DeviceActionSections.For(new[] { windows, mac }, null);
        Assert.True(both.Intune);
        Assert.False(both.WindowsIntune);
        Assert.True(both.WipeMixedPlatforms && both.WipeWindowsOptions && both.WipeUnlockCode && both.WipeObliteration);

        var mixed = DeviceActionSections.For(new[] { windows, registered }, null);
        Assert.False(mixed.Intune);
        Assert.True(mixed.Offboard);
        Assert.False(mixed.AutopilotSync);

        Assert.True(DeviceActionSections.For(Array.Empty<DeviceListRow>(), null).NoActions);
    }

    [Fact]
    public void Sections_AutopilotCardsNeedEveryDeviceRegistered_UnassignNeedsUsers()
    {
        var a = Row(Intune("1", "Windows"), new AutopilotDevice { Id = "a", UserPrincipalName = "a@example.com" });
        var b = Row(Intune("2", "Windows"), new AutopilotDevice { Id = "b" });
        var s = DeviceActionSections.For(new[] { a, b }, null);
        Assert.True(s.AutopilotSync && s.AutopilotGroupTag && s.AutopilotDelete);
        Assert.False(s.AutopilotUnassignUser);
        Assert.True(s.IntuneHeaderShown);
    }

    [Fact]
    public void Sections_AppleCardsNeedOneOrganization()
    {
        var orgs = DeviceDemoData.AppleOrgs(Now);
        var rows = AppleOrgJoin.Enrich(DeviceListJoin.Merge(DeviceDemoData.Devices(Now), DeviceDemoData.Autopilot(Now)), orgs);
        var macs = rows.Where(r => r.Apple != null && r.Intune != null).ToList();
        var s = DeviceActionSections.For(macs, orgs[0].Profile);
        Assert.True(s.AppleAssign);
        Assert.True(s.AppleRelease);
        Assert.True(s.AppleHeaderShown);
        Assert.False(DeviceActionSections.For(macs, null).AppleAssign);
    }

    // MARK: - Action text

    [Fact]
    public void ActionText_ConfirmsOnlyWhatChangesADevice()
    {
        Assert.Null(DeviceActionText.Confirm(DeviceActionKind.Sync, 2));
        var wipe = DeviceActionText.Confirm(DeviceActionKind.Wipe, 2, keepUserData: true)!;
        Assert.Equal("Confirm Wipe", wipe.Title);
        Assert.Contains("keeping user data", wipe.Message);
        Assert.Equal("Successfully synced 3 device(s)", DeviceActionText.Result(DeviceActionKind.Sync, 3, 0));
        Assert.Equal("Synced 2, 1 failed", DeviceActionText.Result(DeviceActionKind.Sync, 2, 1));
        Assert.Equal("Offboarded 2 device(s), 1 with failures", DeviceActionText.Result(DeviceActionKind.Offboard, 2, 1));
        Assert.Equal("Triggered Editor reinstall on 1 device(s)", DeviceActionText.Result(DeviceActionKind.ReinstallApp, 1, 0, "Editor"));
    }

    [Fact]
    public void ActionText_AutopilotWording()
    {
        Assert.Equal("Do you want to clear the group tag on 1 Autopilot identity?",
            DeviceActionText.AutopilotConfirm(AutopilotAction.SetGroupTag, 1, "").Message);
        Assert.Equal("Do you want to set group tag \"Lab\" on 2 Autopilot identities?",
            DeviceActionText.AutopilotConfirm(AutopilotAction.SetGroupTag, 2, "Lab").Message);
        Assert.Equal("Assigning user: 1 done, 1 failed — denied",
            DeviceActionText.AutopilotResult("Assigning user", 2, new[] { "denied" }));
        Assert.False(DeviceActionText.IsUserPrincipalName("someone"));
    }

    [Fact]
    public void MigrationDeadline_EndOfTheDayWithinNinetyDays()
    {
        var now = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        Assert.Null(DeviceActionText.MigrationDeadline(null, now));
        Assert.Null(DeviceActionText.MigrationDeadline(new DateTime(2026, 5, 1), now));
        Assert.Null(DeviceActionText.MigrationDeadline(new DateTime(2026, 12, 1), now));
        var ok = DeviceActionText.MigrationDeadline(new DateTime(2026, 6, 10), now)!.Value;
        Assert.Equal((23, 59), (ok.Hour, ok.Minute));
    }

    [Fact]
    public void OffboardWarning_OnlyWhenDeletingTheRecordCancelsTheAction()
    {
        Assert.Null(DeviceActionText.OffboardWarning(new OffboardPlan()));
        var plan = new OffboardPlan { DeleteIntuneRecord = true, TerminalAction = OffboardTerminalAction.Retire };
        Assert.EndsWith("cancels the retire.", DeviceActionText.OffboardWarning(plan));
    }

    // MARK: - Detail sections

    [Fact]
    public void Detail_SectionsInTheMacOrder_AndEmptyRowsLeftOut()
    {
        var device = DeviceDemoData.Devices(Now)[0];
        var autopilot = DeviceDemoData.Autopilot(Now)[0];
        var titles = DeviceDetailSections.ForDevice(device, autopilot, null).Select(s => s.Title).ToList();
        Assert.Equal(new[]
        {
            "Summary", "Windows Autopilot", "Device Management Service", "Recovery", "Hardware",
            "Group Membership", "Compliance & Conditional Access", "Detected Apps",
        }, titles);
        var summary = DeviceDetailSections.Summary(new IntuneDevice { DeviceName = "X" });
        Assert.DoesNotContain(summary.Rows, r => r.Label == "Model");
        Assert.Equal("Compliant", DeviceDetailSections.Compliance(device).Badge!.Value.Text);
    }

    [Fact]
    public void Detail_AppleOnlyAndAutopilotOnlySayTheyAreNotEnrolled()
    {
        var apple = new AppleOrgView(new AppleOrgDevice { SerialNumber = "SN-1", Model = "" }, "Apple Business Manager", null, true, false);
        var sections = DeviceDetailSections.ForAppleOnly(apple);
        Assert.Equal(new[] { "Apple Business Manager", "AppleCare", "Device Management Service" }, sections.Select(s => s.Title));
        Assert.Equal("Not enrolled in Intune.", sections[^1].Placeholder);
        Assert.Equal("Not Enrolled", DeviceDetailSections.Header(apple.Device).Title);
        Assert.Contains(sections[0].Rows, r => r.Label == "Management Service" && r.Value == "Not assigned");
    }

    [Theory]
    [InlineData("company", "Corporate")]
    [InlineData("personal", "Personal")]
    [InlineData("unknown", "Unknown")]
    [InlineData(null, null)]
    public void Detail_Ownership(string? raw, string? expected) => Assert.Equal(expected, DeviceDetailSections.FormatOwnership(raw));

    [Fact]
    public void Detail_FormatsAndTones()
    {
        Assert.Equal("windows Azure ADJoin", DeviceDetailSections.FormatEnrollmentType("windowsAzureADJoin"));
        Assert.Equal("1.0 GB", DeviceDetailSections.FormatBytes(1_073_741_824));
        Assert.Equal("512 MB", DeviceDetailSections.FormatBytes(536_870_912));
        Assert.Equal(DeviceTone.Attention, DeviceDetailSections.StorageTone(95));
        Assert.Equal(DeviceTone.Caution, DeviceDetailSections.StorageTone(75));
        Assert.Equal(DeviceTone.Good, DeviceDetailSections.StorageTone(10));
        Assert.Equal(DeviceTone.Caution, DeviceDetailSections.PolicyTone("conflict"));
        Assert.Equal((DeviceTone.Caution, "In Grace Period"), DeviceDetailSections.ComplianceBadge("inGracePeriod"));
        Assert.Equal("... and 10 more", DeviceDetailSections.AppsMore(60));
    }

    // MARK: - Compliance report

    [Fact]
    public async Task Report_PutsFailingSettingsFirstAndWritesTheTicketText()
    {
        var device = DeviceDemoData.Devices(Now)[0];
        var policy = (await DeviceDemoData.DetailSource.CompliancePoliciesAsync(device.Id))[0];
        var report = await ComplianceReportLoader.LoadAsync(DeviceDemoData.DetailSource, device, policy);

        var settings = report.Settings();
        Assert.True(settings[0].IsBad);
        Assert.Contains(settings[0].Details, d => d.StartsWith("Error: 0x87D1FDE8", StringComparison.Ordinal));
        Assert.Equal("Setting States (4)", report.SettingsHeading);
        Assert.Equal("v3", report.VersionText);
        Assert.Null(report.DefinitionNote);
        Assert.Equal(new[] { "Demo Devices - All Managed" }, report.AssignedGroupNames);

        var text = report.Text();
        Assert.StartsWith("Compliance policy: Demo Baseline Compliance — nonCompliant (v3) [windows10AndLater]", text);
        Assert.Contains("Policy requirements:", text);
        Assert.Contains($"Intune: {DeviceLinks.IntuneCompliance(device.Id)}", text);
    }

    [Fact]
    public void Report_SaysWhyTheDefinitionIsMissing()
    {
        var report = new ComplianceReport(new IntuneDevice { DeviceName = "X" }, new DeviceCompliancePolicyState { State = "error" });
        Assert.Equal("The policy definition could not be loaded.", report.DefinitionNote);
        report.Definition = new CompliancePolicyDefinition { AccessDenied = true };
        Assert.Contains("DeviceManagementConfiguration.Read.All", report.DefinitionNote);
        Assert.Equal((DeviceTone.Attention, "Error"), report.StateBadge);
        Assert.Equal("Unknown Policy", report.PolicyName);
    }

    // MARK: - Graph cards

    [Fact]
    public void Cards_KpisTreemapDonutAndErrors()
    {
        var cards = DeviceGraphCards.Build(DeviceDemoData.Devices(Now), false, DeviceDemoData.Errors(), Now);
        Assert.Equal(new[] { "Devices", "Platform Distribution", "Compliance", "Errors by Category" }, cards.Select(c => c.Title));
        Assert.Equal(GraphCardKind.Treemap, cards[1].Kind);
        Assert.Equal("Windows", cards[1].Bars[0].Label);
        Assert.Equal(new GraphFilter(DeviceGraphCategory.Platform, "Windows"), cards[1].Bars[0].Open);
        Assert.Equal("2", cards[0].Kpis[1].Value);
        Assert.Equal(2, cards[3].Kpis.Count);
        Assert.Equal("Hash Mismatch", cards[3].Bars[0].Label);
    }

    [Fact]
    public void Cards_EmptyLoadingAndAllClear()
    {
        var empty = DeviceGraphCards.Build(Array.Empty<IntuneDevice>(), false, null, Now);
        Assert.Equal(3, empty.Count);
        Assert.Equal(GraphCardBody.Empty, empty[1].Body);
        Assert.Equal("No device data", empty[1].Message);
        var loading = DeviceGraphCards.Build(Array.Empty<IntuneDevice>(), true, null, Now);
        Assert.Equal(GraphCardBody.Loading, loading[2].Body);
        Assert.True(loading[0].Kpis[0].Loading);

        var clear = DeviceGraphCards.Errors(new DeviceErrorFigures(7, new List<ErrorSummary>(), false));
        Assert.Equal(GraphCardBody.AllClear, clear.Body);
        Assert.Equal("7 devices reporting", clear.Detail);
        Assert.Equal(GraphCardBody.Loading, DeviceGraphCards.Errors(new DeviceErrorFigures(null, null, true)).Body);
    }

    [Fact]
    public void Treemap_FillsTheBoundsLargestFirst()
    {
        var slices = new[] { new ChartSlice("A", 1), new ChartSlice("B", 3), new ChartSlice("C", 0) };
        var tiles = TreemapLayout.Tiles(slices, 0, 0, 400, 100);
        Assert.Equal(2, tiles.Count);
        Assert.Equal(1, tiles[0].Index);
        Assert.Equal(400 * 100, tiles.Sum(t => t.Width * t.Height), 3);
        Assert.Equal(33, TreemapLayout.SharePercent(1, 3));
    }

    [Fact]
    public void WidgetFilterValues_MatchIgnoringCaseAndPunctuation()
    {
        Assert.Equal(new[] { "macOS" }, WidgetFilterValues.Match("Macintosh", new[] { "Windows", "macOS" }, DeviceFigures.PlatformLabel));
        Assert.Equal(new[] { "Noncompliant" }, WidgetFilterValues.Match("Non-Compliant", new[] { "Compliant", "Noncompliant" }));
        Assert.Equal(new[] { "Linux" }, WidgetFilterValues.Match("Linux", new[] { "Windows" }));
    }

    // MARK: - Demo fixtures

    [Fact]
    public void Demo_FixturesAreFictional()
    {
        var devices = DeviceDemoData.Devices(Now);
        Assert.All(devices, d => Assert.StartsWith("DEMO-", d.DeviceName));
        Assert.All(devices, d => Assert.StartsWith("DEMO-", d.SerialNumber));
        Assert.All(devices.Where(d => d.UserPrincipalName != null), d => Assert.EndsWith("@example.edu", d.UserPrincipalName));
        Assert.All(DeviceDemoData.Autopilot(Now), a => Assert.StartsWith("DEMO-", a.SerialNumber));
        Assert.All(DeviceDemoData.AppleOrgs(Now)[0].Devices, d => Assert.StartsWith("DEMO-", d.SerialNumber));
    }

    [Fact]
    public void Demo_JoinShowsRegisteredOnlyAndOrganizationOnlyRows()
    {
        var rows = AppleOrgJoin.Enrich(DeviceListJoin.Merge(DeviceDemoData.Devices(Now), DeviceDemoData.Autopilot(Now)), DeviceDemoData.AppleOrgs(Now));
        Assert.Contains(rows, r => r.Intune == null && r.Autopilot != null);
        Assert.Contains(rows, r => r.Intune == null && r.Apple != null);
        Assert.Equal(12, rows.Count);
    }
}
