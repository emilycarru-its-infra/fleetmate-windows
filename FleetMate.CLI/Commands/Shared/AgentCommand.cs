using System.CommandLine;
using FleetMate.Core.Services.Agent;

namespace FleetMate.Commands.Shared;

/// <summary>`fleetmate agent`: the agent CLIs FleetMate's terminal runs.</summary>
public static class AgentCommand
{
    public static Command Create()
    {
        var command = new Command("agent", "Manage the agent CLIs FleetMate's terminal runs");
        command.AddCommand(CreateUpdate());
        return command;
    }

    private static Command CreateUpdate()
    {
        var command = new Command("update",
            "Update codex and claude to their latest versions. Finds each agent CLI, works out how it was " +
            "installed (npm global, winget, or Claude Code's native installer) and updates it with that same " +
            "tool, non-interactively. CLIs that are not installed are skipped; nothing is installed and nothing " +
            "is elevated. The FleetMate app does the same in the background every six hours.");
        var check = new Option<bool>("--check", "Report versions and install methods without updating");
        var json = new Option<bool>(aliases: ["--json"], description: "Output as JSON");
        command.AddOption(check);
        command.AddOption(json);

        command.SetHandler(async context =>
        {
            var checkOnly = context.ParseResult.GetValueForOption(check);
            var updater = new AgentCliUpdater(log: message => Console.Error.WriteLine(message));
            var state = await updater.RunAsync(checkOnly, AgentCliUpdateState.Load());
            state.Save();

            if (context.ParseResult.GetValueForOption(json))
            {
                Console.WriteLine(state.ToJson());
                return;
            }
            foreach (var status in state.Statuses)
            {
                var name = status.Cli.Command().PadRight(8);
                if (!status.Installed)
                {
                    Console.WriteLine($"{name}not installed");
                    continue;
                }
                var version = (status.Version ?? "?").PadRight(12);
                var method = (status.Method?.DisplayName() ?? "").PadRight(26);
                Console.WriteLine($"{name}{version}{method}{status.Message}");
                if (status.Path != null) Console.WriteLine($"        {status.Path}");
            }
        });
        return command;
    }
}
