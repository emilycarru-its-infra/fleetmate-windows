namespace FleetMate.Core.Knowledge;

public enum SkillKind { Skill, Hook, Standard }

/// <summary>Where a skill comes from.</summary>
public enum SkillOrigin
{
    /// <summary>The hub every repository inherits, as on main.</summary>
    Shared,
    /// <summary>This PC's own agent setup (<c>%USERPROFILE%\.claude</c>).</summary>
    Local,
}

/// <summary>One skill, hook or standard.</summary>
public sealed record SkillEntry(
    SkillKind Kind,
    SkillOrigin Origin,
    string Name,
    string Summary,
    // Path inside the hub repository, or under ~/.claude for a local one.
    string Path,
    // Files that ship with it, such as scripts beside a SKILL.md.
    IReadOnlyList<string> Files,
    // Markdown to show; a script's source in a code block.
    string Body)
{
    public string Id => $"{Origin}:{Kind}:{Path}";

    /// <summary>Group heading, as on macOS: "Skills · Shared with every repository".</summary>
    public string Group => $"{KindLabel(Kind)} · {OriginLabel(Origin)}";

    public static string KindLabel(SkillKind kind) => kind switch
    {
        SkillKind.Skill => "Skills",
        SkillKind.Hook => "Hooks",
        _ => "Standards",
    };

    public static string OriginLabel(SkillOrigin origin) =>
        origin == SkillOrigin.Shared ? "Shared with every repository" : "On this PC";
}

/// <summary>
/// The shared agent setup every repository inherits (skills, git hooks,
/// scoped standards and the shared AGENTS block), read from FleetMate's copy
/// of the hub repository, plus this PC's own skills and hooks (macOS parity).
/// </summary>
public static class SkillCatalog
{
    /// <summary>Read <c>agents/</c> from the hub copy at <paramref name="hubRoot"/>.</summary>
    public static IReadOnlyList<SkillEntry> Load(string hubRoot)
    {
        var agents = System.IO.Path.Combine(hubRoot, "agents");
        var entries = new List<SkillEntry>();

        foreach (var name in Children(System.IO.Path.Combine(agents, "skills"), directories: true))
        {
            var dir = System.IO.Path.Combine(agents, "skills", name);
            var skillFile = System.IO.Path.Combine(dir, "SKILL.md");
            if (!File.Exists(skillFile)) continue;
            var (fields, body) = FrontMatter.Split(Read(skillFile));
            entries.Add(new SkillEntry(SkillKind.Skill, SkillOrigin.Shared,
                fields.GetValueOrDefault("name") ?? name, fields.GetValueOrDefault("description") ?? "",
                $"agents/skills/{name}/SKILL.md", Extras(dir), body));
        }

        foreach (var name in Children(System.IO.Path.Combine(agents, "githooks"), directories: false))
        {
            var text = Read(System.IO.Path.Combine(agents, "githooks", name));
            var markdown = name.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
            var summary = markdown
                ? FrontMatter.Split(text).Body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#')) ?? ""
                : LeadingComment(text);
            entries.Add(new SkillEntry(SkillKind.Hook, SkillOrigin.Shared, name, summary,
                $"agents/githooks/{name}", Array.Empty<string>(), markdown ? text : CodeBlock(text)));
        }

        var shared = System.IO.Path.Combine(agents, "AGENTS.shared.md");
        if (File.Exists(shared))
            entries.Add(new SkillEntry(SkillKind.Standard, SkillOrigin.Shared, "Estate-wide agent standards",
                "The shared AGENTS.md block every repository carries.", "agents/AGENTS.shared.md",
                Array.Empty<string>(), Read(shared)));

        foreach (var name in Children(System.IO.Path.Combine(agents, "scopes"), directories: false)
                     .Where(n => n.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && n != "README.md"))
        {
            var scope = name[..^3];
            entries.Add(new SkillEntry(SkillKind.Standard, SkillOrigin.Shared, scope, $"Scoped standard: {scope}",
                $"agents/scopes/{name}", Array.Empty<string>(), Read(System.IO.Path.Combine(agents, "scopes", name))));
        }
        return entries;
    }

