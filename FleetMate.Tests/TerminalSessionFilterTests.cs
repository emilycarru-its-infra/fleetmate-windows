using FleetMate.Core.Services.Terminal;
using Xunit;

namespace FleetMate.Tests;

public class TerminalSessionFilterTests
{
    private static TerminalSessionTraits Traits(bool agent = false, bool running = false, bool paused = false,
        string[]? text = null, int? seconds = null) =>
        new(agent, running, paused, text ?? Array.Empty<string>(),
            seconds is { } s ? DateTime.UnixEpoch.AddSeconds(s) : null);

    [Fact]
    public void NoFilter_MatchesEverything()
    {
        var filter = new TerminalSessionFilter();
        Assert.False(filter.IsFiltering);
        Assert.True(filter.Matches(Traits()));
    }

    [Fact]
    public void Toggles_Intersect()
    {
        var filter = new TerminalSessionFilter(Active: true, Paused: true);
        Assert.True(filter.Matches(Traits(agent: true, paused: true)));
        Assert.False(filter.Matches(Traits(agent: true)));
        Assert.False(filter.Matches(Traits(paused: true)));
    }

    [Fact]
    public void Search_MatchesEveryWordAcrossFields()
    {
        var filter = new TerminalSessionFilter(Query: "  Fleet main ");
        Assert.True(filter.IsFiltering);
        Assert.True(filter.Matches(Traits(text: new[] { "fleetmate", @"~\src\main" })));
        Assert.False(filter.Matches(Traits(text: new[] { "fleetmate", @"~\src\other" })));
    }

    [Fact]
    public void Search_CombinesWithToggles()
    {
        var filter = new TerminalSessionFilter(Running: true, Query: "codex");
        Assert.True(filter.Matches(Traits(running: true, text: new[] { "Codex" })));
        Assert.False(filter.Matches(Traits(text: new[] { "Codex" })));
    }

    [Fact]
    public void Order_ByRecentActivity()
    {
        var items = new[] { Traits(seconds: 10), Traits(), Traits(seconds: 30), Traits(seconds: 20) };
        Assert.Equal(new[] { 0, 1, 2, 3 }, TerminalSessionFilter.Order(items, TerminalSessionSort.Manual));
        Assert.Equal(new[] { 2, 3, 0, 1 }, TerminalSessionFilter.Order(items, TerminalSessionSort.RecentFirst));
        Assert.Equal(new[] { 1, 0, 3, 2 }, TerminalSessionFilter.Order(items, TerminalSessionSort.RecentLast));
    }

    [Fact]
    public void Paused_IsAttentionOrAnIdleAgent()
    {
        Assert.True(TerminalSessionTraits.Paused(ActivityState.Attention, isAgent: false));
        Assert.True(TerminalSessionTraits.Paused(ActivityState.Idle, isAgent: true));
        Assert.False(TerminalSessionTraits.Paused(ActivityState.Idle, isAgent: false));
        Assert.False(TerminalSessionTraits.Paused(ActivityState.Active, isAgent: true));
    }

    [Fact]
    public void SortKeys_RoundTripWithTheMacValues()
    {
        foreach (var sort in Enum.GetValues<TerminalSessionSort>())
            Assert.Equal(sort, TerminalSessionSortNames.Parse(sort.Key()));
        Assert.Equal(TerminalSessionSort.Manual, TerminalSessionSortNames.Parse(null));
    }

    [Fact]
    public void Layout_RestoresTheLastMode()
    {
        var layout = new TerminalLayoutState(300);
        layout.RestoreMode(fullWindow: true);
        Assert.True(layout.FullWindow);
        layout.Toggle(1000);
        Assert.False(layout.FullWindow);
        Assert.Equal(300, layout.Height);
    }

    [Fact]
    public void Activity_RecordsOutputAndBells()
    {
        var activity = new SessionActivity();
        Assert.Null(activity.LastActivity);
        var now = DateTime.UtcNow;
        activity.Output(now);
        Assert.Equal(now, activity.LastActivity);
        activity.Bell(visible: true);
        Assert.True(activity.LastActivity >= now);
    }
}
