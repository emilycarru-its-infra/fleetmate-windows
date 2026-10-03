using System.Text.Json;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using Xunit;

namespace FleetMate.Tests;

public class TicketBoardQueryTests
{
    private static readonly Guid Me = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static TdxConfig Config(int group = 0) => new()
    {
        BaseUrl = "https://tdx.example.edu/TDWebApi",
        AppId = 31,
        ResponsibleGroupId = group,
    };

    private static TdxTicket T(int id, int? groupId = null, string? groupName = null) => new()
    {
        Id = id,
        ResponsibleGroupId = groupId,
        ResponsibleGroupName = groupName,
    };

    [Fact]
    public void BoardSearch_MatchesTheMacQuery()
    {
        var from = new DateTime(2026, 9, 1);
        var to = new DateTime(2026, 12, 31, 23, 59, 59);
        var search = TicketBoardQuery.BoardSearch(Config(), from, to);

        Assert.Equal(5000, search.MaxResults);
        Assert.Equal(from, search.CreatedDateFrom);
        Assert.Equal(to, search.CreatedDateTo);
        Assert.Null(search.ResponsibleGroupIds);
    }

    [Fact]
    public void BoardSearch_FiltersByGroupOnlyWhenOneIsConfigured()
    {
        var search = TicketBoardQuery.BoardSearch(Config(group: 42), DateTime.Today, DateTime.Today.AddDays(1));
        Assert.Equal(new[] { 42 }, search.ResponsibleGroupIds);
    }

    [Fact]
    public void BoardSearch_SerializesTheTdxFieldNames()
    {
        var json = JsonSerializer.Serialize(
            TicketBoardQuery.BoardSearch(Config(group: 42), new DateTime(2026, 9, 1), new DateTime(2026, 10, 1)));
        Assert.Contains("\"CreatedDateFrom\":\"2026-09-01T00:00:00", json);
        Assert.Contains("\"ResponsibleGroupIDs\":[42]", json);
        Assert.Contains("\"MaxResults\":5000", json);
    }

    [Fact]
    public void OpenSearch_IsEveryOpenTicketWithNoDateWindow()
    {
        var open = TicketBoardQuery.OpenSearch(Config(group: 42));
        Assert.Equal(new[] { 1, 2, 5, 6 }, open.StatusClassIds);
        Assert.Equal(new[] { 42 }, open.ResponsibleGroupIds);
        Assert.Null(open.CreatedDateFrom);
        Assert.Null(open.CreatedDateTo);
        Assert.Equal(5000, open.MaxResults);

        Assert.Null(TicketBoardQuery.OpenSearch(Config()).ResponsibleGroupIds);
        Assert.Contains("\"StatusClassIDs\":[1,2,5,6]", JsonSerializer.Serialize(open));
    }

    [Fact]
    public void MineSearch_IsMyOpenTicketsInAnyGroupWithNoDateWindow()
    {
        Assert.Null(TicketBoardQuery.MineSearch(null));
        Assert.Null(TicketBoardQuery.MineSearch(Guid.Empty));

        var mine = TicketBoardQuery.MineSearch(Me)!;
        Assert.Equal(new[] { Me }, mine.ResponsibleUids);
        Assert.Equal(new[] { 1, 2, 5, 6 }, mine.StatusClassIds);
        Assert.Null(mine.ResponsibleGroupIds);
        Assert.Null(mine.CreatedDateFrom);
        Assert.Equal(1000, mine.MaxResults);
    }

    [Fact]
    public void Merge_CombinesAllThreeByIdWithoutDuplicates()
    {
        var dated = new[] { T(1, 42, "Devices"), T(2, 42, "Devices") };
        var open = new[] { T(2, 42, "Devices"), T(9, 42, "Devices") };   // 9: open, created long ago
        var mine = new[] { T(1, 42, "Devices"), T(3), T(5, 7, "Network") };

        var merged = TicketBoardQuery.Merge(dated, open, mine);
        Assert.Equal(new[] { 1, 2, 9, 3, 5 }, merged.Select(t => t.Id));
        Assert.Equal(new[] { 1 }, TicketBoardQuery.Merge(new[] { T(1) }, null).Select(t => t.Id));
    }

    [Theory]
    [InlineData(2026, 2, 10, "2026-01-01", "2026-05-01", "Spring 2026")]
    [InlineData(2026, 6, 10, "2026-05-01", "2026-09-01", "Summer 2026")]
    [InlineData(2026, 10, 3, "2026-09-01", "2026-12-31T23:59:59", "Fall 2026")]
    public void CurrentTerm_MatchesTheMacTerms(int y, int m, int d, string from, string to, string label)
    {
        var now = new DateTime(y, m, d, 10, 0, 0);
        var range = TicketBoardQuery.Range(TicketDateRangePreset.CurrentTerm, now);
        Assert.Equal(DateTime.Parse(from), range.From);
        Assert.Equal(DateTime.Parse(to), range.To);
        Assert.Equal(label, TicketBoardQuery.Label(TicketDateRangePreset.CurrentTerm, now));
    }

