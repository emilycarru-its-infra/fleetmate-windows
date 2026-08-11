using System.CommandLine;
using System.Text.Json;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Inventory;
using Spectre.Console;

namespace FleetMate.Commands.Devices;

/// <summary>
/// Fleet reset — the whole re-provisioning operation for one machine or a whole
/// lab, in one command.
///
/// This exists because doing it by hand is where the mistakes live. Re-imaging a
/// shared endpoint is a reset plus a directory cleanup, and skipping the second
/// half leaves an orphaned Entra device object that fails the machine's next
/// OOBE at "Registering your device for mobile management" — long after the
/// technician has walked away.
/// </summary>
public static class WipeCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Batch ceiling. A targeting mistake — a location typo that matches every
    /// asset, an empty filter — should stop at a refusal, not wipe a campus.
    /// </summary>
    private const int DefaultMaxTargets = 25;

    private enum ResetMode { AutopilotReset, Factory, Retire }

    public static Command Create(GraphService? graphService, SnipeService? snipeService)
    {
        var command = new Command("wipe",
            "Reset devices back to OOBE and clean up the records that block re-enrollment (DESTRUCTIVE)");

        var serialsArg = new Argument<string[]>(name: "serials",
            description: "One or more device serial numbers")
        { Arity = ArgumentArity.ZeroOrMore };

        var locationOption = new Option<string?>(aliases: ["--location", "-l"],
            description: "Target every Snipe-IT asset at this location (name or id)");
        var modelOption = new Option<string?>(aliases: ["--model"],
            description: "Target every Snipe-IT asset of this model (name or id)");
        var fileOption = new Option<string?>(aliases: ["--file"],
            description: "Read serials from a file, one per line");

        var modeOption = new Option<string>(aliases: ["--mode", "-m"],
            getDefaultValue: () => "autopilot-reset",
            description: "autopilot-reset (keeps OS + enrollment), factory (full reinstall), retire (unenroll only)");
        var keepUserDataOption = new Option<bool>(aliases: ["--keep-user-data"],
            description: "Keep user data (rarely wanted on shared devices)");

        var cleanupOption = new Option<bool>(aliases: ["--cleanup"],
            description: "After the reset, delete the stale Intune and Entra records (keeps the AutoPilot identity)");
        var recordsOnlyOption = new Option<bool>(aliases: ["--records-only"],
            description: "Skip the reset; only clean directory records for the resolved targets");

        var maxOption = new Option<int>(aliases: ["--max"],
            getDefaultValue: () => DefaultMaxTargets,
            description: $"Refuse batches larger than this (default {DefaultMaxTargets})");
        var confirmOption = new Option<bool>(aliases: ["--confirm"],
            description: "Required to actually act; without it this is a dry run");
        var jsonOption = new Option<bool>(aliases: ["--json"], description: "Output as JSON");

        command.AddArgument(serialsArg);
        foreach (var o in new Option[] { locationOption, modelOption, fileOption, modeOption,
                                         keepUserDataOption, cleanupOption, recordsOnlyOption,
                                         maxOption, confirmOption, jsonOption })
            command.AddOption(o);

        command.SetHandler(async (context) =>
        {
            var serials = context.ParseResult.GetValueForArgument(serialsArg);
            var location = context.ParseResult.GetValueForOption(locationOption);
            var model = context.ParseResult.GetValueForOption(modelOption);
            var file = context.ParseResult.GetValueForOption(fileOption);
            var modeText = context.ParseResult.GetValueForOption(modeOption)!;
            var keepUserData = context.ParseResult.GetValueForOption(keepUserDataOption);
            var cleanup = context.ParseResult.GetValueForOption(cleanupOption);
            var recordsOnly = context.ParseResult.GetValueForOption(recordsOnlyOption);
            var max = context.ParseResult.GetValueForOption(maxOption);
            var confirm = context.ParseResult.GetValueForOption(confirmOption);
            var json = context.ParseResult.GetValueForOption(jsonOption);

            if (graphService == null)
            {
                AnsiConsole.MarkupLine("[red]Intune is not configured.[/] Run [cyan]fleetmate login[/] first.");
                context.ExitCode = 1;
                return;
            }

            if (!TryParseMode(modeText, out var mode))
            {
                AnsiConsole.MarkupLine($"[red]Unknown mode '{Markup.Escape(modeText)}'.[/] Use autopilot-reset, factory or retire.");
                context.ExitCode = 1;
                return;
            }

            // AutoPilot Reset deliberately preserves enrollment — the machine
            // comes back as the same managed device. Deleting its records in the
            // same breath removes the channel it is relying on.
            if (cleanup && mode == ResetMode.AutopilotReset && !recordsOnly)
            {
                AnsiConsole.MarkupLine("[red]--cleanup cannot be combined with --mode autopilot-reset.[/]");
                AnsiConsole.MarkupLine("[dim]AutoPilot Reset keeps the device enrolled by design; deleting its records would strip the enrollment it returns to.[/]");
                AnsiConsole.MarkupLine("[dim]Use --mode factory to reinstall and re-enroll clean, or run the cleanup separately once the machine is back at OOBE.[/]");
                context.ExitCode = 1;
                return;
            }

            var targets = await ResolveTargetsAsync(serials, location, model, file, snipeService);

            if (targets.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No targets resolved.[/] Pass serials, or --location/--model/--file.");
                context.ExitCode = 1;
                return;
            }

            if (targets.Count > max)
            {
                AnsiConsole.MarkupLine($"[red]{targets.Count} targets exceeds the --max ceiling of {max}.[/]");
                AnsiConsole.MarkupLine("[dim]Narrow the targeting, or raise --max deliberately.[/]");
                context.ExitCode = 1;
                return;
            }

            // Read every target's record state up front: it is both the dry-run
            // report and the "why did this one fail" answer.
            var states = new List<GraphService.DeviceRecordState>();
            await AnsiConsole.Status().Spinner(Spinner.Known.Dots)
                .StartAsync($"Reading records for {targets.Count} device(s)...", async ctx =>
                {
                    foreach (var serial in targets)
                        states.Add(await graphService.GetDeviceRecordStateAsync(serial));
                });

            if (!confirm)
            {
                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        DryRun = true,
                        Mode = recordsOnly ? "records-only" : modeText,
                        Cleanup = cleanup,
                        Targets = states
                    }, JsonOptions));
                    return;
                }

                DisplayPlan(states, recordsOnly ? "records-only" : modeText, cleanup, recordsOnly);
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[yellow]Dry run.[/] Re-run with [cyan]--confirm[/] to act on {states.Count} device(s).");
                return;
            }

            var results = new List<object>();
            var failures = 0;

            foreach (var state in states)
            {
                var serial = state.Serial;
                var actions = new List<string>();

                if (!recordsOnly)
                {
                    if (state.Intune == null)
                    {
                        // Nothing to send the reset to. Not an error when the
                        // point is to clean up after a machine already wiped.
                        actions.Add("reset skipped: no Intune record");
                        AnsiConsole.MarkupLine($"[yellow]{serial}[/] no Intune record — nothing to reset");
                    }
                    else
                    {
                        var reset = mode switch
                        {
                            ResetMode.AutopilotReset => await graphService.AutopilotResetDeviceAsync(state.Intune.Id, keepUserData, confirmed: true),
                            ResetMode.Factory => await graphService.WipeDeviceAsync(state.Intune.Id, keepEnrollmentData: false, keepUserData: keepUserData, confirmed: true),
                            _ => await graphService.RetireDeviceAsync(state.Intune.Id, confirmed: true),
                        };

                        if (reset.Success)
                        {
                            actions.Add($"{modeText} sent");
                            AnsiConsole.MarkupLine($"[green]{serial}[/] {modeText} sent to {Markup.Escape(state.Intune.DeviceName)}");
                        }
                        else
                        {
                            failures++;
                            actions.Add($"{modeText} FAILED: {reset.Message}");
                            AnsiConsole.MarkupLine($"[red]{serial}[/] {modeText} failed: {Markup.Escape(reset.Message ?? "unknown error")}");
                        }
                    }
                }

                if (cleanup || recordsOnly)
                {
                    var cleaned = await graphService.CleanDeviceRecordsAsync(serial, confirmed: true);
                    foreach (var d in cleaned.Deleted)
                    {
                        actions.Add($"deleted {d}");
                        AnsiConsole.MarkupLine($"[green]{serial}[/] deleted {Markup.Escape(d)}");
                    }
                    foreach (var e in cleaned.Errors)
                    {
                        failures++;
                        actions.Add($"cleanup FAILED: {e}");
                        AnsiConsole.MarkupLine($"[red]{serial}[/] {Markup.Escape(e)}");
                    }
                    if (cleaned.Deleted.Count == 0 && cleaned.Errors.Count == 0)
                        AnsiConsole.MarkupLine($"[dim]{serial} no stale records to remove[/]");
                }

                results.Add(new { Serial = serial, Actions = actions });
            }

            if (json)
                Console.WriteLine(JsonSerializer.Serialize(new { Mode = modeText, Results = results }, JsonOptions));

            AnsiConsole.WriteLine();
            if (failures == 0)
                AnsiConsole.MarkupLine($"[green]Done.[/] {states.Count} device(s) processed.");
            else
            {
                AnsiConsole.MarkupLine($"[red]{failures} failure(s)[/] across {states.Count} device(s).");
                context.ExitCode = 1;
            }
        });

        return command;
    }

    private static bool TryParseMode(string text, out ResetMode mode)
    {
        switch (text.ToLowerInvariant().Replace("_", "-"))
        {
            case "autopilot-reset": case "autopilot": case "reset": mode = ResetMode.AutopilotReset; return true;
            case "factory": case "wipe": mode = ResetMode.Factory; return true;
            case "retire": case "unenroll": mode = ResetMode.Retire; return true;
            default: mode = ResetMode.AutopilotReset; return false;
        }
    }

    /// <summary>
    /// Build the target serial list from every targeting option given, as a
    /// deduplicated union. Inventory targeting resolves through Snipe-IT, which
    /// is where "the machines in that lab" is actually recorded.
    /// </summary>
    private static async Task<List<string>> ResolveTargetsAsync(
        string[] serials, string? location, string? model, string? file, SnipeService? snipe)
    {
        var targets = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? serial)
        {
            var s = serial?.Trim();
            if (!string.IsNullOrEmpty(s) && seen.Add(s)) targets.Add(s);
        }

        foreach (var s in serials) Add(s);

        if (!string.IsNullOrEmpty(file))
        {
            if (!File.Exists(file))
                AnsiConsole.MarkupLine($"[red]Serial file not found: {Markup.Escape(file)}[/]");
            else
                foreach (var line in await File.ReadAllLinesAsync(file))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length > 0 && !trimmed.StartsWith('#')) Add(trimmed);
                }
        }

        if (!string.IsNullOrEmpty(location) || !string.IsNullOrEmpty(model))
        {
            if (snipe == null)
            {
                AnsiConsole.MarkupLine("[red]Snipe-IT is not configured[/] — inventory targeting needs it. Run [cyan]fleetmate configure[/].");
                return targets;
            }

            int? locationId = null;
            if (!string.IsNullOrEmpty(location))
            {
                locationId = await ResolveLocationIdAsync(snipe, location);
                if (locationId == null)
                {
                    AnsiConsole.MarkupLine($"[red]No Snipe-IT location matches '{Markup.Escape(location)}'[/]");
                    return targets;
                }
            }

            int? modelId = null;
            if (!string.IsNullOrEmpty(model))
            {
                modelId = await ResolveModelIdAsync(snipe, model);
                if (modelId == null)
                {
                    AnsiConsole.MarkupLine($"[red]No Snipe-IT model matches '{Markup.Escape(model)}'[/]");
                    return targets;
                }
            }

            var assets = await snipe.GetAssetsAsync(locationId: locationId, modelId: modelId);
            var withSerials = assets.Where(a => !string.IsNullOrWhiteSpace(a.Serial)).ToList();

            // Assets with no serial cannot be matched to a device record; say so
            // rather than quietly shrinking the batch.
            if (withSerials.Count < assets.Count)
                AnsiConsole.MarkupLine($"[yellow]{assets.Count - withSerials.Count} asset(s) skipped — no serial recorded in Snipe-IT[/]");

            foreach (var a in withSerials) Add(a.Serial);
        }

        return targets;
    }

    private static async Task<int?> ResolveLocationIdAsync(SnipeService snipe, string location)
    {
        if (int.TryParse(location, out var id)) return id;

        var matches = await snipe.GetLocationsAsync(search: location);
        var exact = matches.FirstOrDefault(l => string.Equals(l.Name, location, StringComparison.OrdinalIgnoreCase));
        return exact?.Id ?? matches.FirstOrDefault()?.Id;
    }

    private static async Task<int?> ResolveModelIdAsync(SnipeService snipe, string model)
    {
        if (int.TryParse(model, out var id)) return id;

        var matches = await snipe.GetModelsAsync(search: model);
        var exact = matches.FirstOrDefault(m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase));
        return exact?.Id ?? matches.FirstOrDefault()?.Id;
    }

    private static void DisplayPlan(List<GraphService.DeviceRecordState> states, string mode, bool cleanup, bool recordsOnly)
    {
        AnsiConsole.MarkupLine(recordsOnly
            ? "[bold]Plan:[/] clean directory records only — no reset is sent."
            : $"[bold]Plan:[/] {Markup.Escape(mode)}{(cleanup ? ", then delete stale Intune and Entra records" : "")}. The AutoPilot identity is always kept.");
        AnsiConsole.WriteLine();

        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Serial");
        table.AddColumn("Device");
        table.AddColumn("Intune");
        table.AddColumn("Entra");
        table.AddColumn("AutoPilot");
        table.AddColumn("Note");

        foreach (var s in states)
        {
            var note = s.IsOrphaned
                ? "[red]orphaned — Entra object with no Intune record[/]"
                : s.Intune == null && s.EntraDevices.Count == 0
                    ? "[dim]no directory records[/]"
                    : "";

            table.AddRow(
                Markup.Escape(s.Serial),
                Markup.Escape(s.Intune?.DeviceName ?? s.EntraDevices.FirstOrDefault()?.DisplayName ?? "-"),
                s.Intune == null ? "[yellow]none[/]" : "[green]present[/]",
                s.EntraDevices.Count == 0 ? "[yellow]none[/]" : $"[green]{s.EntraDevices.Count}[/]",
                s.Autopilot == null ? "[red]missing[/]" : "[green]present[/]",
                note);
        }

        AnsiConsole.Write(table);

        var noAutopilot = states.Count(s => s.Autopilot == null);
        if (noAutopilot > 0)
            AnsiConsole.MarkupLine($"[yellow]{noAutopilot} device(s) have no AutoPilot identity[/] — those will not find a deployment profile at OOBE.");
    }
}
