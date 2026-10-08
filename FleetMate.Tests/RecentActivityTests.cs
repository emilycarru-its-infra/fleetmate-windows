using System.Windows.Input;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Models.Tickets;
using FleetMate.GUI.Views.Shared;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The toolbar Recent Activity feed and the tab shortcuts that replaced the Dashboard.</summary>
public class RecentActivityTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Tickets_Last24HoursNewestFirstWithRequestor()
    {
        var sources = new RecentActivityFeed.Sources
        {
            Tickets = new[]
            {
                new TdxTicket { Id = 1, Title = "Old", ModifiedDate = Now.AddDays(-2) },
                new TdxTicket { Id = 2, Title = "Printer", StatusName = "In Process", RequestorName = "Ada", ModifiedDate = Now.AddHours(-3) },
                new TdxTicket { Id = 3, Title = "Wifi", StatusName = "New", ModifiedDate = Now.AddHours(-1) },
            },
        };

        var feed = RecentActivityFeed.Build("Tickets", sources, Now);

        Assert.Equal(new[] { 3, 2 }, feed.Select(i => i.TicketId!.Value));
        Assert.Equal("#2  Printer", feed[1].Name);
        Assert.Equal("In Process", feed[1].Detail);
        Assert.Equal("Ada", feed[1].Context);
        Assert.All(feed, i => Assert.Equal("Tickets", i.Tab));
    }

    [Fact]
    public void Devices_SyncedTodayWithUserOrOs()
    {
        var sources = new RecentActivityFeed.Sources
        {
            Devices = new[]
            {
                new IntuneDevice { Id = "a", DeviceName = "LAB-01", UserDisplayName = "Ada", LastSyncDateTime = Now.AddHours(-2) },
                new IntuneDevice { Id = "b", DeviceName = "LAB-02", OperatingSystem = "Windows", LastSyncDateTime = Now.AddHours(-5) },
                new IntuneDevice { Id = "c", DeviceName = "LAB-03", LastSyncDateTime = Now.AddDays(-3) },
            },
        };

        var feed = RecentActivityFeed.Build("Devices", sources, Now);

        Assert.Equal(new[] { "a", "b" }, feed.Select(i => i.DeviceId));
        Assert.Equal(new[] { "Ada", "Windows" }, feed.Select(i => i.Context));
        Assert.All(feed, i => Assert.Equal("synced", i.Detail));
    }

    [Fact]
    public void Development_CommentsAcrossPullRequestsWithHideMine()
    {
        var pr = new UnifiedPullRequest
        {
            Source = PullRequestSource.GitHub, Number = 7, Container = "acme", Repository = "fleet", Title = "Widget",
            RecentComments = new()
            {
                new PullRequestComment { AuthorName = "me", Body = "done", Date = Now.AddHours(-1) },
                new PullRequestComment { AuthorName = "ada", Body = "nit", Date = Now.AddHours(-2) },
            },
        };
        var queue = new PullRequestQueue();
        queue.Insert(pr);
        queue.ViewerNames.Add("me");

        var all = RecentActivityFeed.Build("Development", new RecentActivityFeed.Sources { PullRequests = queue }, Now);
        Assert.Equal(new[] { "me", "ada" }, all.Select(i => i.Context));
        Assert.All(all, i => Assert.Same(pr, i.PullRequest));

        var hidden = RecentActivityFeed.Build("Development", new RecentActivityFeed.Sources { PullRequests = queue }, Now, hideMine: true);
        Assert.Equal("ada", Assert.Single(hidden).Context);
    }

    [Theory]
    [InlineData("Manage")]
    [InlineData("Identity")]
    public void ManageAndIdentityHaveNoFeed(string tab) =>
        Assert.Empty(RecentActivityFeed.Build(tab, new RecentActivityFeed.Sources(), Now));

    [Fact]
    public void Projects_AreaPathJoinedWithChevrons()
    {
        var item = new WorkItem { Id = 42 };
        item.Fields.Title = "Rollout";
        item.Fields.State = "Active";
        item.Fields.AreaPath = @"Projects\Devices\Windows";
        item.Fields.ChangedDate = Now.AddHours(-1);

        var feed = RecentActivityFeed.Build("Projects", new RecentActivityFeed.Sources { WorkItems = new[] { item, item } }, Now);

        var row = Assert.Single(feed);
        Assert.Equal("Projects › Devices › Windows", row.Context);
        Assert.Equal(42, row.WorkItemId);
    }

    [Theory]
    [InlineData(Key.D1, "Development")]
    [InlineData(Key.D2, "Projects")]
    [InlineData(Key.D3, "Devices")]
    [InlineData(Key.D4, "Reporting")]
    [InlineData(Key.D5, "Manage")]
    [InlineData(Key.D6, "Inventory")]
    [InlineData(Key.D7, "Identity")]
    [InlineData(Key.NumPad8, "Tickets")]
    [InlineData(Key.D9, null)]
    public void CtrlNumberSwitchesTabsInBarOrder(Key key, string? tab) =>
        Assert.Equal(tab, MainWindow.TabShortcut(key));

    [Fact]
    public void RelativeTimeUsesTheFeedsUnits()
    {
        Assert.Equal("just now", ActivityItem.Relative(Now.AddSeconds(-10), Now));
        Assert.Equal("5m ago", ActivityItem.Relative(Now.AddMinutes(-5), Now));
        Assert.Equal("3h ago", ActivityItem.Relative(Now.AddHours(-3), Now));
        Assert.Equal("2d ago", ActivityItem.Relative(Now.AddDays(-2), Now));
    }
}
