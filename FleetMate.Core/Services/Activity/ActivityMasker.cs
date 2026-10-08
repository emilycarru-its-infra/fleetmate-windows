using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FleetMate.Core.Services.Activity;

/// <summary>
/// Replaces device identifiers with stable placeholders so an activity log can
/// be attached to a bug report: serial numbers become SERIAL-1, SERIAL-2…,
/// UDIDs and hardware UUIDs UDID-n, hardware (MAC) addresses MAC-n, email
/// addresses USER-n and every host outside a short public list host-n. The same
/// value always maps to the same placeholder within one masker.
/// </summary>
public sealed partial class ActivityMasker
{
    /// <summary>Hosts that say nothing about the organization using FleetMate.</summary>
    public static readonly IReadOnlySet<string> PublicHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "graph.microsoft.com", "login.microsoftonline.com", "management.azure.com",
        "api.github.com", "github.com",
        "api-school.apple.com", "api-business.apple.com", "mdmenrollment.apple.com",
    };

    private readonly Dictionary<string, string> _placeholders = new();
    private readonly Dictionary<string, int> _counters = new();
    private readonly List<string> _knownSerials;

    public ActivityMasker(IEnumerable<string>? knownSerials = null)
    {
        // Longest first, so a serial that contains another is replaced whole.
        _knownSerials = (knownSerials ?? Enumerable.Empty<string>())
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(s => s.Length)
            .ToList();
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b")]
    private static partial Regex HardwareAddress();

    [GeneratedRegex(@"\b[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\b")]
    private static partial Regex Guid();

    [GeneratedRegex(@"\b[0-9A-Fa-f]{8}-[0-9A-Fa-f]{16}\b")]
    private static partial Regex AppleUdid();

    [GeneratedRegex(@"\b[0-9A-Fa-f]{40}\b")]
    private static partial Regex LegacyUdid();

    /// <summary>
    /// Upper-case letters and digits, 8 to 14 long, with at least one of each:
    /// the shape of Apple and most PC serial numbers.
    /// </summary>
    [GeneratedRegex(@"\b(?=[A-Z0-9]*[A-Z])(?=[A-Z0-9]*[0-9])[A-Z0-9]{8,14}\b")]
    private static partial Regex SerialShape();

    [GeneratedRegex(@"serial(?:Number|_number)?\s*(?:=|eq\s*)\s*'?([A-Za-z0-9-]{5,20})'?", RegexOptions.IgnoreCase)]
    private static partial Regex QuerySerial();

    public string Mask(string text)
    {
        var result = Replace(Email(), text, "USER");
        result = Replace(HardwareAddress(), result, "MAC");
        result = Replace(Guid(), result, "UDID");
        result = Replace(AppleUdid(), result, "UDID");
        result = Replace(LegacyUdid(), result, "UDID");
        foreach (var serial in _knownSerials)
        {
            result = Regex.Replace(result, Regex.Escape(serial),
                _ => Placeholder(serial.ToUpperInvariant(), "SERIAL"), RegexOptions.IgnoreCase);
        }
        return Replace(SerialShape(), result, "SERIAL");
    }

    public string MaskHost(string host)
    {
        if (string.IsNullOrEmpty(host) || PublicHosts.Contains(host)) return host;
        return Placeholder(host.ToLowerInvariant(), "host");
    }

    private string Replace(Regex regex, string text, string kind) =>
        regex.Replace(text, match =>
        {
            var value = match.Value;
            if (_placeholders.ContainsValue(value)) return value;
            var key = kind == "MAC" ? value.ToUpperInvariant().Replace('-', ':') : value.ToUpperInvariant();
            return Placeholder(key, kind);
        });

    private string Placeholder(string value, string kind)
    {
        var key = $"{kind}:{value}";
        if (_placeholders.TryGetValue(key, out var existing)) return existing;
        var next = _counters.GetValueOrDefault(kind) + 1;
        _counters[kind] = next;
        var label = $"{kind}-{next}";
        _placeholders[key] = label;
        return label;
    }

    /// <summary>
    /// Serial numbers a request's query names, read before the query is
    /// dropped: <c>$filter=serialNumber eq 'X'</c>, <c>serial=X</c>.
    /// </summary>
    public static IEnumerable<string> SerialsInQuery(Uri? url)
    {
        if (url == null) return Enumerable.Empty<string>();
        var raw = url.IsAbsoluteUri ? url.Query : (url.OriginalString.Split('?', 2) is [_, var q] ? q : "");
        if (string.IsNullOrEmpty(raw)) return Enumerable.Empty<string>();
        var query = Uri.UnescapeDataString(raw.TrimStart('?'));
        return QuerySerial().Matches(query).Select(m => m.Groups[1].Value.ToUpperInvariant()).ToList();
    }

    /// <summary>The log as plain text with every identifier masked.</summary>
    public static string Export(IEnumerable<ActivityAction> actions, DateTimeOffset? at = null)
    {
        var list = actions.ToList();
        var masker = new ActivityMasker(list.SelectMany(a => a.Serials));
        var text = new StringBuilder();
        text.Append("FleetMate Activity Log (masked), exported ")
            .Append((at ?? DateTimeOffset.Now).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
            .Append("\n\n");
        foreach (var action in list)
        {
            var serials = action.Serials.Count == 0 ? "" : " [" + string.Join(", ", action.Serials.Select(masker.Mask)) + "]";
            text.Append(action.StartedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture))
                .Append("  ").Append(action.Service)
                .Append("  ").Append(masker.Mask(action.Title))
                .Append("  ").Append(masker.Mask(action.Result))
                .Append(serials).Append('\n');
            foreach (var request in action.Requests)
            {
                var ms = (int)Math.Round(request.Duration.TotalMilliseconds);
                text.Append("    ").Append(request.Method).Append(' ')
                    .Append(masker.MaskHost(request.Host)).Append(masker.Mask(request.Path))
                    .Append("  ").Append(masker.Mask(request.StatusText))
                    .Append("  ").Append(ms).Append(" ms\n");
            }
        }
        return text.ToString();
    }
}
