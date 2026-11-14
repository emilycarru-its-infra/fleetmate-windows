namespace FleetMate.Core.Services.Teams;

/// <summary>Managed channel defaults change the initial view, never the API's authorization.</summary>
public static class TeamsChannelScope
{
    public static IReadOnlyList<string> PinnedIds(string? configured) =>
        (configured ?? "").Split(new[] { ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToList();

    public static IReadOnlyList<TeamsChannel> Pinned(IEnumerable<TeamsChannel> channels, string? configured)
    {
        var byId = channels.GroupBy(c => c.ChannelId).ToDictionary(g => g.Key, g => g.First());
        return PinnedIds(configured).Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }

    public static IReadOnlyList<TeamsChannel> Filter(IEnumerable<TeamsChannel> channels, string? query)
    {
        var words = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return channels.Where(channel => words.All(word =>
            channel.TeamName.Contains(word, StringComparison.CurrentCultureIgnoreCase)
            || channel.ChannelName.Contains(word, StringComparison.CurrentCultureIgnoreCase))).ToList();
    }

}
