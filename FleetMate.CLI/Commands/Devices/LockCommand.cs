using System.CommandLine;
using System.Text.Json;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using Spectre.Console;

namespace FleetMate.Commands.Devices;

/// <summary>
/// `fleetmate lock` / `fleetmate unlock` — a reversible lock for a Windows
/// device, which Intune does not offer (its Lock action is iOS/Android only).
///
/// The lock itself is an Intune remediation assigned to one Entra group; being
/// in the group is being locked. On the device it shows a "locked by IT"
/// sign-in notice, signs out the interactive user and stops non-admin sign-in.
/// A companion remediation assigned to every device except that group reverses
/// it once the device leaves. So these commands change exactly one thing — the
/// device's membership of the lock group — and record why on the device's
/// Intune notes.
///
/// One device per run, by design: a lock aimed at the wrong machine takes it
/// away from whoever is using it. Dry run until --confirm, like wipe.
/// </summary>
public static class LockCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static Command CreateLock(GraphService? graphService, string lockGroup) =>
        Create(graphService, lockGroup, locking: true);

    public static Command CreateUnlock(GraphService? graphService, string lockGroup) =>
        Create(graphService, lockGroup, locking: false);

    private static Command Create(GraphService? graphService, string lockGroup, bool locking)
    {
        var command = locking
            ? new Command("lock",
                $"Lock a Windows device: add it to the {lockGroup} group (sign-in notice, signs out users, blocks non-admin sign-in; nothing is wiped)")
            : new Command("unlock",
                $"Unlock a Windows device: remove it from the {lockGroup} group; the lock is reversed on the device");

        var serialArg = new Argument<string>("serial", "Device serial number");
        serialArg.AddValidator(result =>
        {
            var value = result.Tokens.FirstOrDefault()?.Value ?? string.Empty;
            if (value.StartsWith('-'))
                result.ErrorMessage = $"'{value}' is not a serial. Flags take two dashes.";
        });
        var ticketOption = new Option<string?>(aliases: ["--ticket", "-t"],
            description: locking ? "Ticket id this lock is for (required)" : "Ticket id this unlock is for");
        var reasonOption = new Option<string?>(aliases: ["--reason"],
            description: "Short reason, recorded on the device's Intune notes");
        var confirmOption = new Option<bool>(aliases: ["--confirm"],
            description: "Required to actually act; without it this is a dry run");
        var jsonOption = new Option<bool>(aliases: ["--json"], description: "Output as JSON");

        command.AddArgument(serialArg);
        command.AddOption(ticketOption);
        command.AddOption(reasonOption);
        command.AddOption(confirmOption);
        command.AddOption(jsonOption);

        command.SetHandler(async context =>
        {
            var serial = context.ParseResult.GetValueForArgument(serialArg).Trim();
            var ticket = context.ParseResult.GetValueForOption(ticketOption)?.Trim();
            var reason = context.ParseResult.GetValueForOption(reasonOption)?.Trim();
            var confirm = context.ParseResult.GetValueForOption(confirmOption);
            var json = context.ParseResult.GetValueForOption(jsonOption);

            context.ExitCode = await RunAsync(graphService, lockGroup, locking, serial, ticket, reason, confirm, json);
        });

        return command;
    }

    private static async Task<int> RunAsync(
        GraphService? graph, string lockGroup, bool locking,
        string serial, string? ticket, string? reason, bool confirm, bool json)
    {
        var verb = locking ? "lock" : "unlock";

        if (graph == null)
            return Fail(json, "not-configured", "Intune is not configured. Run fleetmate login first.");
        if (locking && string.IsNullOrWhiteSpace(ticket))
            return Fail(json, "ticket-required", "A lock needs --ticket <id>, so the device record says why it was locked.");

        GraphService.DeviceRecordState state = null!;
        await AnsiConsole.Status().Spinner(Spinner.Known.Dots)
            .StartAsync($"Reading records for {serial}...", async _ => state = await graph.GetDeviceRecordStateAsync(serial));
        if (state.LookupFailed)
            return Fail(json, "lookup-failed",
                $"Could not read the device records, so nothing was changed. {state.LookupError ?? "reason unavailable"}");

        var entra = TargetEntraDevice(state, out var refusal);
        if (entra == null)
            return Fail(json, "no-target", refusal!);
        var intune = state.Intune!;

        var before = graph.Elevation.Snapshot();
        var group = await graph.GetGroupByNameAsync(lockGroup);
        if (group == null)
            return Fail(json, "no-group", graph.Elevation.FailedSince(before)
                ? $"Could not read the {lockGroup} group: {graph.Elevation.LastError}"
                : $"The {lockGroup} group does not exist. It is created by infrastructure code, not by FleetMate.");

        before = graph.Elevation.Snapshot();
        var memberships = await graph.GetDeviceGroupMembershipsAsync(intune.AzureAdDeviceId!);
        if (graph.Elevation.FailedSince(before))
            return Fail(json, "lookup-failed",
                $"Could not read the device's group memberships, so nothing was changed. {graph.Elevation.LastError}");
        var isMember = memberships.Any(g => string.Equals(g.Id, group.Id, StringComparison.OrdinalIgnoreCase));

        var plan = new LockPlan(verb, serial, intune.DeviceName, intune.OperatingSystem, intune.UserPrincipalName,
            intune.LastSyncDateTime, lockGroup, isMember, ticket);

        if (!confirm)
        {
            if (json) Console.WriteLine(JsonSerializer.Serialize(new { DryRun = true, Plan = plan }, JsonOptions));
            else
            {
                DisplayPlan(plan);
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine(IsNoOp(plan)
                    ? $"[yellow]Nothing to do.[/] {Markup.Escape(plan.DeviceName)} is {(locking ? "already" : "not")} in {Markup.Escape(lockGroup)}."
                    : "[yellow]Dry run.[/] Re-run with [cyan]--confirm[/] to act.");
            }
            return 0;
        }

        if (IsNoOp(plan))
        {
            if (json) Console.WriteLine(JsonSerializer.Serialize(new { Changed = false, Plan = plan }, JsonOptions));
            else AnsiConsole.MarkupLine($"[yellow]Nothing to do.[/] {Markup.Escape(plan.DeviceName)} is {(locking ? "already" : "not")} in {Markup.Escape(lockGroup)}.");
            return 0;
        }

        var changed = locking
            ? await graph.AddGroupMemberAsync(group.Id, entra.Id)
            : await graph.RemoveGroupMemberAsync(group.Id, entra.Id);
        if (!changed)
            return Fail(json, "membership-failed",
                $"Could not {(locking ? "add" : "remove")} {plan.DeviceName} {(locking ? "to" : "from")} {lockGroup}. {graph.Elevation.LastError}");

        var note = NoteLine(verb, ticket, reason, Environment.UserName, DateTimeOffset.Now);
        var noted = await graph.AppendManagedDeviceNoteAsync(intune.Id, note);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Changed = true, NoteWritten = noted, Note = note, Plan = plan }, JsonOptions));
            return 0;
        }

        AnsiConsole.MarkupLine(locking
            ? $"[green]Locked.[/] {Markup.Escape(plan.DeviceName)} is now in {Markup.Escape(lockGroup)}."
            : $"[green]Unlocked.[/] {Markup.Escape(plan.DeviceName)} is out of {Markup.Escape(lockGroup)}.");
        if (!noted)
            AnsiConsole.MarkupLine("[yellow]The Intune notes could not be updated[/]; record the ticket on the device by hand.");
        AnsiConsole.MarkupLine($"[dim]{Markup.Escape(Timeline(locking))}[/]");
        return 0;
    }

    /// <summary>
    /// The one Entra device object to change: the one Intune says this machine
    /// is. A hybrid-joined machine can carry more than one directory object, and
    /// the Intune record is what the remediation assignment is evaluated
    /// against, so any other object would change nothing on the device.
    /// </summary>
    internal static EntraDevice? TargetEntraDevice(GraphService.DeviceRecordState state, out string? refusal)
    {
        refusal = null;
        if (state.Intune == null)
        {
            refusal = $"{state.Serial} has no Intune record. The lock is an Intune remediation, so it cannot reach this device.";
            return null;
        }
        if (!string.Equals(state.Intune.OperatingSystem, "Windows", StringComparison.OrdinalIgnoreCase))
        {
            refusal = $"{state.Intune.DeviceName} runs {state.Intune.OperatingSystem ?? "an unknown OS"}. This lock is for Windows only.";
            return null;
        }
        var match = state.EntraDevices.FirstOrDefault(d =>
            !string.IsNullOrEmpty(state.Intune.AzureAdDeviceId)
            && string.Equals(d.DeviceId, state.Intune.AzureAdDeviceId, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            refusal = $"{state.Intune.DeviceName} has no Entra device object matching its Intune record, so group membership cannot target it.";
        return match;
    }

    internal static bool IsNoOp(LockPlan plan) => plan.Verb == "lock" ? plan.InLockGroup : !plan.InLockGroup;

    /// <summary>The line appended to the device's Intune notes.</summary>
    internal static string NoteLine(string verb, string? ticket, string? reason, string operatorName, DateTimeOffset when)
    {
        var parts = new List<string> { $"{when:yyyy-MM-dd HH:mm zzz} fleetmate {verb} by {operatorName}" };
        if (!string.IsNullOrWhiteSpace(ticket)) parts.Add($"ticket {ticket}");
        if (!string.IsNullOrWhiteSpace(reason)) parts.Add(reason!);
        return string.Join(" - ", parts);
    }

    /// <summary>When the change reaches the device, stated plainly for the operator.</summary>
    internal static string Timeline(bool locking) => locking
        ? "The device picks this up at its next Intune remediation policy check: on restart, at a user sign-in, or within 8 hours, then locks within the hour. An offline device locks when it next comes online."
        : "The device picks this up at its next Intune remediation policy check (restart, a sign-in, or within 8 hours), then unlocks within the hour. Until then it stays locked.";

    private static void DisplayPlan(LockPlan plan)
    {
        var action = plan.Verb == "lock"
            ? $"add to {plan.Group}: sign-in notice, sign-out, non-admin sign-in blocked; nothing is wiped"
            : $"remove from {plan.Group}: the lock is reversed on the device";
        AnsiConsole.MarkupLine($"[bold]Plan:[/] {Markup.Escape(plan.Verb)} - {Markup.Escape(action)}.");
        AnsiConsole.WriteLine();

        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Serial");
        table.AddColumn("Device");
        table.AddColumn("User");
        table.AddColumn("Last sync");
        table.AddColumn($"In {Markup.Escape(plan.Group)}");
        table.AddColumn("Ticket");
        table.AddRow(
            Markup.Escape(plan.Serial),
            Markup.Escape(plan.DeviceName),
            Markup.Escape(plan.User ?? "-"),
            plan.LastSync?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-",
            plan.InLockGroup ? "[red]yes[/]" : "no",
            Markup.Escape(plan.Ticket ?? "-"));
        AnsiConsole.Write(table);
    }

    private static int Fail(bool json, string code, string message)
    {
        if (json) Console.WriteLine(JsonSerializer.Serialize(new { Error = code, Message = message }, JsonOptions));
        else AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return 1;
    }

    internal sealed record LockPlan(
        string Verb, string Serial, string DeviceName, string? OperatingSystem, string? User,
        DateTime? LastSync, string Group, bool InLockGroup, string? Ticket);
}
