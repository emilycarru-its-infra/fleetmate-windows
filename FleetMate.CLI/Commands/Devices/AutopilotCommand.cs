using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using Spectre.Console;
using static FleetMate.Commands.Devices.IntuneLifecycleCommands;

namespace FleetMate.Commands.Devices;

/// <summary>
/// Windows Autopilot registrations (macOS parity): list, get, delete,
/// assign-user and unassign-user. A registration is the hardware-hash record
/// that survives a wipe: a device stays claimed by this tenant until its
/// registration is deleted. "autopilot" alone lists, like Mac's default.
/// </summary>
public static class AutopilotCommand
{
    public static Command Create(GraphService? graph)
    {
        var command = new Command("autopilot", "Windows Autopilot device registrations");
        var list = CreateList(graph);
        // No default subcommand in System.CommandLine: the bare command takes
        // list's options and runs it.
        foreach (var option in list.Options) command.AddOption(option);
        command.Handler = list.Handler;
        command.AddCommand(list);
        command.AddCommand(CreateGet(graph));
        command.AddCommand(CreateDelete(graph));
        command.AddCommand(CreateAssignUser(graph));
        command.AddCommand(CreateUnassignUser(graph));
        return command;
    }

    private static Command CreateList(GraphService? graph)
    {
        var command = new Command("list", "List Autopilot device registrations");
        var filter = new Option<string?>(new[] { "-f", "--filter" }, "Filter expression (OData)");
        var limit = new Option<int>(new[] { "-l", "--limit" }, () => 50, "Maximum results");
        var json = JsonFlag();
        command.AddOption(filter);
        command.AddOption(limit);
        command.AddOption(json);
        command.SetHandler(async context =>
        {
            if (!Configured(graph, context)) return;
            var r = context.ParseResult;
            var devices = await graph!.GetAutopilotDevicesAsync(r.GetValueForOption(filter), r.GetValueForOption(limit));
            if (r.GetValueForOption(json))
            {
                Console.WriteLine(JsonSerializer.Serialize(devices, Json));
                return;
            }
            PrintTable(devices);
        });
        return command;
    }

    private static Command CreateGet(GraphService? graph)
    {
        var command = new Command("get", "Show the Autopilot registration for a serial number");
        var serial = new Argument<string>("serial", "Serial number");
        var json = JsonFlag();
        command.AddArgument(serial);
        command.AddOption(json);
        command.SetHandler(async context =>
        {
            if (!Configured(graph, context)) return;
            var s = context.ParseResult.GetValueForArgument(serial);
            if (!CliTargets.IsSerial(s))
            {
                Fail(context, "A serial number is letters, digits and hyphens only.");
                return;
            }
            if (await graph!.FindAutopilotRegistrationAsync(s) is not { } d)
            {
                AnsiConsole.MarkupLine($"[yellow]No Autopilot registration for {Esc(s)}[/]");
                context.ExitCode = 1;
                return;
            }
            if (context.ParseResult.GetValueForOption(json))
            {
                Console.WriteLine(JsonSerializer.Serialize(d, Json));
                return;
            }
            AnsiConsole.MarkupLine($"\n[bold]{Esc(d.DisplayName ?? d.SerialNumber ?? s)}[/]");
            AnsiConsole.WriteLine($"  Serial:      {d.SerialNumber ?? "-"}");
            AnsiConsole.WriteLine($"  Model:       {d.Manufacturer ?? "-"} {d.Model ?? ""}");
            AnsiConsole.WriteLine($"  Enrollment:  {d.EnrollmentState ?? "-"}");
            AnsiConsole.WriteLine($"  Profile:     {d.DeploymentProfileAssignmentStatusOrDash()}");
            AnsiConsole.WriteLine($"  Assigned to: {d.UserPrincipalName ?? "-"}");
            AnsiConsole.WriteLine($"  Entra id:    {d.AzureActiveDirectoryDeviceId ?? "-"}");
            AnsiConsole.WriteLine($"  Intune id:   {d.ManagedDeviceId ?? "-"}");
            AnsiConsole.WriteLine($"  Last seen:   {d.LastContactedDateTime?.ToString("yyyy-MM-ddTHH:mm:ssZ") ?? "-"}\n");
        });
        return command;
    }

    private static Command CreateDelete(GraphService? graph)
    {
        var command = new Command("delete", "Delete an Autopilot registration, releasing the hardware hash");
        var id = new Argument<string>("identifier", "Serial number or Autopilot device id");
        var dryRun = new Option<bool>("--dry-run", "Resolve the registration and print the request without sending it");
        var confirm = ConfirmFlag("Required to actually delete the registration");
        command.AddArgument(id);
        command.AddOption(dryRun);
        command.AddOption(confirm);
        command.SetHandler(async context =>
        {
            var identifier = context.ParseResult.GetValueForArgument(id);
            var isDryRun = context.ParseResult.GetValueForOption(dryRun);
            if (!Gate(context, isDryRun, confirm,
                    $"This will release the Autopilot registration for {identifier}. Re-run with --confirm to proceed, or --dry-run to see what would be sent."))
                return;
            if (!Configured(graph, context)) return;
            if (await ResolveIdAsync(graph!, identifier, context) is not { } autopilotId) return;

            if (isDryRun)
            {
                AnsiConsole.MarkupLine($"\n[bold]Dry run[/] — Autopilot {Esc(identifier)}");
                AnsiConsole.WriteLine($"  DELETE windowsAutopilotDeviceIdentities/{autopilotId}");
                AnsiConsole.MarkupLine("\n[cyan]Dry run — nothing was sent.[/]");
                return;
            }
            var result = await graph!.DeleteAutopilotRegistrationAsync(autopilotId, confirmed: true);
            if (!result.Success)
            {
                Fail(context, $"delete failed: {result.Message ?? "unknown error"}");
                return;
            }
            AnsiConsole.MarkupLine($"[green]Deleted Autopilot registration {Esc(autopilotId)}[/]");
        });
        return command;
    }

