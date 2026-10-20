namespace FleetMate.Core.Services.Terminal;

/// <summary>
/// The session row's Switch Agent and Duplicate Session menus, and how the
/// Repos rows are written back to the Repos workspace.
/// </summary>
public static class TerminalSessionActions
{
    /// <summary>The agents a session can switch to or be duplicated as, in menu order, with the names Settings gives them.</summary>
    public static readonly IReadOnlyList<(string Command, string Label)> Presets = new[]
    {
        ("claude-remote", "Claude (remote)"),
        ("codex-remote", "Codex (remote)"),
        ("claude", "Claude"),
        ("codex", "Codex"),
        (AgentCommands.Shell, "Shell"),
    };

    /// <summary>Whether a session running <paramref name="command"/> runs the preset <paramref name="preset"/>; "" and "shell" are both the shell.</summary>
    public static bool IsCurrent(string preset, string? command) =>
        string.Equals(Normalize(preset), Normalize(command), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? command)
    {
        var value = command?.Trim() ?? "";
        return value.Equals(AgentCommands.Shell, StringComparison.OrdinalIgnoreCase) ? "" : value;
    }

    /// <summary>A duplicate's name: "<paramref name="name"/> 2", or the next number free among <paramref name="taken"/>.</summary>
    public static string DuplicateName(string name, IEnumerable<string> taken)
    {
        var names = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 2; ; n++)
        {
            var candidate = $"{name} {n}";
            if (!names.Contains(candidate)) return candidate;
        }
    }

    /// <summary>A new id for a session the person adds to the Repos workspace.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// A Repos row as the list holds it. <paramref name="Command"/> is null
    /// when the row follows the default launch; <paramref name="AddedId"/> is
    /// set for a session the person added.
    /// </summary>
    public sealed record RepoRow(string Key, string Section, string? Label, string? Command, string? StartDirectory,
        string? Name, bool Pinned, string? AddedId = null);

    /// <summary>
    /// Write the Repos rows back to the workspace by key: names, pins,
    /// commands, sections and their order and collapsed state, and the rows
    /// the person removed (they stay out, since the workspace remembers
    /// placing them). Sessions written by hand without a folder keep their
    /// section. Returns whether the workspace changed.
    /// </summary>
    public static bool WriteBack(TerminalWorkspace workspace, IReadOnlyList<string> sectionOrder,
        IReadOnlyCollection<string> collapsed, IReadOnlyList<RepoRow> rows)
    {
        var before = TerminalWorkspaceStore.Serialize(workspace);
        var byKey = new Dictionary<string, TerminalWorkspaceSession>(StringComparer.Ordinal);
        foreach (var entry in workspace.Sections.SelectMany(s => s.Sessions))
            if (entry.Directory is { Length: > 0 }) byKey.TryAdd(TerminalWorkspaceBaseline.RowKey(entry), entry);

        var sections = new List<TerminalWorkspaceSection>();
        foreach (var name in sectionOrder)
        {
            var old = workspace.Sections.FirstOrDefault(s => s.Name == name);
            var members = rows.Where(r => r.Section == name).ToList();
            var unkeyed = old?.Sessions.Where(e => e.Directory is not { Length: > 0 }).ToList() ?? new();
            if (members.Count == 0 && unkeyed.Count == 0 && old is not { Sessions.Count: 0 }) continue;
            var section = new TerminalWorkspaceSection { Name = name, Collapsed = collapsed.Contains(name) ? true : null };
            foreach (var row in members)
            {
                var entry = byKey.TryGetValue(row.Key, out var existing) ? existing : new TerminalWorkspaceSession
                {
                    Label = row.Label,
                    Directory = row.StartDirectory is { } start ? TerminalWorkspaceStore.Abbreviate(start) : null,
                    Added = row.AddedId != null ? true : null,
                    Id = row.AddedId,
                };
                entry.Name = row.Name;
                entry.Pinned = row.Pinned ? true : null;
                entry.Command = row.Command;
                section.Sessions.Add(entry);
            }
            section.Sessions.AddRange(unkeyed);
            sections.Add(section);
        }
        workspace.Sections = sections;
        return TerminalWorkspaceStore.Serialize(workspace) != before;
    }
}
