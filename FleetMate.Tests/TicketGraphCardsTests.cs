using FleetMate.Core.Development;
using FleetMate.Core.Models.Tickets;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The Tickets widgets as shared strip cards, and their clicks turned back into filters.</summary>
public class TicketGraphCardsTests
{
    private static List<TdxTicket> Sample() => new()
    {
        new() { Id = 1, StatusName = "New", PriorityName = "High", CreatedDate = DateTime.UtcNow },
        new() { Id = 2, StatusName = "In Process", PriorityName = "Low", ResponsibleFullName = "Sam", CreatedDate = DateTime.UtcNow },
    };

    private static (List<GraphCard> Cards, List<TicketWidgetTile> Tiles, TicketFilters Filters) Build()
    {
        var filters = new TicketFilters();
        List<TdxTicket> Matching(TicketFilterCategory? ignoring) =>
            TicketListQuery.Matching(Sample(), new TicketListOptions(filters), ignoring);
        var tiles = TicketWidgetRules.Tiles(Matching, filters);
        return (TicketGraphCards.For(tiles, TicketWidgetRules.Charts(Matching), loading: false), tiles, filters);
    }

    [Fact]
    public void TwoFigureCardsThenADonutThenBars()
    {
        var (cards, _, _) = Build();

        Assert.Equal(new[] { GraphCardKind.Kpis, GraphCardKind.Kpis, GraphCardKind.Donut, GraphCardKind.Bars, GraphCardKind.Bars, GraphCardKind.Bars, GraphCardKind.Bars },
            cards.Select(c => c.Kind));
        Assert.Equal(3, cards[0].Kpis.Count);
        Assert.Equal(3, cards[1].Kpis.Count);
        Assert.Equal("2", cards[0].Kpis[0].Value);
    }

    [Fact]
    public void ABarClickTogglesItsValue()
    {
        var (cards, tiles, filters) = Build();
        var high = cards.Single(c => c.Title == "By Priority").Bars.Single(b => b.Label == "High");

        Assert.True(TicketGraphCards.Apply(high.Open!, tiles, filters));
        Assert.True(filters.IsSelected(TicketFilterCategory.Priority, "High"));
    }

    [Fact]
    public void AFigureClickRunsItsToggle()
    {
        var (cards, tiles, filters) = Build();
        var unassigned = cards[1].Kpis.Single(k => k.Title == "Unassigned");

        Assert.True(TicketGraphCards.Apply(unassigned.Open!, tiles, filters));
        Assert.True(filters.IsSelected(TicketFilterCategory.Responsible, TicketStats.Unassigned));
    }

    [Fact]
    public void AnotherTabsFilterIsNotApplied()
    {
        var (_, tiles, filters) = Build();

        Assert.False(TicketGraphCards.Apply(new GraphFilter(GraphCategory.Repository, "x"), tiles, filters));
        Assert.False(filters.HasActiveFilters);
    }
}