    private static Command CreateAssignUser(GraphService? graph)
    {
        var command = new Command("assign-user", "Assign a user to an Autopilot device for the out-of-box experience");
        var id = new Argument<string>("identifier", "Serial number or Autopilot device id");
        var upn = new Argument<string>("userPrincipalName", "User principal name to assign");
        var displayName = new Option<string?>("--display-name", "Friendly name shown during setup");
        command.AddArgument(id);
        command.AddArgument(upn);
        command.AddOption(displayName);
        command.SetHandler(async context =>
        {
            if (!Configured(graph, context)) return;
            var r = context.ParseResult;
            var identifier = r.GetValueForArgument(id);
            if (await ResolveIdAsync(graph!, identifier, context) is not { } autopilotId) return;
            var result = await graph!.AssignAutopilotRegistrationUserAsync(autopilotId, r.GetValueForArgument(upn), r.GetValueForOption(displayName));
            if (!result.Success) { Fail(context, $"assign-user failed: {result.Message ?? "unknown error"}"); return; }
            AnsiConsole.MarkupLine($"[green]Assigned {Esc(r.GetValueForArgument(upn))} to {Esc(identifier)}[/]");
        });
        return command;
    }

    private static Command CreateUnassignUser(GraphService? graph)
    {
        var command = new Command("unassign-user", "Remove the assigned user from an Autopilot device");
        var id = new Argument<string>("identifier", "Serial number or Autopilot device id");
        command.AddArgument(id);
        command.SetHandler(async context =>
        {
            if (!Configured(graph, context)) return;
            var identifier = context.ParseResult.GetValueForArgument(id);
            if (await ResolveIdAsync(graph!, identifier, context) is not { } autopilotId) return;
            var result = await graph!.UnassignAutopilotRegistrationUserAsync(autopilotId);
            if (!result.Success) { Fail(context, $"unassign-user failed: {result.Message ?? "unknown error"}"); return; }
            AnsiConsole.MarkupLine($"[green]Removed the assigned user from {Esc(identifier)}[/]");
        });
        return command;
    }

    /// <summary>Autopilot ids are GUIDs; anything else is looked up as an exact serial.</summary>
    private static async Task<string?> ResolveIdAsync(GraphService graph, string identifier, InvocationContext context)
    {
        if (CliTargets.IsGuid(identifier)) return identifier.Trim();
        if (!CliTargets.IsSerial(identifier))
        {
            Fail(context, $"Refusing {identifier}: give an Autopilot id (a GUID) or a serial number (letters, digits and hyphens only).");
            return null;
        }
        var matches = await graph.FindAutopilotRegistrationsAsync(identifier);
        if (matches.Count == 1)
        {
            AnsiConsole.MarkupLine($"[bold]Target:[/] Autopilot {Esc(matches[0].Id)}  serial {Esc(matches[0].SerialNumber ?? "-")}  {Esc(matches[0].Model ?? "")}");
            return matches[0].Id;
        }
        if (matches.Count == 0)
        {
            Fail(context, $"No Autopilot registration for {identifier}");
            return null;
        }
        Fail(context, $"{identifier} matches {matches.Count} Autopilot registrations. Re-run with the Autopilot id of the one you mean:");
        foreach (var m in matches) AnsiConsole.WriteLine($"  {m.Id}  {m.SerialNumber}  {m.Model}  {m.GroupTag}");
        return null;
    }

    private static void PrintTable(List<AutopilotDevice> devices)
    {
        AnsiConsole.MarkupLine($"\n[bold]Windows Autopilot Registrations[/] ({devices.Count} shown)\n");
        Console.WriteLine($"{Col("Serial", 18)} {Col("Model", 24)} {Col("Enrollment", 14)} {Col("Profile", 20)} {Col("User", 24)}");
        foreach (var d in devices)
            Console.WriteLine($"{Col(d.SerialNumber ?? "-", 18)} {Col(d.Model ?? "-", 24)} {Col(d.EnrollmentState ?? "-", 14)} " +
                              $"{Col(d.DeploymentProfileAssignmentStatusOrDash(), 20)} {Col(d.UserPrincipalName ?? "-", 24)}");
        Console.WriteLine();
    }

    /// <summary>Pad or cut to a fixed column width, like the macOS .col(n).</summary>
    internal static string Col(string s, int width) => s.Length > width ? s[..(width - 1)] + "…" : s.PadRight(width);
}

internal static class AutopilotDeviceDisplay
{
    /// <summary>The deployment-profile status, read from the raw record when the model carries it.</summary>
    public static string DeploymentProfileAssignmentStatusOrDash(this AutopilotDevice d) =>
        d.GetType().GetProperty("DeploymentProfileAssignmentStatus")?.GetValue(d) as string ?? "-";
}
