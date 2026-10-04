using FleetMate.GUI.Views.Shared;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The toolbar search dropdown's grouping.</summary>
public class ToolbarSearchTests
{
    private static ToolbarSearchResult Hit(string category, string title) => new(category, title, "", () => { });

    [Fact]
    public void Group_KeepsFirstSeenCategoryOrderAndCapsEachGroup()
    {
        var hits = new List<ToolbarSearchResult>
        {
            Hit("Pull Requests", "a"), Hit("Commits", "b"), Hit("Pull Requests", "c"), Hit("Pull Requests", "d"),
        };

        var groups = ToolbarSearch.Group(hits, perCategory: 2);

        Assert.Equal(new[] { "Pull Requests", "Commits" }, groups.Select(g => g.Category));
        Assert.Equal(new[] { "a", "c" }, groups[0].Hits.Select(h => h.Title));
    }
}
