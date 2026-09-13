using System.CommandLine;
using System.Text.Json;
using FleetMate.Core.Services;
using Spectre.Console;

namespace FleetMate.Commands.Identity;

/// <summary>
/// PIM — the <c>security</c> elevation domain, exposed on the command line.
///
/// Unlike the other five domains this runs as the signed-in operator rather than
/// a managed identity, because a role activation is a statement about a user and
/// a service principal cannot make it on their behalf. See <see cref="PimService"/>.
/// </summary>
public static class PimCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static Command Create(PimService? pim)
    {
        var command = new Command("pim", "Privileged Identity Management - activate an eligible directory role");

        command.AddCommand(CreateListCommand(pim));
        command.AddCommand(CreateActivateCommand(pim));
        command.AddCommand(CreateDeactivateCommand(pim));

        return command;
    }

    private static bool Unavailable(PimService? pim)
    {
        if (pim != null) return false;
        AnsiConsole.MarkupLine("[red]PIM is unavailable — sign in first with[/] [yellow]fleetmate login[/]");
        return true;
    }

    private static Command CreateListCommand(PimService? pim)
    {
        var command = new Command("list", "Show the directory roles you are eligible to activate, and which are active");
        var jsonOption = new Option<bool>(aliases: ["--json"], description: "Output as JSON");
        command.AddOption(jsonOption);

        command.SetHandler(async (bool json) =>
        {
            if (Unavailable(pim)) { Environment.ExitCode = 1; return; }
            try
            {
                var eligible = await pim!.GetEligibleRolesAsync();
                var active = await pim.GetActiveRolesAsync();
                var activeIds = active.Select(r => r.RoleDefinitionId).ToHashSet();

                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { eligible, active }, JsonOptions));
                    return;
                }

                if (eligible.Count == 0 && active.Count == 0)
                {
                    // Not an error. An operator who just hit a missing-role failure
                    // needs to be told plainly that they hold no eligibility at all,
                    // rather than shown an empty table they might read as a glitch.
                    AnsiConsole.MarkupLine("[yellow]You hold no PIM eligibilities and no active directory roles.[/]");
                    AnsiConsole.MarkupLine("[dim]Ask an administrator to grant eligibility for the role you need.[/]");
                    return;
                }

                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn("Role");
                table.AddColumn("State");
                table.AddColumn("Expires");

                foreach (var r in eligible.OrderBy(r => r.DisplayName))
                {
                    var isActive = activeIds.Contains(r.RoleDefinitionId);
                    var expiry = isActive
                        ? active.First(a => a.RoleDefinitionId == r.RoleDefinitionId).EndDateTime
                        : null;
                    table.AddRow(
                        Markup.Escape(r.DisplayName),
                        isActive ? "[green]active[/]" : "[dim]eligible[/]",
                        Markup.Escape(expiry ?? "-"));
                }

                // Standing assignments are not eligibilities, but an operator
                // wondering why a call still fails needs to see them too.
                foreach (var r in active.Where(a => eligible.All(e => e.RoleDefinitionId != a.RoleDefinitionId)))
                {
                    table.AddRow(Markup.Escape(r.DisplayName), "[green]active[/] [dim](standing)[/]",
                        Markup.Escape(r.EndDateTime ?? "-"));
                }

                AnsiConsole.Write(table);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                Environment.ExitCode = 1;
            }
        }, jsonOption);

        return command;
    }

    private static Command CreateActivateCommand(PimService? pim)
    {
        var command = new Command("activate", "Activate one of your eligible directory roles");

        var roleArg = new Argument<string>(name: "role", description: "Role display name, e.g. \"Cloud Device Administrator\"");
        var reasonOption = new Option<string>(aliases: ["--reason", "-r"],
            description: "Justification recorded in the tenant audit log (required)") { IsRequired = true };
        var hoursOption = new Option<int>(aliases: ["--hours"], getDefaultValue: () => 8,
            description: "Activation duration in hours");
        var jsonOption = new Option<bool>(aliases: ["--json"], description: "Output as JSON");

        command.AddArgument(roleArg);
        command.AddOption(reasonOption);
        command.AddOption(hoursOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (string role, string reason, int hours, bool json) =>
        {
            if (Unavailable(pim)) { Environment.ExitCode = 1; return; }
            try
            {
                var result = await pim!.ActivateAsync(role, reason, hours);

                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                }
                else if (result.Status.Equals("AlreadyActive", StringComparison.OrdinalIgnoreCase))
                {
                    AnsiConsole.MarkupLine($"[green]{Markup.Escape(result.RoleName)}[/] is already active" +
                        (result.EndDateTime is null ? "." : $" until {Markup.Escape(result.EndDateTime)}."));
                }
                else if (result.IsActive)
                {
                    AnsiConsole.MarkupLine($"[green]Activated[/] {Markup.Escape(result.RoleName)} for {hours}h.");
                }
                else
                {
                    // Approval-gated tenants return a pending request. Calling that
                    // success would send the operator straight back into a refusal.
                    AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(result.RoleName)}: {Markup.Escape(result.Status)}[/]");
                    AnsiConsole.MarkupLine("[dim]Not active yet — the tenant requires approval for this role.[/]");
                }

                // Exit code follows reality, so scripts gating on activation do not
                // proceed against a request that is merely pending.
                if (!result.IsActive) Environment.ExitCode = 2;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                Environment.ExitCode = 1;
            }
        }, roleArg, reasonOption, hoursOption, jsonOption);

        return command;
    }

    private static Command CreateDeactivateCommand(PimService? pim)
    {
        var command = new Command("deactivate", "Give an active role back early");

        var roleArg = new Argument<string>(name: "role", description: "Role display name");
        command.AddArgument(roleArg);

        command.SetHandler(async (string role) =>
        {
            if (Unavailable(pim)) { Environment.ExitCode = 1; return; }
            try
            {
                var name = await pim!.DeactivateAsync(role);
                AnsiConsole.MarkupLine($"[green]Deactivated[/] {Markup.Escape(name)}.");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                Environment.ExitCode = 1;
            }
        }, roleArg);

        return command;
    }
}
