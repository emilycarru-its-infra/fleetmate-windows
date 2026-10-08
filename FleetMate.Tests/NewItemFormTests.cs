using System.Text.Json;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Xunit;

namespace FleetMate.Tests;

public class NewItemFormTests
{
    private static readonly DevOpsMember[] Team =
    {
        new("Alex Doe", "alex@example.org"),
        new("Sam Roe", "sam@example.org"),
    };

    [Fact]
    public void ResolveAssignee_MapsDisplayNameToUniqueName()
    {
        Assert.Equal("alex@example.org", NewItemForm.ResolveAssignee("alex doe", Team));
    }

    [Fact]
    public void ResolveAssignee_KeepsUniqueNameAndUnknownValues()
    {
        Assert.Equal("sam@example.org", NewItemForm.ResolveAssignee("SAM@example.org", Team));
        Assert.Equal("someone@else.org", NewItemForm.ResolveAssignee("someone@else.org", Team));
        Assert.Equal("", NewItemForm.ResolveAssignee(null, Team));
    }

    [Fact]
    public void SplitTags_AcceptsCommasAndSemicolons_WithoutRepeats()
    {
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, NewItemForm.SplitTags(" alpha; beta,Alpha ;; gamma "));
        Assert.Empty(NewItemForm.SplitTags("  "));
    }

    [Fact]
    public void PickType_PrefersPrefillThenCurrentThenFirst()
    {
        var allowed = new[] { "Bug", "Task", "User Story" };
        Assert.Equal("Task", NewItemForm.PickType(allowed, "task", "Bug"));
        Assert.Equal("Bug", NewItemForm.PickType(allowed, "Epic", "Bug"));
        Assert.Equal("Bug", NewItemForm.PickType(allowed, null, "Feature"));
        Assert.Equal("", NewItemForm.PickType(Array.Empty<string>(), "Bug", null));
    }

    [Fact]
    public void Prefill_FromTask_CarriesTheAlikeFields()
    {
        var task = new UnifiedTask
        {
            Id = "42",
            Provider = "azdevops",
            Title = "Source",
            Description = "Steps",
            Assignees = new() { "Alex Doe" },
            Labels = new() { "devices", "mac" },
            Priority = 3,
            Bucket = "Sprint 7",
            Metadata = new() { ["workItemType"] = "Bug", ["areaPath"] = "Proj\\Devices", ["iterationPath"] = "" },
        };

        var prefill = WorkItemPrefill.FromTask(task);

        Assert.Equal("Bug", prefill.Type);
        Assert.Equal("Alex Doe", prefill.AssignedTo);
        Assert.Equal(3, prefill.Priority);
        Assert.Equal("Proj\\Devices", prefill.AreaPath);
        Assert.Equal("Sprint 7", prefill.IterationPath); // empty iteration falls back to the bucket
        Assert.Equal(new[] { "devices", "mac" }, prefill.Tags);
        Assert.Equal("Steps", prefill.Description);
    }

    [Fact]
    public void BoardWorkItemTypes_AreTheStateMappingKeys()
    {
        using var doc = JsonDocument.Parse("""
        { "value": [
            { "name": "New", "stateMappings": { "Bug": "New", "Task": "New" } },
            { "name": "Active", "stateMappings": { "task": "Active", "User Story": "Active" } },
            { "name": "Done" }
        ] }
        """);

        Assert.Equal(new[] { "Bug", "Task", "User Story" }, AzureDevOpsService.BoardWorkItemTypes(doc.RootElement));
    }
}
