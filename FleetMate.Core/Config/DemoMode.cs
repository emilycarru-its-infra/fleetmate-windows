namespace FleetMate.Core.Config;

/// <summary>
/// Demo mode: the app serves fictional fixture data, makes no live calls and
/// shows no signed-in identity. It is for screenshots and UI QA in a public
/// repository, where a capture must never carry a real ticket, person,
/// device, repository, account or tenant.
/// </summary>
public static class DemoMode
{
    /// <summary>The environment variable that turns demo mode on when set to 1.</summary>
    public const string EnvironmentVariable = "FLEETMATE_DEMO";

    /// <summary>
    /// True for <c>--demo</c>, for <c>--capture</c> (a capture is always a
    /// demo), or for <c>FLEETMATE_DEMO=1</c>.
    /// </summary>
    public static bool IsRequested(IEnumerable<string> args, string? environmentValue) =>
        environmentValue?.Trim() == "1"
        || args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase)
                      || a.Equals("--capture", StringComparison.OrdinalIgnoreCase));

    /// <summary>This process's arguments and environment.</summary>
    public static bool IsRequested() =>
        IsRequested(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>
    /// Why a screenshot may not be written, or null when it may: only in demo
    /// mode, and only with no live service built and no one signed in.
    /// </summary>
    public static string? CaptureRefusal(bool demoMode, bool liveSession) =>
        !demoMode ? "Not in demo mode; captures show only fictional fixture data."
        : liveSession ? "A live service is connected or signed in; captures show only fictional fixture data."
        : null;
}
