namespace FleetMate.Core.Services.Terminal;

/// <summary>What one terminal pane runs, and where.</summary>
public sealed record TerminalLaunch(string Title, TerminalCommand Command, string? WorkingDirectory, RepoLocation? Repo = null)
{
    /// <summary>Take keyboard focus once ready. A session opened by the person does; one auto-started at launch does not.</summary>
    public bool TakeFocus { get; init; } = true;

    /// <summary>What the person chose to run ("codex", "claude", a custom command; "" for the shell), saved with a workspace.</summary>
    public string AgentCommand { get; init; } = "";

    /// <summary>
    /// Demo mode's fictional output, shown in place of a program. A pane with
    /// no <see cref="AgentSessionHost"/> never starts a process; it shows this.
    /// </summary>
    public string? DemoTranscript { get; init; }
}
