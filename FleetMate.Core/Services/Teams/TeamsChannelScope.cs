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

}
