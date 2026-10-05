using System.Text.RegularExpressions;
using FleetMate.Core.Models.Devices;

namespace FleetMate.Core.Services.Devices;

/// <summary>How an identifier resolved: one device, none, several, or an identifier refused outright.</summary>
public sealed record DeviceResolution(IntuneDevice? Device, IReadOnlyList<IntuneDevice> Candidates, string? Error)
{
    public static DeviceResolution One(IntuneDevice device) => new(device, new[] { device }, null);
    public static DeviceResolution None() => new(null, Array.Empty<IntuneDevice>(), null);
    public static DeviceResolution Many(IReadOnlyList<IntuneDevice> candidates) => new(null, candidates, null);
    public static DeviceResolution Refused(string error) => new(null, Array.Empty<IntuneDevice>(), error);

    public bool IsAmbiguous => Device == null && Candidates.Count > 1;
    public bool IsNotFound => Device == null && Error == null && Candidates.Count == 0;
}

/// <summary>
/// The rules every device-targeting CLI command follows: a GUID is taken only
/// as a GUID, a serial only as [A-Za-z0-9-], every lookup is an exact eq, and
/// every $filter literal is escaped. There is no fallback from serial to name.
/// Local to the CLI port until the shared ODataFilter / DestructiveTargetResolver
/// lands on main, which these should then call.
/// </summary>
public static partial class CliTargets
{
    public static bool IsGuid(string identifier) => Guid.TryParseExact(identifier.Trim(), "D", out _);

    public static bool IsSerial(string identifier) => SerialPattern().IsMatch(identifier.Trim());

    /// <summary>An OData string literal: single quotes doubled, then wrapped.</summary>
    public static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>The exact-match filter for a serial, or null when the serial isn't acceptable.</summary>
    public static string? SerialFilter(string serial) =>
        IsSerial(serial) ? $"serialNumber eq {Literal(serial.Trim())}" : null;

    public const string RefusedIdentifier =
        "Give a managedDevice id (a GUID) or a serial number (letters, digits and hyphens only).";

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex SerialPattern();
}
