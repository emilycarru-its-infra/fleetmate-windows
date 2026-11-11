namespace FleetMate.Core.Services.Teams;

/// <summary>Never joins pages from different index snapshots; preserves API order and removes repeated thread references.</summary>
public sealed class TeamsBrowseState
{
    public IReadOnlyList<TeamsSearchHit> Results { get; private set; } = Array.Empty<TeamsSearchHit>();
    public string? IndexVersion { get; private set; }
    public int? NextOffset { get; private set; }
    public int Total { get; private set; }

    public bool Apply(TeamsBrowseResponse response, bool append)
    {
        if (append && IndexVersion != response.IndexVersion) return false;
        Results = (append ? Results.Concat(response.Results) : response.Results)
            .DistinctBy(hit => (hit.ChannelId, hit.ThreadId)).ToList();
        IndexVersion = response.IndexVersion;
        NextOffset = response.NextOffset;
        Total = response.Total;
        return true;
    }
}
