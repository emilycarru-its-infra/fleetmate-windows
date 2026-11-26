namespace FleetMate.Core.Shared;

/// <summary>
/// How a widget click becomes a list filter, shared by both Windows apps:
/// a widget names a value as the Mac does ("Non-Compliant", "Macintosh"),
/// and the list's filter holds the service's own spelling.
/// </summary>
public static class WidgetFilterValues
{
    /// <summary>
    /// The filter values a widget click selects: every value in
    /// <paramref name="available"/> whose label (through <paramref name="display"/>,
    /// when given) matches <paramref name="incoming"/>, ignoring case and
    /// punctuation — "Non-Compliant" finds "Noncompliant", and "Macintosh"
    /// finds "macOS". With no match the incoming value is used as it is.
    /// </summary>
    public static string[] Match(string incoming, IEnumerable<string> available, Func<string, string>? display = null)
    {
        static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var want = Norm(incoming);
        var matches = available.Where(v => Norm(display?.Invoke(v) ?? v) == want).ToArray();
        return matches.Length > 0 ? matches : new[] { incoming };
    }
}
