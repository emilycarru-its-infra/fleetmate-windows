using FleetMate.Core.Identity;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The Identity rules the WPF and WinUI tabs share. Synthetic values only.</summary>
public class IdentityModelTests
{
    private sealed class FakeDirectory : IIdentityDirectory
    {
        public List<string> Calls { get; } = new();
        public EntraUser? Exact { get; init; }
        public Task<IReadOnlyList<EntraGroup>> DeviceGroupsAsync() => Task.FromResult<IReadOnlyList<EntraGroup>>(Array.Empty<EntraGroup>());
        public Task<IReadOnlyList<IntuneDevice>> GroupDevicesAsync(string groupId) => Task.FromResult<IReadOnlyList<IntuneDevice>>(Array.Empty<IntuneDevice>());
        public Task<EntraUser?> UserAsync(string upnOrId) { Calls.Add("exact:" + upnOrId); return Task.FromResult(Exact); }
        public Task<IReadOnlyList<EntraUser>> SearchUsersAsync(string query) { Calls.Add("search:" + query); return Task.FromResult<IReadOnlyList<EntraUser>>(new[] { new EntraUser { DisplayName = "Fuzzy" } }); }
        public Task<IReadOnlyList<IntuneDevice>> UserDevicesAsync(string upn) => Task.FromResult<IReadOnlyList<IntuneDevice>>(Array.Empty<IntuneDevice>());
        public Task<IReadOnlyList<EntraGroup>> UserGroupsAsync(string upn) => Task.FromResult<IReadOnlyList<EntraGroup>>(Array.Empty<EntraGroup>());
        public Task<bool> SetAccountEnabledAsync(string userId, bool enabled) => Task.FromResult(true);
        public Task<bool> AddToGroupAsync(string groupNameOrId, string userId) => Task.FromResult(true);
        public Task<bool> RemoveFromGroupAsync(string groupId, string userId) => Task.FromResult(true);
    }

    [Fact]
    public async Task AnAddressIsLookedUpExactlyFirst()
    {
        var directory = new FakeDirectory { Exact = new EntraUser { DisplayName = "Pat Example" } };
        var found = await IdentitySearch.FindUsersAsync(directory, " pat@example.invalid ");
        Assert.Equal("Pat Example", Assert.Single(found).DisplayName);
        Assert.Equal(new[] { "exact:pat@example.invalid" }, directory.Calls);
    }

    [Fact]
    public async Task AnExactLookupThatFindsNoOneFallsBackToTheFuzzySearch()
    {
        var directory = new FakeDirectory();
        var found = await IdentitySearch.FindUsersAsync(directory, Guid.Empty.ToString());
        Assert.Equal("Fuzzy", Assert.Single(found).DisplayName);
        Assert.Equal(2, directory.Calls.Count);
    }

    [Fact]
    public async Task ANameGoesStraightToTheFuzzySearch()
    {
        var directory = new FakeDirectory();
        await IdentitySearch.FindUsersAsync(directory, "pat");
        Assert.Equal(new[] { "search:pat" }, directory.Calls);
        Assert.Empty(await IdentitySearch.FindUsersAsync(directory, "   "));
    }

    [Fact]
    public void GroupsFilterOnNameOrDescription()
    {
        var group = new EntraGroup { DisplayName = "Devices-Lab-North", Description = "Teaching lab" };
        Assert.True(IdentitySearch.GroupMatches(group, null));
        Assert.True(IdentitySearch.GroupMatches(group, "north"));
        Assert.True(IdentitySearch.GroupMatches(group, "teaching"));
        Assert.False(IdentitySearch.GroupMatches(group, "studio"));
    }

    [Theory]
    [InlineData("Pat Example", "PE")]
    [InlineData("pat.example", "PE")]
    [InlineData("Single", "S")]
    [InlineData("", "?")]
    public void InitialsComeFromTheFirstTwoNameParts(string name, string initials) =>
        Assert.Equal(initials, EntraUserProfile.Initials(name));

    [Fact]
    public void TheBadgeAndActionFollowTheAccountState()
    {
        var disabled = new EntraUser { AccountEnabled = false };
        Assert.Equal(AccountState.Disabled, EntraUserProfile.State(disabled));
        Assert.Equal("Enable Account", EntraUserProfile.ToggleLabel(disabled));
        Assert.Equal("Disable Account", EntraUserProfile.ToggleLabel(new EntraUser { AccountEnabled = true }));
        Assert.Equal(AccountState.Unknown, EntraUserProfile.State(new EntraUser()));
    }

    [Fact]
    public void SectionsWithNoValuesAreSkippable()
    {
        var user = new EntraUser { DisplayName = "Pat Example", JobTitle = "Technician", Department = "Studio" };
        var sections = EntraUserProfile.Sections(user);
        Assert.True(sections.Single(s => s.Title == "Job information").HasValues);
        Assert.False(sections.Single(s => s.Title == "On-premises").HasValues);
        Assert.Equal("Technician · Studio", EntraUserProfile.Subtitle(user));
        Assert.Equal("—", sections[0].Rows.Single(r => r.Label == "Surname").DisplayValue);
    }
}
