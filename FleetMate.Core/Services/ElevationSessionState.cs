namespace FleetMate.Core.Services;

/// <summary>What the window chrome shows for an elevation session.</summary>
public enum ElevationSessionState
{
    /// <summary>Elevation is not configured, or the Graph transport is direct.</summary>
    Off,
    /// <summary>Not checked yet, or the check itself failed (az not signed in).</summary>
    Unknown,
    /// <summary>No session container exists.</summary>
    None,
    /// <summary>The container is being created or is booting.</summary>
    Starting,
    /// <summary>The container is running and its TTL has not passed.</summary>
    Ready,
    /// <summary>The container finished its TTL sleep, or its expires tag has passed.</summary>
    Expired,
}

/// <summary>
/// One domain's session as read from Azure: <c>az container show</c> on
/// <c>aze-{domain}-{user}</c>, taking <c>instanceView.state</c> and the
/// <c>expires</c> tag (unix seconds) written when the session was created.
/// </summary>
public sealed record ElevationSessionInfo(string? ContainerState, long? ExpiresUnix)
{
    public DateTimeOffset? Expires =>
        ExpiresUnix is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : null;

    /// <summary>
    /// Map the Azure signal to a chrome state. <paramref name="creating"/> is
    /// true while this process is inside the create call, before Azure has a
    /// container to report on.
    /// </summary>
    public ElevationSessionState Classify(DateTimeOffset now, bool creating = false)
    {
        var state = ContainerState?.Trim();
        if (string.IsNullOrEmpty(state))
            return creating ? ElevationSessionState.Starting : ElevationSessionState.None;

        if (state.Equals("Running", StringComparison.OrdinalIgnoreCase))
            return Expires is { } e && e <= now ? ElevationSessionState.Expired : ElevationSessionState.Ready;

        // ACI reports Pending/Waiting while pulling the image and starting.
        if (state.Equals("Pending", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("Waiting", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("Creating", StringComparison.OrdinalIgnoreCase) ||
            creating)
            return ElevationSessionState.Starting;

        // Terminated / Stopped / Succeeded / Failed: the sleep ran out or the
        // container died. Either way the next action recreates it.
        return ElevationSessionState.Expired;
    }
}

/// <summary>Operator-facing wording for elevation states, shared by chrome and tests.</summary>
public static class ElevationStatusText
{
    public static string Label(ElevationSessionState state) => state switch
    {
        ElevationSessionState.Ready => "Elevation ready",
        ElevationSessionState.Starting => "Elevation starting…",
        ElevationSessionState.Expired => "Elevation expired",
        ElevationSessionState.None => "Elevation idle",
        ElevationSessionState.Unknown => "Elevation unknown",
        _ => "Elevation off",
    };

    public static string DomainLine(GraphDomain domain, ElevationSessionState state,
        DateTimeOffset? expires, string? error, DateTimeOffset now)
    {
        var name = domain.DomainName();
        return state switch
        {
            ElevationSessionState.Ready when expires is { } e =>
                $"{name}: ready, expires in {FormatRemaining(e - now)}",
            ElevationSessionState.Ready => $"{name}: ready",
            ElevationSessionState.Starting => $"{name}: starting (first boot takes about a minute)",
            ElevationSessionState.Expired => $"{name}: expired — the next action restarts it",
            ElevationSessionState.None => $"{name}: not started — the next action starts it",
            ElevationSessionState.Unknown when !string.IsNullOrWhiteSpace(error) => $"{name}: unknown ({error})",
            ElevationSessionState.Unknown => $"{name}: not checked yet",
            _ => $"{name}: off",
        };
    }

    private static string FormatRemaining(TimeSpan span)
    {
        if (span <= TimeSpan.Zero) return "0m";
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{Math.Max(1, (int)span.TotalMinutes)}m";
    }
}
