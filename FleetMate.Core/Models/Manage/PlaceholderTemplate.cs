using System.Text.RegularExpressions;

namespace FleetMate.Core.Models.Manage;

/// <summary>
/// A command with <c>&lt;PLACEHOLDER&gt;</c> tokens that must be filled in
/// before it runs. Placeholders are upper-case words in angle brackets, for
/// example <c>&lt;USERNAME&gt;</c> or <c>&lt;PACKAGE_NAME&gt;</c>. Any placeholder whose
/// name contains PASSWORD is sensitive: it is entered masked and redacted in previews.
/// </summary>
public class PlaceholderTemplate
{
    private static readonly Regex TokenPattern = new(@"<([A-Z][A-Z0-9_]*)>", RegexOptions.Compiled);

    public string Label { get; }
    public string Command { get; }
    public IReadOnlyList<string> Placeholders { get; }

    private PlaceholderTemplate(string label, string command, IReadOnlyList<string> placeholders)
    {
        Label = label;
        Command = command;
        Placeholders = placeholders;
    }

    /// <summary>Returns null when the command has no placeholders.</summary>
    public static PlaceholderTemplate? Detect(string label, string command)
    {
        var found = new List<string>();
        foreach (Match m in TokenPattern.Matches(command ?? ""))
        {
            var token = m.Value;
            if (!found.Contains(token)) found.Add(token);
        }
        return found.Count == 0 ? null : new PlaceholderTemplate(label, command!, found);
    }

    public static bool IsSensitive(string placeholder) =>
        placeholder.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)
        || placeholder.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
        || placeholder.Contains("TOKEN", StringComparison.OrdinalIgnoreCase);

    public bool HasSensitive => Placeholders.Any(IsSensitive);

    /// <summary>Human label for a prompt field: USERNAME becomes "Username", PACKAGE_NAME "Package name".</summary>
    public static string FieldLabel(string placeholder)
    {
        var name = placeholder.Trim('<', '>').Replace('_', ' ').ToLowerInvariant();
        return name.Length == 0 ? placeholder : char.ToUpperInvariant(name[0]) + name[1..];
    }

    public bool IsComplete(IReadOnlyDictionary<string, string> values) =>
        Placeholders.All(p => values.TryGetValue(p, out var v) && !string.IsNullOrWhiteSpace(v));

    /// <summary>
    /// Substitute values. Values are quoted for PowerShell as single-quoted
    /// literals only when the placeholder sits bare (not already inside quotes),
    /// so library authors can write either <c>'&lt;USERNAME&gt;'</c> or <c>&lt;USERNAME&gt;</c>.
    /// With redactSensitive the sensitive values are replaced by bullets for display.
    /// </summary>
    public string Resolve(IReadOnlyDictionary<string, string> values, bool redactSensitive = false)
    {
        var result = Command;
        foreach (var placeholder in Placeholders)
        {
            values.TryGetValue(placeholder, out var value);
            value ??= "";
            var replacement = redactSensitive && IsSensitive(placeholder) ? "••••••••" : value;

            // Quoted occurrence: '<X>' or "<X>" -> keep the author's quotes, escape for them.
            result = result.Replace("'" + placeholder + "'", "'" + EscapeSingleQuoted(replacement) + "'");
            result = result.Replace("\"" + placeholder + "\"", "\"" + EscapeDoubleQuoted(replacement) + "\"");
            // Bare occurrence -> single-quoted literal.
            result = result.Replace(placeholder, "'" + EscapeSingleQuoted(replacement) + "'");
        }
        return result;
    }

    internal static string EscapeSingleQuoted(string value) => value.Replace("'", "''");

    internal static string EscapeDoubleQuoted(string value) =>
        value.Replace("`", "``").Replace("\"", "`\"").Replace("$", "`$");
}
