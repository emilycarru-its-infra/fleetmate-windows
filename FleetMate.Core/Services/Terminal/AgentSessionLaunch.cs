namespace FleetMate.Core.Services.Terminal;

/// <summary>
/// Turns what a terminal session would run into what it does run: the agent
/// handed its brief, and the session's own environment variables.
/// </summary>
public static class AgentSessionLaunch
{
    /// <summary>
    /// The command and session variables for <paramref name="command"/>.
    /// <paramref name="cliUpdatesManaged"/> is true when FleetMate keeps the
    /// agent CLIs current, so they skip their own update checks.
    /// </summary>
    public static (TerminalCommand Command, Dictionary<string, string> Environment) Prepare(
        TerminalCommand command, AgentSessionBrief brief, bool cliUpdatesManaged, bool codexNoDaemon,
        Func<string, string?> findOnPath)
    {
        var environment = new Dictionary<string, string>
        {
            // What FleetMate is and every fleetmate command, for any agent.
            [AgentBrief.BriefVariable] = brief.BriefPath,
        };
        // FleetMate updates Claude Code in the background, so it must not
        // update itself mid-session too.
        if (cliUpdatesManaged) environment["DISABLE_AUTOUPDATER"] = "1";

        var agent = AgentBrief.AgentOf(command.FileName);
        if (agent == null) return (command, environment);

        // Codex from npm is a .cmd, which only runs through cmd.exe; run its
        // script under node instead so the brief's text reaches Codex as it
        // is. Where that is not possible, Codex gets the fixed instruction to
        // read the brief file, which carries nothing cmd.exe could misread.
        TerminalCommand? unwrapped = agent == "codex" ? NpmShim.Unwrap(command, findOnPath) : null;
        var throughCmd = unwrapped == null && Path.GetExtension(command.FileName).Equals(".cmd", StringComparison.OrdinalIgnoreCase);
        var instructions = throughCmd ? AgentBrief.PointerInstructions : brief.CodexInstructions;

        var arguments = AgentBrief.LaunchArguments(command.FileName, command.Arguments, brief.BriefPath, instructions,
            selfUpdate: !cliUpdatesManaged, codexNoDaemon: codexNoDaemon);
        var result = unwrapped != null
            ? new TerminalCommand(unwrapped.FileName, unwrapped.Arguments.Take(1).Concat(arguments).ToList())
            : new TerminalCommand(command.FileName, arguments);
        return (result, environment);
    }
}
