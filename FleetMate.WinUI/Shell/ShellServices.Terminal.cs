using FleetMate.Core.Config;
using FleetMate.Core.Services.Terminal;

namespace FleetMate.WinUI.Shell;

/// <summary>
/// The Agent Terminal's services: the FLEETMATE_CONTEXT file every session
/// reads (<see cref="AgentContext"/>, which Development already reports to),
/// and the host that writes each session's brief and keeps the agent CLIs
/// current. Neither exists in demo mode or in TicketsMate, so a capture can
/// never start a shell or an agent, or write the context file.
/// </summary>
public sealed partial class ShellServices
{
    /// <summary>The brief, start directory and CLI updates; null in demo mode and TicketsMate.</summary>
    public AgentSessionHost? AgentHost { get; private set; }

    /// <summary>Called from the constructor, on the UI thread, outside demo mode.</summary>
    private void InitializeTerminal(FleetMateConfig config)
    {
        if (AppEdition.Current.IsTicketsOnly) return;
        AgentContext = new AppContextFile(AppContextFile.DefaultPath);
        AgentHost = new AgentSessionHost(AgentContext, () => config.Terminal, () => Auth);
    }

    private void StartTerminal() => AgentHost?.Start();
}
