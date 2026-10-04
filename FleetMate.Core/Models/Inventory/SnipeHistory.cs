using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FleetMate.Core.Models.Inventory;

/// <summary>One field change inside an activity row's log_meta.</summary>
public sealed record SnipeFieldChange(string Field, string? Old, string? New);

/// <summary>An attached file on an activity row (upload events).</summary>
public sealed record SnipeHistoryFile(string Name, string? Url);

/// <summary>
/// One asset history event, flattened from a Snipe-IT activity row into what
/// the History tab draws: action, who, target, when, field changes, note, file.
/// </summary>
public sealed class SnipeHistoryEntry
{
    public int Id { get; init; }
    public string Action { get; init; } = "";
    public string? By { get; init; }
    public string? Target { get; init; }
    public string? Date { get; init; }
    public string? Note { get; init; }
    public SnipeHistoryFile? File { get; init; }
    public IReadOnlyList<SnipeFieldChange> Changes { get; init; } = Array.Empty<SnipeFieldChange>();

    /// <summary>Everything the filter box matches against, lowercased.</summary>
    public string SearchText => string.Join(" ", new[] { Action, By, Target, Date, Note, File?.Name }
        .Concat(Changes.SelectMany(c => new[] { c.Field, c.Old, c.New }))
        .Where(s => !string.IsNullOrEmpty(s))).ToLowerInvariant();
}

/// <summary>
/// Turns raw Snipe-IT activity rows into <see cref="SnipeHistoryEntry"/>s.
/// log_meta arrives as an object of {field: {old, new}} — or as [] when a
/// row has no changes, or with bare scalar values — and every shape is
/// tolerated rather than thrown on.
/// </summary>
public static partial class SnipeHistory
{
    public static SnipeHistoryEntry ToEntry(SnipeActivity row) => new()
    {
        Id = row.Id,
        Action = string.IsNullOrWhiteSpace(row.ActionType) ? "activity" : row.ActionType!.Trim(),
        By = Blank(row.CreatedBy?.Name) ?? Blank(row.Admin?.Name),
        Target = Blank(row.Target?.Name),
        Date = Blank(row.ActionDate?.DateTime) ?? Blank(row.CreatedAt?.DateTime),
        Note = Blank(StripTags(row.Note)),
        File = ParseFile(row.File),
        Changes = ParseChanges(row.LogMeta),
    };

    /// <summary>
    /// Field label from a log_meta key: custom fields arrive as their db column
    /// (<c>_snipeit_chip_7</c>), so drop the prefix and the trailing field id,
    /// then title-case the rest — "Chip". Built-in keys just title-case.
    /// </summary>
    public static string FieldLabel(string key)
    {
        var name = key;
        if (name.StartsWith("_snipeit_", StringComparison.OrdinalIgnoreCase))
        {
            name = name["_snipeit_".Length..];
            name = TrailingId().Replace(name, "");
        }
        var words = name.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => Acronyms.Contains(w) ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w[1..]);
        var label = string.Join(" ", words);
        return label.Length == 0 ? key : label;
    }

    /// <summary>Key words that read as acronyms, so "intune_id" is "Intune ID", not "Intune Id".</summary>
    private static readonly HashSet<string> Acronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "cpu", "gpu", "npu", "eol", "imei", "upn", "mac", "ip", "os", "url", "uuid",
    };

    public static IReadOnlyList<SnipeFieldChange> ParseChanges(object? logMeta)
    {
        if (logMeta is not JsonElement meta) return Array.Empty<SnipeFieldChange>();

        // Older rows store log_meta as a JSON string of the object.
        if (meta.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var doc = JsonDocument.Parse(meta.GetString() ?? "");
                return ParseChanges(doc.RootElement.Clone());
            }
            catch (JsonException) { return Array.Empty<SnipeFieldChange>(); }
        }
        if (meta.ValueKind != JsonValueKind.Object) return Array.Empty<SnipeFieldChange>();

        var changes = new List<SnipeFieldChange>();
        foreach (var prop in meta.EnumerateObject())
        {
            string? oldValue = null, newValue;
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                oldValue = prop.Value.TryGetProperty("old", out var o) ? Scalar(o) : null;
                newValue = prop.Value.TryGetProperty("new", out var n) ? Scalar(n) : null;
            }
            else
            {
                newValue = Scalar(prop.Value);
            }
            changes.Add(new SnipeFieldChange(FieldLabel(prop.Name), oldValue, newValue));
        }
        return changes;
    }

    /// <summary>Unescape HTML entities; strip tags only where asked (notes).</summary>
    public static string? CleanValue(string? value) =>
        value == null ? null : WebUtility.HtmlDecode(value).Trim();

    public static string? StripTags(string? html)
    {
        if (string.IsNullOrEmpty(html)) return html;
        var text = BreakTag().Replace(html, "\n");
        text = AnyTag().Replace(text, "");
        return WebUtility.HtmlDecode(text).Trim();
    }

    private static SnipeHistoryFile? ParseFile(object? file)
    {
        if (file is not JsonElement f) return null;
        if (f.ValueKind == JsonValueKind.String)
            return Blank(f.GetString()) is { } s ? new SnipeHistoryFile(s, null) : null;
        if (f.ValueKind != JsonValueKind.Object) return null;

        var url = f.TryGetProperty("url", out var u) ? Scalar(u) : null;
        var name = f.TryGetProperty("filename", out var n) ? Scalar(n) : null;
        name ??= url != null ? Uri.UnescapeDataString(url.Split('/').Last()) : null;
        return name == null ? null : new SnipeHistoryFile(name, url);
    }

    private static string? Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => CleanValue(e.GetString()),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => e.GetRawText(),
        JsonValueKind.Object when e.TryGetProperty("name", out var n) => Scalar(n),
        JsonValueKind.Object or JsonValueKind.Array => e.GetRawText(),
        _ => null,
    };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    [GeneratedRegex(@"_\d+$")]
    private static partial Regex TrailingId();

    [GeneratedRegex(@"<br\s*/?>|</p>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTag();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();
}