    [Fact]
    public void PreviousTerm_WrapsIntoLastYear()
    {
        var now = new DateTime(2026, 2, 1);
        var range = TicketBoardQuery.Range(TicketDateRangePreset.PreviousTerm, now);
        Assert.Equal(new DateTime(2025, 9, 1), range.From);
        Assert.Equal("Fall 2025", TicketBoardQuery.Label(TicketDateRangePreset.PreviousTerm, now));
    }

    [Fact]
    public void CalendarPresets()
    {
        var now = new DateTime(2026, 10, 3, 15, 0, 0);
        Assert.Equal((new DateTime(2026, 10, 3), new DateTime(2026, 10, 4)), TicketBoardQuery.Range(TicketDateRangePreset.Today, now));
        Assert.Equal((new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)), TicketBoardQuery.Range(TicketDateRangePreset.ThisMonth, now));
        Assert.Equal((new DateTime(2026, 9, 1), new DateTime(2026, 10, 1)), TicketBoardQuery.Range(TicketDateRangePreset.LastMonth, now));
        var week = TicketBoardQuery.Range(TicketDateRangePreset.ThisWeek, now);
        Assert.Equal(7, (week.To - week.From).TotalDays);
        Assert.InRange(now, week.From, week.To);
    }
}

public class TicketBoardLayoutTests
{
    private static TdxTicket T(int id, string? group, string? responsible) => new()
    {
        Id = id,
        ResponsibleGroupName = group,
        ResponsibleFullName = responsible,
    };

    [Fact]
    public void ResponsibleMode_PutsGrouplessTicketsInATrailingNoGroupBlock()
    {
        var blocks = TicketBoardLayout.ResponsibleBlocks(new[]
        {
            T(1, null, "Avery"),
            T(2, "Network", "Blake"),
            T(3, "Devices", null),
            T(4, "Devices", "Casey"),
            T(5, "", null),
        });

        Assert.Equal(new[] { "Devices", "Network", "No Group" }, blocks.Select(b => b.Header));
        Assert.Equal(new[] { 1, 5 }, blocks[^1].Columns.SelectMany(c => c.Tickets).Select(t => t.Id).OrderBy(i => i));
        Assert.Equal(5, blocks.Sum(b => b.Count));
    }

    [Fact]
    public void ResponsibleMode_PeopleAlphabeticalWithUnassignedLast()
    {
        var blocks = TicketBoardLayout.ResponsibleBlocks(new[]
        {
            T(1, "Devices", null),
            T(2, "Devices", "casey"),
            T(3, "Devices", "Avery"),
        });

        var block = Assert.Single(blocks);
        Assert.Equal(new[] { "Avery", "casey", "Unassigned" }, block.Columns.Select(c => c.Key));
    }

    [Fact]
    public void GroupMode_HasANoGroupColumnLast()
    {
        var columns = TicketBoardLayout.Columns(new[]
        {
            T(1, null, "Avery"),
            T(2, "Network", null),
            T(3, "Devices", null),
        }, TicketBoardLayout.GroupKey, TicketBoardLayout.NoGroup);

        Assert.Equal(new[] { "Devices", "Network", "No Group" }, columns.Select(c => c.Key));
        Assert.Equal(1, columns[^1].Tickets.Single().Id);
    }
}

public class TicketWebUrlTests
{
    [Theory]
    [InlineData("https://tdx.example.edu/TDWebApi")]
    [InlineData("https://tdx.example.edu/TDWebApi/")]
    [InlineData("https://tdx.example.edu/tdwebapi")]
    [InlineData("https://tdx.example.edu/TDWebApi/api")]
    [InlineData("https://tdx.example.edu")]
    [InlineData("tdx.example.edu/TDWebApi")]
    public void BuildsTheTdNextPageFromTheConfiguredHost(string baseUrl)
    {
        var config = new TdxConfig { BaseUrl = baseUrl, AppId = 31 };
        Assert.Equal("https://tdx.example.edu/TDNext/Apps/31/Tickets/TicketDet?TicketID=12345",
            config.GetTicketWebUrl(12345));
    }

    [Fact]
    public void UsesTheTicketingAppWhenItDiffers()
    {
        var config = new TdxConfig { BaseUrl = "https://tdx.example.edu/TDWebApi", AppId = 31, TicketingAppId = 44 };
        Assert.Equal("https://tdx.example.edu/TDNext/Apps/44/Tickets/TicketDet?TicketID=7", config.GetTicketWebUrl(7));
    }

    [Fact]
    public void NoBaseUrlMeansNoLink()
    {
        Assert.Null(new TdxConfig { AppId = 31 }.GetTicketWebUrl(1));
    }
}