    /// <summary>
    /// This PC's own skills and hooks: <c>.claude\skills\*\SKILL.md</c> and the
    /// scripts in <c>.claude\hooks</c> under <paramref name="claudeHome"/>.
    /// </summary>
    public static IReadOnlyList<SkillEntry> LoadLocal(string claudeHome)
    {
        var entries = new List<SkillEntry>();
        foreach (var name in Children(System.IO.Path.Combine(claudeHome, "skills"), directories: true))
        {
            var dir = System.IO.Path.Combine(claudeHome, "skills", name);
            var file = System.IO.Path.Combine(dir, "SKILL.md");
            if (!File.Exists(file)) continue;
            var (fields, body) = FrontMatter.Split(Read(file));
            entries.Add(new SkillEntry(SkillKind.Skill, SkillOrigin.Local,
                fields.GetValueOrDefault("name") ?? name, fields.GetValueOrDefault("description") ?? "",
                $"~/.claude/skills/{name}/SKILL.md", Extras(dir), body));
        }
        foreach (var name in Children(System.IO.Path.Combine(claudeHome, "hooks"), directories: false))
        {
            var text = Read(System.IO.Path.Combine(claudeHome, "hooks", name));
            entries.Add(new SkillEntry(SkillKind.Hook, SkillOrigin.Local, name, LeadingComment(text),
                $"~/.claude/hooks/{name}", Array.Empty<string>(), CodeBlock(text)));
        }
        return entries;
    }

    /// <summary>Entries matching <paramref name="filter"/> by name or summary, grouped as macOS groups them.</summary>
    public static IReadOnlyList<(string Group, IReadOnlyList<SkillEntry> Entries)> Grouped(
        IEnumerable<SkillEntry> entries, string? filter)
    {
        var needle = filter?.Trim() ?? "";
        var matching = entries.Where(e => needle.Length == 0
            || e.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || e.Summary.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
        var groups = new List<(string, IReadOnlyList<SkillEntry>)>();
        foreach (var origin in Enum.GetValues<SkillOrigin>())
            foreach (var kind in Enum.GetValues<SkillKind>())
            {
                var rows = matching.Where(e => e.Origin == origin && e.Kind == kind).ToList();
                if (rows.Count > 0) groups.Add(($"{SkillEntry.KindLabel(kind)} · {SkillEntry.OriginLabel(origin)}", rows));
            }
        return groups;
    }

    /// <summary>The first comment of a script, as its one-line summary.</summary>
    public static string LeadingComment(string text)
    {
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n').Take(15))
        {
            var line = raw.Trim();
            if (line.StartsWith("#!")) continue;
            if (line.StartsWith('#') || line.StartsWith("\"\"\"") || line.StartsWith("//") || line.StartsWith("<#") || line.StartsWith("::") || line.StartsWith("REM ", StringComparison.OrdinalIgnoreCase))
            {
                var summary = line.TrimStart('#', '/', '"', '<', ':', ' ').Trim();
                if (summary.StartsWith("REM ", StringComparison.OrdinalIgnoreCase)) summary = summary[4..].Trim();
                // A one-line block comment closes on the same line: "<# ... #>", '""" ... """'.
                summary = summary.TrimEnd('#', '>', '"', ' ');
                if (summary.Length > 0) return summary;
            }
        }
        return "";
    }

    private static string CodeBlock(string text) => $"```\n{text}\n```";

    private static string Read(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static IReadOnlyList<string> Extras(string dir) =>
        Children(dir, directories: null).Where(n => n != "SKILL.md").ToList();

    /// <summary>Names in <paramref name="dir"/>, sorted, hidden ones left out. <paramref name="directories"/> null lists both.</summary>
    private static IEnumerable<string> Children(string dir, bool? directories)
    {
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        try
        {
            IEnumerable<string> paths = directories switch
            {
                true => Directory.EnumerateDirectories(dir),
                false => Directory.EnumerateFiles(dir),
                null => Directory.EnumerateFileSystemEntries(dir),
            };
            return paths.Select(p => System.IO.Path.GetFileName(p)!)
                .Where(n => !n.StartsWith('.'))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
