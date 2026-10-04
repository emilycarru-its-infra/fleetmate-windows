using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Search;
using Xunit;

namespace FleetMate.Tests;

public class GlobalSearchTests
{
    private static SearchSources Sources() => new()
    {
        Devices = new[]
        {
            new IntuneDevice { Id = "dev-1", DeviceName = "DEVICE-ONE", SerialNumber = "SN-ALPHA-1", UserDisplayName = "Pat Doe" },
        },
        Assets = new[]
        {
            new SnipeAsset
            {
                Id = 42, Name = "Sample Laptop", AssetTag = "T0042", Serial = "C02XYZ123",
                Model = new SnipeRef { Name = "Laptop Model" },
                AssignedTo = new SnipeAssignee { Name = "Pat Doe" },
                CustomFields = new() { ["Hostname"] = new SnipeCustomField { Value = "box-alpha-7" } },
            },
        },
        Tickets = new[] { new TdxTicket { Id = 5150, Title = "Printer jam", RequestorName = "Sam Roe" } },
        WorkItems = new[] { new WorkItem { Id = 1234, Fields = new WorkItemFields { Title = "Rotate printer keys" } } },
        Users = new[] { new EntraUser { Id = "u1", DisplayName = "Pat Doe", UserPrincipalName = "pdoe@example.edu" } },
        Groups = new[] { new EntraGroup { Id = "g1", DisplayName = "Printer Admins" } },
    };

    [Theory]
    [InlineData("1234", 1234)]
    [InlineData("#1234", 1234)]
    [InlineData("Task#1234", 1234)]
    [InlineData(" 77 ", 77)]
    [InlineData("12a", null)]
    [InlineData("printer", null)]
    public void ParseWorkItemId_AcceptsBareHashAndPrefixedForms(string query, int? expected) =>
        Assert.Equal(expected, GlobalSearch.ParseWorkItemId(query));

    [Theory]
    [InlineData("a", false)]
    [InlineData("ab", true)]
    [InlineData("7", true)]
    [InlineData("#7", true)]
    public void ShouldSearch_StartsAtTwoCharactersOrAnyId(string query, bool expected) =>
        Assert.Equal(expected, GlobalSearch.ShouldSearch(query));

    [Fact]
    public void Search_BySerial_FindsAssetWithMatchLabelAndKey()
    {
        var group = Assert.Single(GlobalSearch.Search("C02XYZ", Sources()));
        Assert.Equal(SearchCategory.Inventory, group.Category);
        var hit = Assert.Single(group.Hits);
        Assert.Equal("42", hit.Key);
        Assert.Equal("Serial: C02XYZ123", hit.MatchLabel);
        Assert.Equal("T0042 · Laptop Model · Pat Doe", hit.Subtitle);
    }

    [Fact]
    public void Search_MatchesAssetCustomFieldValues()
    {
        var hit = Assert.Single(Assert.Single(GlobalSearch.Search("box-alpha", Sources())).Hits);
        Assert.Equal("Hostname: box-alpha-7", hit.MatchLabel);
    }

    [Fact]
    public void Search_GroupsAcrossCategories()
    {
        var groups = GlobalSearch.Search("pat doe", Sources());
        Assert.Equal(new[] { SearchCategory.Devices, SearchCategory.Inventory, SearchCategory.Users },
            groups.Select(g => g.Category));
    }

    [Fact]
    public void Search_CapsEachCategoryButCountsAll()
    {
        var groups = GlobalSearch.Search("printer", new SearchSources
        {
            Groups = Enumerable.Range(1, 9).Select(i => new EntraGroup { Id = $"g{i}", DisplayName = $"Printer {i}" }).ToList(),
        });
        var group = Assert.Single(groups);
        Assert.Equal(9, group.Total);
        Assert.Equal(GlobalSearch.PerCategory, group.Hits.Count);
    }

    [Fact]
    public void Search_ByHashId_FindsExactlyThatWorkItem()
    {
        var groups = GlobalSearch.Search("#1234", Sources());
        var hit = Assert.Single(Assert.Single(groups, g => g.Category == SearchCategory.WorkItems).Hits);
        Assert.Equal("1234", hit.Key);
        Assert.Equal("ID: 1234", hit.MatchLabel);
    }

    [Fact]
    public void PrependWorkItem_PutsFetchedItemFirst()
    {
        var groups = GlobalSearch.Search("printer", Sources());
        GlobalSearch.PrependWorkItem(groups, new WorkItem { Id = 999, Fields = new WorkItemFields { Title = "Fetched" } });

        Assert.Equal(SearchCategory.WorkItems, groups[0].Category);
        Assert.Equal("999", groups[0].Hits[0].Key);
        Assert.Equal(2, groups[0].Total);
    }
}
