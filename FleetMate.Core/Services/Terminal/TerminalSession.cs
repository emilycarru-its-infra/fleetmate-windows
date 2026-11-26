namespace FleetMate.Core.Services.Terminal;

/// <summary>
/// One row in the terminal's session list and the panes it runs. A row can
/// wait to start (a Repos row, one reopened from the last quit) and has no
/// panes until it does; a started row has one pane, or two when split.
/// </summary>
public sealed class TerminalSession
{
    /// <summary>The command's label ("codex", "shell"): the name of last resort.</summary>
    public required string Label { get; set; }
    public string? CustomName { get; set; }
    /// <summary>The title the agent wrote to its session file.</summary>
    public string? AutomaticName { get; set; }
    /// <summary>The row's name until the program titles it, such as <c>owner/repo</c> for a Repos row.</summary>
    public string? WorkspaceLabel { get; set; }
    public string Section { get; set; } = TerminalSessionList.DefaultSection;
    /// <summary>What it runs, as a workspace saves it.</summary>
    public string Command { get; set; } = "";
    /// <summary>For a Repos row with no command of its own: it runs the default launch as it is when it starts or resumes.</summary>
    public bool FollowsDefault { get; set; }
    /// <summary>For a session the person added to a Repos section, such as a duplicate: its id in the Repos workspace.</summary>
    public string? AddedId { get; set; }
    /// <summary>For a row from the Repos workspace: its folder, normalized, the key renames and moves are written back by. Null for the person's own sessions.</summary>
    public string? RepoFolder { get; set; }
    /// <summary>Where a row that has not started will start.</summary>
    public string? StartDirectory { get; set; }
    public bool Pinned { get; set; }
    /// <summary>Resume the agent's last conversation when this row next starts.</summary>
    public bool Resume { get; set; }
    /// <summary>When the current process started, and whether it was a resume, to fall back to a fresh start when resuming fails.</summary>
    public DateTime StartedAt { get; set; }
    public bool StartedResuming { get; set; }
    public bool IsStarted => Panes.Count > 0;
    public bool IsRepoRow => RepoFolder != null;
    public List<TerminalPaneController> Panes { get; } = new();

    /// <summary>
    /// The row's name: a name the person typed, then the agent's own, the task
    /// in the terminal title, the row's label, and last what it runs.
    /// </summary>
    public string DisplayName => TerminalSessionName.Resolve(CustomName, AutomaticName,
        TerminalTitleTask.Parse(Panes.FirstOrDefault()?.ProgramTitle).Task, WorkspaceLabel, Label);

    /// <summary>The folder the row is in now, or will start in.</summary>
    public string? Folder => Panes.FirstOrDefault()?.Directory is { Length: > 0 } dir ? dir : StartDirectory;

    /// <summary>The session's state across its panes: a bell wins, then output, then idle; ended only when every pane has.</summary>
    public ActivityState StateAt(DateTime now) => Combine(Panes.Select(p => p.Activity.State(now)));

    public static ActivityState Combine(IEnumerable<ActivityState> panes)
    {
        var states = panes.ToList();
        return states.Contains(ActivityState.Attention) ? ActivityState.Attention
            : states.Contains(ActivityState.Active) ? ActivityState.Active
            : states.All(s => s == ActivityState.Exited) ? ActivityState.Exited
            : ActivityState.Idle;
    }

    /// <summary>Started and not ended: what the header counts count.</summary>
    public bool IsRunning => IsStarted && StateAt(DateTime.UtcNow) != ActivityState.Exited;

    /// <summary>Whether the session's program is working now, so restarting it would interrupt.</summary>
    public bool IsBusy => Panes.FirstOrDefault() is { } lead
        && (StateAt(DateTime.UtcNow) == ActivityState.Active || TerminalTitleTask.Parse(lead.ProgramTitle).Working == true);

    /// <summary>What closing the row does, as its menu and close button say it.</summary>
    public string CloseTitle => CloseTitleFor(IsRepoRow, IsStarted);

    public static string CloseTitleFor(bool isRepoRow, bool isStarted) =>
        !isRepoRow ? "Close Session"
        : isStarted ? "Stop Session"
        : "Remove from List";
}

/// <summary>How a row's activity dot is drawn.</summary>
public enum TerminalDot { Waiting, Idle, Running, Attention }

/// <summary>Everything a session row shows, worked out once for both desktop apps.</summary>
public sealed record TerminalRowState(
    string Title, string Breadcrumb, string Status, string Glyph, bool StatusVisible,
    TerminalDot Dot, string AutomationName, string ToolTip, string Hint, string CloseTitle);

/// <summary>One line of the session list, in order.</summary>
public abstract record TerminalListEntry;

/// <summary>A section's header, or the Pinned group's (<see cref="IsPinned"/>).</summary>
public sealed record TerminalSectionEntry(string Name, int Running, bool Collapsed, bool IsPinned,
    bool CanRename, bool CanMoveUp, bool CanMoveDown) : TerminalListEntry
{
    /// <summary>"▾ Sessions (2)", "▸ Pinned".</summary>
    public string Header => IsPinned
        ? $"{(Collapsed ? "▸" : "▾")} Pinned{(Running > 0 ? $" ({Running})" : "")}"
        : $"{(Collapsed ? "▸" : "▾")} {Name} ({Running})";
}

public sealed record TerminalSessionEntry(TerminalSession Session) : TerminalListEntry;

/// <summary>Filters hide every row: "No Matching Sessions" and Clear Filters.</summary>
public sealed record TerminalEmptyEntry : TerminalListEntry;

/// <summary>Which status filter a button toggles.</summary>
public enum TerminalFilterKind { Active, Running, Paused }

/// <summary>A status filter button: an icon and a neutral count, tinted while it is on.</summary>
public sealed record TerminalFilterButton(TerminalFilterKind Kind, string Glyph, string Name, bool On, int Count)
{
    public string CountText => Count > 0 ? Count.ToString(System.Globalization.CultureInfo.CurrentCulture) : "";
    /// <summary>One that is on stays clickable so it can be turned off with no matches.</summary>
    public bool IsEnabled => On || Count > 0;
    public string ToolTip => On ? "Show all sessions" : Kind switch
    {
        TerminalFilterKind.Active => "Show only sessions running an agent",
        TerminalFilterKind.Running => "Show only sessions that are working",
        _ => "Show only agents waiting on you",
    };
    public string AutomationName => $"{Name}, {Count} {(Count == 1 ? "session" : "sessions")}{(On ? ", on" : "")}";
    public string AutomationId => "TerminalFilter" + Kind;
}
