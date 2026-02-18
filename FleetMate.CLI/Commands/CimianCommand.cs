using System.CommandLine;
using System.Text.Json;
using FleetMate.Services;
using Spectre.Console;

namespace FleetMate.Commands;

/// <summary>
/// Cimian deployment system commands - push triggers, status, and management
/// </summary>
public static class CimianCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static Command Create(GraphService? graphService, SecureShellService? secureShellService, CimianService? cimianService)
    {
        var command = new Command("cimian", "Cimian deployment system - push triggers, device management");

        command.AddCommand(CreatePushCommand(graphService, secureShellService, cimianService));

        return command;
    }

    private static Command CreatePushCommand(GraphService? graphService, SecureShellService? secureShellService, CimianService? cimianService)
    {
        var command = new Command("push", "Trigger an immediate Cimian run on target devices");

        var serialOption = new Option<string[]?>(
            aliases: ["--serial", "-s"],
            description: "Target device serial numbers (comma-separated or multiple flags)")
        {
            AllowMultipleArgumentsPerToken = true
        };

        var groupOption = new Option<string?>(
            aliases: ["--group", "-g"],
            description: "Target Intune/Entra group name or ID");

        var sshOption = new Option<bool>(
            aliases: ["--ssh"],
            description: "Use SSH channel (direct, near-instant). Default is Intune.");

        var noSyncOption = new Option<bool>(
            aliases: ["--no-sync"],
            description: "Skip forcing Intune sync after deploying remediation (Intune channel only)");

        var dryRunOption = new Option<bool>(
            aliases: ["--dry-run"],
            description: "Show what would happen without executing");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddOption(serialOption);
        command.AddOption(groupOption);
        command.AddOption(sshOption);
        command.AddOption(noSyncOption);
        command.AddOption(dryRunOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (serials, group, useSsh, noSync, dryRun, json) =>
        {
            // Validate inputs
            var hasSerials = serials != null && serials.Length > 0;
            var hasGroup = !string.IsNullOrEmpty(group);

            if (!hasSerials && !hasGroup)
            {
                AnsiConsole.MarkupLine("[red]Specify --serial or --group to target devices[/]");
                AnsiConsole.MarkupLine("  [cyan]fleetmate cimian push --serial ABC123[/]");
                AnsiConsole.MarkupLine("  [cyan]fleetmate cimian push --group \"Design Lab\"[/]");
                AnsiConsole.MarkupLine("  [cyan]fleetmate cimian push --serial ABC123 --ssh[/]");
                return;
            }

            // Expand comma-separated serials
            var serialList = new List<string>();
            if (hasSerials)
            {
                foreach (var s in serials!)
                {
                    serialList.AddRange(s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }

            var channel = useSsh ? "SSH" : "Intune";

            // Dry run
            if (dryRun)
            {
                DisplayDryRun(channel, serialList, group);
                return;
            }

            // SSH channel
            if (useSsh)
            {
                if (cimianService == null || secureShellService == null)
                {
                    AnsiConsole.MarkupLine("[red]SSH is not configured.[/]");
                    AnsiConsole.MarkupLine("Add SecureShell configuration to your config file (~/.fleetmate/config.yaml)");
                    return;
                }

                if (!hasSerials)
                {
                    AnsiConsole.MarkupLine("[red]SSH channel requires --serial (cannot resolve group members via SSH)[/]");
                    AnsiConsole.MarkupLine("Use [cyan]--group[/] without [cyan]--ssh[/] to push via Intune.");
                    return;
                }

                await ExecuteSshPush(cimianService, serialList, json);
                return;
            }

            // Intune channel (default)
            if (graphService == null)
            {
                AnsiConsole.MarkupLine("[red]Intune (Graph) is not configured.[/]");
                AnsiConsole.MarkupLine("Add Graph configuration and run: [cyan]az login[/]");
                return;
            }

            if (cimianService == null)
            {
                AnsiConsole.MarkupLine("[red]Cimian service not available[/]");
                return;
            }

            if (hasGroup)
            {
                await ExecuteIntunePushGroup(cimianService, graphService, group!, !noSync, json);
            }
            else if (hasSerials)
            {
                await ExecuteIntunePushSerials(cimianService, graphService, serialList, !noSync, json);
            }

        }, serialOption, groupOption, sshOption, noSyncOption, dryRunOption, jsonOption);

        return command;
    }

    private static void DisplayDryRun(string channel, List<string> serials, string? group)
    {
        var panel = new Panel(new Markup("[yellow]DRY RUN - No changes will be made[/]"));
        panel.Header = new PanelHeader("Cimian Push");
        panel.Border = BoxBorder.Rounded;
        AnsiConsole.Write(panel);

        var table = new Table();
        table.Border = TableBorder.Rounded;
        table.AddColumn("Setting");
        table.AddColumn("Value");

        table.AddRow("Channel", channel);

        if (serials.Count > 0)
        {
            table.AddRow("Serials", string.Join(", ", serials));
        }

        if (!string.IsNullOrEmpty(group))
        {
            table.AddRow("Group", group);
        }

        if (channel == "Intune")
        {
            table.AddRow("Method", "Deploy proactive remediation + force Intune sync");
            table.AddRow("Trigger file", @"C:\ProgramData\ManagedInstalls\.cimian.headless");
            table.AddRow("Expected latency", "Minutes (after Intune sync)");
        }
        else
        {
            table.AddRow("Method", "SSH: Create trigger file directly on device");
            table.AddRow("Trigger file", @"C:\ProgramData\ManagedInstalls\.cimian.headless");
            table.AddRow("Expected latency", "<10 seconds (CimianWatcher polling)");
        }

        AnsiConsole.Write(table);
    }

    private static async Task ExecuteSshPush(CimianService cimianService, List<string> serials, bool json)
    {
        CimianService.CimianPushBatchResult? result = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Pushing Cimian run via SSH to {serials.Count} device(s)...", async ctx =>
            {
                result = await cimianService.PushViaSshAsync(serials, "FleetMate CLI (SSH)");
            });

        if (result == null) return;

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return;
        }

        DisplayPushResults(result);
    }

    private static async Task ExecuteIntunePushGroup(
        CimianService cimianService, GraphService graphService, string group, bool forceSync, bool json)
    {
        CimianService.CimianPushBatchResult? result = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Deploying Cimian push remediation to group '{group}'...", async ctx =>
            {
                result = await cimianService.PushViaIntuneAsync(graphService, group, forceSync);
            });

        if (result == null) return;

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return;
        }

        DisplayPushResults(result);
    }

    private static async Task ExecuteIntunePushSerials(
        CimianService cimianService, GraphService graphService, List<string> serials, bool forceSync, bool json)
    {
        CimianService.CimianPushBatchResult? result = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Pushing Cimian run via Intune to {serials.Count} device(s)...", async ctx =>
            {
                result = await cimianService.PushViaIntuneBySerialAsync(graphService, serials, forceSync);
            });

        if (result == null) return;

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return;
        }

        DisplayPushResults(result);
    }

    private static void DisplayPushResults(CimianService.CimianPushBatchResult batchResult)
    {
        // Summary
        var summaryColor = batchResult.FailedCount == 0 ? "green" : "yellow";
        AnsiConsole.MarkupLine($"\n[{summaryColor}]Push complete: {batchResult.SuccessCount}/{batchResult.TotalCount} succeeded via {batchResult.Channel} ({batchResult.Duration.TotalSeconds:F1}s)[/{summaryColor}]");

        if (batchResult.Results.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No devices targeted[/]");
            return;
        }

        // Results table
        var table = new Table();
        table.Border = TableBorder.Rounded;
        table.AddColumn("Device");
        table.AddColumn("Name");
        table.AddColumn("Channel");
        table.AddColumn("Status");
        table.AddColumn("Message");

        foreach (var r in batchResult.Results)
        {
            var status = r.Success ? "[green]OK[/]" : "[red]FAIL[/]";
            table.AddRow(
                r.DeviceIdentifier,
                r.DeviceName ?? "-",
                r.Channel,
                status,
                Markup.Escape(r.Message ?? "-"));
        }

        AnsiConsole.Write(table);

        if (batchResult.Channel == "Intune")
        {
            AnsiConsole.MarkupLine("\n[dim]Devices will create .cimian.headless on next Intune check-in.[/]");
            AnsiConsole.MarkupLine("[dim]CimianWatcher polls every 10s and will launch managedsoftwareupdate.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("\n[dim]CimianWatcher will detect the trigger file within 10 seconds.[/]");
        }
    }
}
