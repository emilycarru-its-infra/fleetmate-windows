using System.Text.Json;
using FleetMate.Core.Models.Projects;
using Xunit;

namespace FleetMate.Tests;

public class InitiativeStatsTests
{
    private static WorkItem Item(int id, string type = "Task", string title = "Work", int? parent = null) =>
        new() { Id = id, Fields = new() { WorkItemType = type, Title = title, ParentId = parent } };

    [Fact]
    public void NestedWorkUsesInitiativeWithoutCountingAncestorsAsWork()
    {
        var task = Item(1, parent: 2);
        var feature = Item(2, "Feature", parent: 3);
        var initiative = Item(3, "Initiative", "Improve onboarding");
        Assert.Equal(new[] { ("Improve onboarding (#3)", 1) }, WorkItemStats.Initiatives(new[] { task }, new[] { feature, initiative }));
        Assert.Equal(new[] { ("Improve onboarding (#3)", 2) }, WorkItemStats.Initiatives(new[] { task, initiative }, new[] { feature }));
    }

    [Fact]
    public void UnassignedDiffersFromMissingParentsAndCycles()
    {
        var result = WorkItemStats.Initiatives(new[] { Item(1), Item(2, parent: 99), Item(3, parent: 4) }, new[] { Item(4, parent: 3) });
        Assert.Equal(new[] { ("Unavailable", 2), ("Unassigned", 1) }, result);
    }

    [Fact]
    public void DuplicateTitlesRemainSeparateAndNearestInitiativeWins()
    {
        var result = WorkItemStats.Initiatives(new[] { Item(1, parent: 10), Item(2, parent: 20) },
            new[] { Item(10, "Initiative", "Improve", 20), Item(20, "Initiative", "Improve") });
        Assert.Equal(new[] { ("Improve (#10)", 1), ("Improve (#20)", 1) }, result);
    }

    [Fact]
    public void TraversalBoundAppliesToAlreadyLoadedItems()
    {
        var chain = Enumerable.Range(1, 18).Select(id => Item(id, parent: id < 18 ? id + 1 : null)).ToArray();
        chain[16] = Item(17, "Initiative", "Boundary", 18);
        chain[17] = Item(18, "Initiative", "Beyond boundary");
        Assert.Equal(new[] { ("Boundary (#17)", 1) }, WorkItemStats.Initiatives(new[] { chain[0] }, chain.Skip(1)));
        chain[16] = Item(17, parent: 18);
        Assert.Equal(new[] { ("Unavailable", 1) }, WorkItemStats.Initiatives(new[] { chain[0] }, chain.Skip(1)));
        Assert.Contains(("Unavailable", 1), WorkItemStats.Initiatives(chain, Array.Empty<WorkItem>()));
    }

    [Fact]
    public void ParentFieldAndExistingParentRelationsDecode()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var field = JsonSerializer.Deserialize<WorkItem>("""{"id":1,"fields":{"System.Parent":9}}""", options)!;
        var relation = JsonSerializer.Deserialize<WorkItem>("""{"id":2,"fields":{},"relations":[{"rel":"System.LinkTypes.Hierarchy-Reverse","url":"https://example.com/_apis/wit/workItems/9"}]}""", options)!;
        Assert.Equal(9, field.HierarchyParentId);
        Assert.Equal(9, relation.HierarchyParentId);
        var result = WorkItemStats.Initiatives(new[] { relation }, new[] { Item(9, "Initiative", "Delivery") });
        Assert.Equal(new[] { ("Delivery (#9)", 1) }, result);
    }
}
