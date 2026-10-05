using System.Text.RegularExpressions;

namespace FleetMate.Core.Services;

/// <summary>An identifier rejected before it reached a Graph filter.</summary>
public sealed class ODataValidationException(string message) : ArgumentException(message);

/// <summary>
/// Builds the values interpolated into Graph <c>$filter</c> expressions.
/// Every string a person or another system typed goes through one of these,
/// so a quote in a name can't close the literal and change the query. The
/// caller URL-encodes the finished filter exactly once.
/// </summary>
public static partial class ODataFilter
{
    /// <summary>A quoted string literal, with single quotes doubled as OData requires.</summary>
    public static string Literal(string value) => "'" + (value ?? "").Replace("'", "''") + "'";

    /// <summary>A serial number as a literal: letters, digits and hyphens only.</summary>
    public static string Serial(string serial)
    {
        var trimmed = (serial ?? "").Trim();
        if (!IsSerial(trimmed))
            throw new ODataValidationException($"\"{serial}\" is not a serial number (letters, digits and hyphens only).");
        return Literal(trimmed);
    }

    /// <summary>A GUID literal, normalised to its canonical form.</summary>
    public static string Guid(string id)
    {
        if (!System.Guid.TryParse((id ?? "").Trim(), out var guid))
            throw new ODataValidationException($"\"{id}\" is not an id (a GUID).");
        return Literal(guid.ToString("D"));
    }

    /// <summary>A user principal name literal: one @ with something on each side, no spaces.</summary>
    public static string Upn(string upn)
    {
        var trimmed = (upn ?? "").Trim();
        if (!UpnShape().IsMatch(trimmed))
            throw new ODataValidationException($"\"{upn}\" is not a user principal name.");
        return Literal(trimmed);
    }

    public static bool IsSerial(string? value) =>
        !string.IsNullOrWhiteSpace(value) && SerialShape().IsMatch(value.Trim());

    public static bool IsGuid(string? value) => System.Guid.TryParse((value ?? "").Trim(), out _);

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex SerialShape();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+$")]
    private static partial Regex UpnShape();
}
