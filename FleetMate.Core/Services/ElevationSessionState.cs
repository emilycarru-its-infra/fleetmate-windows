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
/// <summary>The shield dot's colours. Never red or amber.</summary>
public enum ElevationDotTone { Grey, Blue, Green }

public static class ElevationStatusText
{
    /// <summary>
    /// The colour of the dot on the toolbar's Authentication shield. The toolbar
    /// shows no elevation text: stopped, idle and unchecked stay a quiet grey,
    /// since the next action starts the session. The state is spelled out in
    /// the Authentication popover.
    /// </summary>
    public static ElevationDotTone DotTone(ElevationSessionState state) => state switch
    {
        ElevationSessionState.Ready => ElevationDotTone.Green,
        ElevationSessionState.Starting => ElevationDotTone.Blue,
        _ => ElevationDotTone.Grey,
    };

    /// <summary>What the feature is called wherever it is listed.</summary>
    public const string Title = "Elevated sessions";

    /// <summary>One line on what an elevated session is, for tooltips.</summary>
    public static string Explainer(int ttlHours = 8) =>
        "Elevated sessions are Azure containers that run admin actions as a managed identity, "
        + "authorized by your az sign-in. One starts on first use, takes about a minute, "
        + $"and then stays ready for up to {ttlHours} hours.";

    /// <summary>The short state in a session's pill.</summary>
    public static string StateLabel(ElevationSessionState state) => state switch
    {
        ElevationSessionState.Ready => "Ready",
        ElevationSessionState.Starting => "Starting…",
        ElevationSessionState.Expired or ElevationSessionState.None => "Stopped",
        ElevationSessionState.Unknown => "Not checked",
        _ => "Off",
    };

    /// <summary>The line under a session's name: what happens next, or time left.</summary>
    public static string Detail(ElevationSessionState state, DateTimeOffset? expires, DateTimeOffset now) => state switch
    {
        ElevationSessionState.Ready when expires is { } e => $"Ready, {FormatRemaining(e - now)} left",
        ElevationSessionState.Ready => "Ready",
        ElevationSessionState.Starting => "Starting… a cold start takes about a minute",
        ElevationSessionState.Expired or ElevationSessionState.None => "Stopped, restarts on next use",
        ElevationSessionState.Unknown => "Not checked yet",
        _ => "Off",
    };

    /// <summary>What a domain's session is used for, so its row reads as more than an internal name.</summary>
    public static string DomainTitle(GraphDomain domain) => domain switch
    {
        GraphDomain.Devices => "Devices (Intune, Microsoft Graph)",
        GraphDomain.Identity => "Identity (Entra ID)",
        GraphDomain.Systems => "Systems (app registrations)",
        GraphDomain.Cloud => "Cloud (SharePoint, Teams)",
        GraphDomain.Security => "Security",
        _ => "Infrastructure",
    };

    /// <summary>One domain's line in the chrome tooltip.</summary>
    public static string DomainLine(GraphDomain domain, ElevationSessionState state,
        DateTimeOffset? expires, string? error, DateTimeOffset now)
    {
        var name = DomainTitle(domain);
        if (state == ElevationSessionState.Unknown && !string.IsNullOrWhiteSpace(error))
            return $"{name}: not checked ({error})";
        return $"{name}: {Detail(state, expires, now)}";
    }

    /// <summary>The toolbar shield's first tooltip line, and its whole tooltip when elevation is off.</summary>
    public const string ShieldTooltipTitle = "Authentication status for every connected system";

    /// <summary>
    /// The toolbar shield's tooltip: what it is, one line on what an elevated
    /// session is, then one line per domain with its state or time left.
    /// </summary>
    public static string ShieldTooltip(IEnumerable<(GraphDomain Domain, ElevationSessionState State, DateTimeOffset? Expires, string? Error)> domains,
        int ttlHours, DateTimeOffset now)
    {
        var lines = new List<string> { ShieldTooltipTitle, Explainer(ttlHours), "" };
        lines.AddRange(domains.Select(d => DomainLine(d.Domain, d.State, d.Expires, d.Error, now)));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The shield's accessible name, which carries the state its dot only shows.</summary>
    public static string ShieldAccessibleName(ElevationSessionState overall) =>
        overall == ElevationSessionState.Off
            ? "Authentication"
            : $"Authentication, elevated sessions {StateLabel(overall).ToLowerInvariant()}";

    private static string FormatRemaining(TimeSpan span)
    {
        if (span <= TimeSpan.Zero) return "0m";
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{Math.Max(1, (int)span.TotalMinutes)}m";
    }
}
