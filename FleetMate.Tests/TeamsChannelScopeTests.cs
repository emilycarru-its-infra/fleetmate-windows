using FleetMate.Core.Config;
using FleetMate.Core.Services.Teams;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FleetMate.Tests;

public class TeamsChannelScopeTests
{
    [Fact]
    public void PinsOnlyContainAuthorizedChannelsAndPreserveConfiguredOrder()
    {
        var channels = new[]
        {
            new TeamsChannel("team-1", "Example Team", "c1", "General", "standard", 2, null),
            new TeamsChannel("team-1", "Example Team", "c2", "Support", "shared", 3, null),
        };
        var pins = TeamsChannelScope.Pinned(channels, " c2,missing\nc1\nc2\r\n");
        Assert.Equal(new[] { "c2", "c1" }, pins.Select(c => c.ChannelId));
        Assert.Equal(2, channels.Length);
        Assert.Empty(TeamsChannelScope.Pinned(channels, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegistryAndPolicyLoadDefaultsIncludingAnExplicitClear(bool fromPolicy)
    {
        var config = new FleetMateConfig { TeamsDefaultTeam = "old-team", TeamsDefaultChannels = "old-channel" };
        var values = new Dictionary<string, object?> { ["TeamsDefaultTeam"] = "", ["TeamsDefaultChannels"] = "c1,c2" };
        FleetMateConfig.ApplyRegistryValues(name => values.GetValueOrDefault(name), config, fromPolicy);
        Assert.Equal("", config.TeamsDefaultTeam);
        Assert.Equal("c1,c2", config.TeamsDefaultChannels);
    }

    [Theory]
    [InlineData("teams_default_team: team-1\nteams_default_channels: 'c1,c2'")]
    [InlineData("teamsDefaultTeam: team-1\nteamsDefaultChannels: 'c1,c2'")]
    public void DefaultsLoadFromBothYamlConventions(string yaml)
    {
        var config = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties().Build().Deserialize<FleetMateConfig>(yaml);
        Assert.Equal("team-1", config.TeamsDefaultTeam);
        Assert.Equal(new[] { "c1", "c2" }, TeamsChannelScope.PinnedIds(config.TeamsDefaultChannels));
    }

    [Fact]
    public void TicketsMateClearsTeamsDefaults()
    {
        var config = new FleetMateConfig { TeamsDefaultTeam = "team-1", TeamsDefaultChannels = "c1" };
        config.LimitToTickets();
        Assert.Null(config.TeamsDefaultTeam);
        Assert.Null(config.TeamsDefaultChannels);
    }

    [Fact]
    public void TeamsModuleRequiresBothEndpointAndScope()
    {
        var config = new FleetMateConfig { TeamsApiUrl = "https://search.example.org" };
        Assert.False(AppModules.IsConfigured("Teams", config));
        config.TeamsApiScope = "api://example-search/.default";
        Assert.True(AppModules.IsConfigured("Teams", config));
        Assert.Contains(AppModules.Tabs, m => m.Tag == "Teams");
    }
}
