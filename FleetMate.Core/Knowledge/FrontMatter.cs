namespace FleetMate.Core.Knowledge;

/// <summary>
/// The <c>---</c> YAML block at the top of a Markdown page, read as flat
/// <c>key: value</c> pairs: enough for titles, slugs and dates without a YAML
/// dependency for nested values nobody here needs.
/// </summary>
public static class FrontMatter
{
    public static (Dictionary<string, string> Fields, string Body) Split(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalized = text.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---")) return (fields, normalized);

        var lines = normalized.Split('\n');
        var end = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---") { end = i; break; }
        }
        if (end < 0) return (fields, normalized);

        for (var i = 1; i < end; i++)
        {
            var line = lines[i];
            if (line.StartsWith(' ') || line.StartsWith('\t')) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1];
            if (key.Length > 0 && value.Length > 0) fields[key] = value;
        }
        return (fields, string.Join('\n', lines[(end + 1)..]));
    }
}
