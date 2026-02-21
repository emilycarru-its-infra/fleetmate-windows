using System.CommandLine;
using System.Text.Json;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;
using FleetMate.Core.Config;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Tickets;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Reporting;
using Spectre.Console;

namespace FleetMate.Commands.Devices;

/// <summary>
/// Intune device management commands
/// </summary>
public static class IntuneCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static Command Create(GraphService? graphService, ReportMateService? reportMate)
    {
        var command = new Command("intune", "Intune device management - managed devices, compliance");

        command.AddCommand(CreateDevicesCommand(graphService));
        command.AddCommand(CreateDeviceCommand(graphService));
        command.AddCommand(CreateComplianceCommand(graphService));

        return command;
    }

    private static Command CreateDevicesCommand(GraphService? graphService)
    {
        var command = new Command("devices", "List Intune managed devices");

        var filterOption = new Option<string?>(
            aliases: ["--filter", "-f"],
            description: "OData filter expression (e.g., \"complianceState eq 'noncompliant'\")");

        var searchOption = new Option<string?>(
            aliases: ["--search", "-s"],
            description: "Search by device name prefix");

        var nonCompliantOption = new Option<bool>(
            aliases: ["--noncompliant"],
            description: "Show only non-compliant devices");

        var limitOption = new Option<int>(
            aliases: ["--limit", "-n"],
            getDefaultValue: () => 50,
            description: "Maximum results (default: 50)");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddOption(filterOption);
        command.AddOption(searchOption);
        command.AddOption(nonCompliantOption);
        command.AddOption(limitOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (filter, search, nonCompliant, limit, json) =>
        {
            if (!EnsureConfigured(graphService)) return;

            List<IntuneDevice> devices = new();

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Fetching Intune devices...", async ctx =>
                {
                    if (nonCompliant)
                    {
                        devices = await graphService!.GetNonCompliantDevicesAsync(limit);
                    }
                    else if (!string.IsNullOrEmpty(search))
                    {
                        devices = await graphService!.SearchDevicesAsync(search, limit);
                    }
                    else
                    {
                        devices = await graphService!.GetManagedDevicesAsync(filter, limit);
                    }
                });

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(devices, JsonOptions));
                return;
            }

            if (devices.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No devices found[/]");
                return;
            }

            DisplayDevices(devices);
        }, filterOption, searchOption, nonCompliantOption, limitOption, jsonOption);

        return command;
    }

    private static Command CreateDeviceCommand(GraphService? graphService)
    {
        var command = new Command("device", "Get device details by serial number or name");

        var queryArg = new Argument<string>(
            name: "query",
            description: "Serial number or device name");

        var byNameOption = new Option<bool>(
            aliases: ["--by-name"],
            description: "Search by device name instead of serial");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddArgument(queryArg);
        command.AddOption(byNameOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (query, byName, json) =>
        {
            if (!EnsureConfigured(graphService)) return;

            IntuneDevice? device = null;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Finding device {query}...", async ctx =>
                {
                    device = byName
                        ? await graphService!.GetDeviceByNameAsync(query)
                        : await graphService!.GetDeviceBySerialAsync(query);
                });

            if (device == null)
            {
                AnsiConsole.MarkupLine($"[yellow]Device not found: {query}[/]");
                return;
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(device, JsonOptions));
                return;
            }

            DisplayDeviceDetail(device);
        }, queryArg, byNameOption, jsonOption);

        return command;
    }

    private static Command CreateComplianceCommand(GraphService? graphService)
    {
        var command = new Command("compliance", "Check device compliance status");

        var queryArg = new Argument<string>(
            name: "query",
            description: "Serial number or device name");

        var byNameOption = new Option<bool>(
            aliases: ["--by-name"],
            description: "Search by device name instead of serial");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddArgument(queryArg);
        command.AddOption(byNameOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (query, byName, json) =>
        {
            if (!EnsureConfigured(graphService)) return;

            IntuneDevice? device = null;
            List<DeviceCompliancePolicyState> policies = new();

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Checking compliance for {query}...", async ctx =>
                {
                    device = byName
                        ? await graphService!.GetDeviceByNameAsync(query)
                        : await graphService!.GetDeviceBySerialAsync(query);

                    if (device != null)
                    {
                        policies = await graphService!.GetDeviceComplianceAsync(device.Id);
                    }
                });

            if (device == null)
            {
                AnsiConsole.MarkupLine($"[yellow]Device not found: {query}[/]");
                return;
            }

            if (json)
            {
                var result = new { Device = device, CompliancePolicies = policies };
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                return;
            }

            DisplayComplianceStatus(device, policies);
        }, queryArg, byNameOption, jsonOption);

        return command;
    }

    private static bool EnsureConfigured(GraphService? graph)
    {
        if (graph != null) return true;

        AnsiConsole.MarkupLine("[red]Intune is not configured.[/]");
        AnsiConsole.MarkupLine("Add Graph configuration to your config file (~/.fleetmate/config.yaml):");
        AnsiConsole.MarkupLine("  [cyan]graph:[/]");
        AnsiConsole.MarkupLine("    [cyan]useAzureCliAuth:[/] true");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("Then log in with: [cyan]az login[/]");
        return false;
    }

    private static void DisplayDevices(List<IntuneDevice> devices)
    {
        var table = new Table();
        table.Border = TableBorder.Rounded;
        table.AddColumn("Device Name");
        table.AddColumn("Serial");
        table.AddColumn("OS");
        table.AddColumn("Compliance");
        table.AddColumn("User");
        table.AddColumn("Last Sync");

        foreach (var device in devices)
        {
            var complianceColor = device.ComplianceState?.ToLowerInvariant() switch
            {
                "compliant" => "green",
                "noncompliant" => "red",
                "ingraceperiod" => "yellow",
                _ => "dim"
            };

            var lastSync = device.LastSyncDateTime?.ToString("MM/dd HH:mm") ?? "-";

            table.AddRow(
                Markup.Escape(device.DeviceName),
                device.SerialNumber ?? "-",
                $"{device.OperatingSystem} {device.OsVersion}".Trim(),
                $"[{complianceColor}]{device.ComplianceState ?? "unknown"}[/]",
                Markup.Escape(device.UserDisplayName ?? device.UserPrincipalName ?? "-"),
                lastSync);
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]Showing {devices.Count} devices[/]");
    }

    private static void DisplayDeviceDetail(IntuneDevice device)
    {
        var complianceColor = device.IsCompliant ? "green" : "red";

        var panel = new Panel(
            new Rows(
                new Markup($"[bold]{Markup.Escape(device.DeviceName)}[/]"),
                new Text(""),
                new Markup($"[dim]Serial:[/] {device.SerialNumber ?? "-"}"),
                new Markup($"[dim]Model:[/] {device.Manufacturer} {device.Model}"),
                new Markup($"[dim]OS:[/] {device.OperatingSystem} {device.OsVersion}"),
                new Markup($"[dim]Compliance:[/] [{complianceColor}]{device.ComplianceState}[/]"),
                new Markup($"[dim]Management:[/] {device.ManagementState}"),
                new Text(""),
                new Markup($"[dim]User:[/] {Markup.Escape(device.UserDisplayName ?? "-")}"),
                new Markup($"[dim]Email:[/] {device.UserPrincipalName ?? "-"}"),
                new Text(""),
                new Markup($"[dim]Enrolled:[/] {device.EnrolledDateTime?.ToString("g") ?? "-"}"),
                new Markup($"[dim]Last Sync:[/] {device.LastSyncDateTime?.ToString("g") ?? "-"}"),
                new Markup($"[dim]Encrypted:[/] {(device.IsEncrypted == true ? "[green]Yes[/]" : "[red]No[/]")}"),
                new Markup($"[dim]Storage:[/] {device.StorageUsedPercent?.ToString("F1") ?? "-"}% used")
            ))
        {
            Header = new PanelHeader(" Intune Device "),
            Border = BoxBorder.Rounded
        };

        AnsiConsole.Write(panel);
    }

    private static void DisplayComplianceStatus(IntuneDevice device, List<DeviceCompliancePolicyState> policies)
    {
        var overallColor = device.IsCompliant ? "green" : "red";

        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(device.DeviceName)}[/] - [{overallColor}]{device.ComplianceState}[/]");
        AnsiConsole.MarkupLine($"[dim]Serial: {device.SerialNumber}[/]");
        AnsiConsole.WriteLine();

        if (policies.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No compliance policies assigned[/]");
            return;
        }

        var table = new Table();
        table.Border = TableBorder.Rounded;
        table.AddColumn("Policy");
        table.AddColumn("State");
        table.AddColumn("Settings");

        foreach (var policy in policies)
        {
            var stateColor = policy.State?.ToLowerInvariant() switch
            {
                "compliant" => "green",
                "noncompliant" => "red",
                "notapplicable" => "dim",
                _ => "yellow"
            };

            table.AddRow(
                Markup.Escape(policy.DisplayName ?? "Unknown Policy"),
                $"[{stateColor}]{policy.State ?? "unknown"}[/]",
                policy.SettingCount?.ToString() ?? "-");
        }

        AnsiConsole.Write(table);
    }
}
