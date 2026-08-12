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
        command.AddCommand(CreateSyncCommand(graphService));
        command.AddCommand(CreateRebootCommand(graphService));
        command.AddCommand(CreateLockCommand(graphService));
        command.AddCommand(CreateWipeCommand(graphService));
        command.AddCommand(CreateRetireCommand(graphService));
        command.AddCommand(CreateAutopilotResetCommand(graphService));
        command.AddCommand(CreateDeleteCommand(graphService));
        command.AddCommand(CreateAutopilotCommand(graphService));
        command.AddCommand(CreateCleanupCommand(graphService));
        command.AddCommand(CreateCimianPushCommand(graphService));

        return command;
    }

    private static Command CreateAutopilotResetCommand(GraphService? graphService)
    {
        var command = new Command("autopilot-reset",
            "AutoPilot Reset a device back to OOBE, keeping OS and enrollment (DESTRUCTIVE)");
        var idArg = new Argument<string>(name: "identifier", description: "Serial number or managedDevice id");
        var keepUserDataOption = new Option<bool>(aliases: ["--keep-user-data"], description: "Keep user data (rarely wanted on shared devices)");
        var confirmOption = new Option<bool>(aliases: ["--confirm"], description: "Required to actually reset");
        command.AddArgument(idArg);
        command.AddOption(keepUserDataOption);
        command.AddOption(confirmOption);
        command.SetHandler(async (identifier, keepUserData, confirm) =>
        {
            if (!EnsureConfigured(graphService)) return;
            if (!confirm)
            {
                AnsiConsole.MarkupLine($"[yellow]This will reset {Markup.Escape(identifier)} to OOBE, removing profiles, apps and settings. Re-run with --confirm to proceed.[/]");
                return;
            }
            var id = await ResolveDeviceIdAsync(graphService!, identifier);
            ReportAction(await graphService!.AutopilotResetDeviceAsync(id!, keepUserData: keepUserData, confirmed: true), "AutoPilot Reset");
        }, idArg, keepUserDataOption, confirmOption);
        return command;
    }

    private static Command CreateDeleteCommand(GraphService? graphService)
    {
        var command = new Command("delete", "Delete a device's Intune record (server-side only; sends nothing to the device)");
        var idArg = new Argument<string>(name: "identifier", description: "Serial number or managedDevice id");
        var confirmOption = new Option<bool>(aliases: ["--confirm"], description: "Required to actually delete");
        command.AddArgument(idArg);
        command.AddOption(confirmOption);
        command.SetHandler(async (identifier, confirm) =>
        {
            if (!EnsureConfigured(graphService)) return;
            if (!confirm)
            {
                AnsiConsole.MarkupLine($"[yellow]This will delete the Intune record for {Markup.Escape(identifier)}, leaving it unmanaged until it re-enrolls. Re-run with --confirm to proceed.[/]");
                return;
            }
            var id = await ResolveDeviceIdAsync(graphService!, identifier);
            ReportAction(await graphService!.DeleteManagedDeviceAsync(id!, confirmed: true), "delete");
        }, idArg, confirmOption);
        return command;
    }

    private static Command CreateAutopilotCommand(GraphService? graphService)
    {
        var command = new Command("autopilot", "Show the AutoPilot identity and directory records for a serial");
        var serialArg = new Argument<string>(name: "serial", description: "Device serial number");
        var jsonOption = new Option<bool>(aliases: ["--json"], description: "Output as JSON");
        command.AddArgument(serialArg);
        command.AddOption(jsonOption);
        // Context handler rather than a typed lambda: the exit code has to come
        // from the invocation context. Environment.ExitCode is discarded, because
        // Program.cs returns the pipeline's own result — so a failed lookup would
        // print an error and still exit 0, which scripts would read as success.
        command.SetHandler(async (context) =>
        {
            var serial = context.ParseResult.GetValueForArgument(serialArg);
            var json = context.ParseResult.GetValueForOption(jsonOption);

            if (!EnsureConfigured(graphService))
            {
                context.ExitCode = 1;
                return;
            }

            GraphService.DeviceRecordState? state = null;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Reading records for {serial}...", async ctx =>
                {
                    state = await graphService!.GetDeviceRecordStateAsync(serial);
                });

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(state, JsonOptions));
                if (state!.LookupFailed) context.ExitCode = 1;
                return;
            }

            if (!DisplayRecordState(state!)) context.ExitCode = 1;
        });
        return command;
    }

    private static Command CreateCleanupCommand(GraphService? graphService)
    {
        var command = new Command("cleanup",
            "Delete the stale Intune and Entra records blocking re-enrollment (keeps the AutoPilot identity)");
        var serialArg = new Argument<string>(name: "serial", description: "Device serial number");
        var confirmOption = new Option<bool>(aliases: ["--confirm"], description: "Required to actually delete");
        var jsonOption = new Option<bool>(aliases: ["--json"], description: "Output as JSON");
        command.AddArgument(serialArg);
        command.AddOption(confirmOption);
        command.AddOption(jsonOption);
        // Context handler so a refusal exits non-zero; see CreateAutopilotCommand.
        command.SetHandler(async (context) =>
        {
            var serial = context.ParseResult.GetValueForArgument(serialArg);
            var confirm = context.ParseResult.GetValueForOption(confirmOption);
            var json = context.ParseResult.GetValueForOption(jsonOption);

            if (!EnsureConfigured(graphService))
            {
                context.ExitCode = 1;
                return;
            }

            // Without --confirm this is the dry run: show exactly which records
            // would go, which is also the answer to "why did this machine fail".
            if (!confirm)
            {
                var preview = await graphService!.GetDeviceRecordStateAsync(serial);
                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(preview, JsonOptions));
                    if (preview.LookupFailed) context.ExitCode = 1;
                    return;
                }
                if (!DisplayRecordState(preview))
                {
                    context.ExitCode = 1;
                    return;
                }
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[yellow]Dry run.[/] Re-run with [cyan]--confirm[/] to delete the Intune and Entra records above. The AutoPilot identity is kept.");
                return;
            }

            GraphService.RecordCleanupResult? result = null;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Cleaning records for {serial}...", async ctx =>
                {
                    result = await graphService!.CleanDeviceRecordsAsync(serial, confirmed: true);
                });

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                if (!result!.Success) context.ExitCode = 1;
                return;
            }

            foreach (var d in result!.Deleted) AnsiConsole.MarkupLine($"[green]Deleted[/] {Markup.Escape(d)}");
            foreach (var s in result.Skipped) AnsiConsole.MarkupLine($"[dim]Skipped {Markup.Escape(s)}[/]");
            foreach (var e in result.Errors) AnsiConsole.MarkupLine($"[red]Error[/] {Markup.Escape(e)}");

            // Only claim the identity is absent when we actually looked. On a
            // failed lookup RetainedAutopilotId is null because nothing was read,
            // not because the machine lacks a hardware hash.
            if (!string.IsNullOrEmpty(result.RetainedAutopilotId))
                AnsiConsole.MarkupLine($"[dim]Kept AutoPilot identity {result.RetainedAutopilotId}[/]");
            else if (!result.LookupFailed)
                AnsiConsole.MarkupLine("[yellow]No AutoPilot identity found for this serial — the machine will not find a deployment profile at OOBE.[/]");
            else
                AnsiConsole.MarkupLine("[yellow]This is usually an elevation problem, not a device problem.[/] Check [cyan]az login[/], then retry.");

            AnsiConsole.MarkupLine(result.Success
                ? "[green]Records cleaned.[/] The device can now enroll fresh."
                : "[red]Cleanup incomplete — see errors above.[/]");

            if (!result.Success) context.ExitCode = 1;
        });
        return command;
    }

    /// <summary>
    /// Renders the three records, or refuses to render anything when the lookup
    /// never reached Graph. Returns false in that case: absent records and
    /// unreadable records look identical in this table, and showing "none" for a
    /// machine that is actually enrolled has sent people chasing a missing
    /// hardware hash that was never missing.
    /// </summary>
    private static bool DisplayRecordState(GraphService.DeviceRecordState state)
    {
        if (state.LookupFailed)
        {
            AnsiConsole.MarkupLine($"[red]Could not read the records for {Markup.Escape(state.Serial)}.[/]");
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(state.LookupError ?? "reason unavailable")}[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]This is usually an elevation problem, not a device problem.[/] Check [cyan]az login[/], then retry.");
            return false;
        }

        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Record");
        table.AddColumn("Status");
        table.AddColumn("Detail");

        var ap = state.Autopilot;
        table.AddRow(
            "AutoPilot identity",
            ap == null ? "[red]missing[/]" : "[green]present[/]",
            ap == null
                ? "[red]no hardware hash registered[/]"
                : Markup.Escape($"{ap.Id}  enrollmentState={ap.EnrollmentState}  groupTag={(string.IsNullOrEmpty(ap.GroupTag) ? "(none)" : ap.GroupTag)}"));

        table.AddRow(
            "Intune managedDevice",
            state.Intune == null ? "[yellow]none[/]" : "[green]present[/]",
            state.Intune == null
                ? "[dim]no enrollment record[/]"
                : Markup.Escape($"{state.Intune.Id}  {state.Intune.DeviceName}  {state.Intune.ComplianceState}"));

        if (state.EntraDevices.Count == 0)
        {
            table.AddRow("Entra device object", "[yellow]none[/]", "[dim]no directory object[/]");
        }
        else
        {
            foreach (var e in state.EntraDevices)
                table.AddRow(
                    "Entra device object",
                    "[green]present[/]",
                    Markup.Escape($"{e.Id}  {e.DisplayName}  trust={e.TrustType}  managed={e.IsManaged}"));
        }

        AnsiConsole.Write(table);

        if (state.IsOrphaned)
        {
            AnsiConsole.MarkupLine("[red]Orphaned:[/] Entra still holds a device object but Intune has no record.");
            AnsiConsole.MarkupLine("[dim]The next OOBE pass re-binds to the stale object by ZTDID and fails at \"Registering your device for mobile management\".[/]");
        }

        if (state.HasDanglingManagedDeviceId)
            AnsiConsole.MarkupLine($"[dim]AutoPilot identity still points at deleted managedDevice {state.Autopilot!.ManagedDeviceId} — Intune clears this on re-enrollment.[/]");

        return true;
    }

    /// <summary>Resolve a serial to a managedDevice id; pass an id straight through.</summary>
    private static async Task<string?> ResolveDeviceIdAsync(GraphService graph, string identifier)
    {
        if (Guid.TryParse(identifier, out _)) return identifier;
        var device = await graph.GetDeviceBySerialAsync(identifier);
        return string.IsNullOrEmpty(device?.Id) ? identifier : device!.Id;
    }

    private static void ReportAction(GraphService.DeviceActionResult result, string action)
    {
        if (result.Success) AnsiConsole.MarkupLine($"[green]Sent {action}[/]");
        else AnsiConsole.MarkupLine($"[red]{action} failed:[/] {Markup.Escape(result.Message ?? "unknown error")}");
    }

    private static Command CreateSyncCommand(GraphService? graphService)
    {
        var command = new Command("sync", "Force a device to sync with Intune");
        var idArg = new Argument<string>(name: "identifier", description: "Serial number or managedDevice id");
        command.AddArgument(idArg);
        command.SetHandler(async (identifier) =>
        {
            if (!EnsureConfigured(graphService)) return;
            var id = await ResolveDeviceIdAsync(graphService!, identifier);
            ReportAction(await graphService!.SyncDeviceAsync(id!), "sync");
        }, idArg);
        return command;
    }

    private static Command CreateRebootCommand(GraphService? graphService)
    {
        var command = new Command("reboot", "Reboot a device");
        var idArg = new Argument<string>(name: "identifier", description: "Serial number or managedDevice id");
        command.AddArgument(idArg);
        command.SetHandler(async (identifier) =>
        {
            if (!EnsureConfigured(graphService)) return;
            var id = await ResolveDeviceIdAsync(graphService!, identifier);
            ReportAction(await graphService!.RebootDeviceAsync(id!), "reboot");
        }, idArg);
        return command;
    }

    private static Command CreateLockCommand(GraphService? graphService)
    {
        var command = new Command("lock", "Remotely lock a device");
        var idArg = new Argument<string>(name: "identifier", description: "Serial number or managedDevice id");
        var pinOption = new Option<string?>(aliases: ["--pin"], description: "Optional PIN (macOS)");
        command.AddArgument(idArg);
        command.AddOption(pinOption);
        command.SetHandler(async (identifier, pin) =>
        {
            if (!EnsureConfigured(graphService)) return;
            var id = await ResolveDeviceIdAsync(graphService!, identifier);
            ReportAction(await graphService!.RemoteLockDeviceAsync(id!, pin, confirmed: true), "lock");
        }, idArg, pinOption);
        return command;
    }

    private static Command CreateWipeCommand(GraphService? graphService)
    {
        var command = new Command("wipe", "Factory-reset a device (DESTRUCTIVE)");
        var idArg = new Argument<string>(name: "identifier", description: "Serial number or managedDevice id");
        var keepUserDataOption = new Option<bool>(aliases: ["--keep-user-data"], description: "Keep user data");
        var confirmOption = new Option<bool>(aliases: ["--confirm"], description: "Required to actually wipe");
        command.AddArgument(idArg);
        command.AddOption(keepUserDataOption);
        command.AddOption(confirmOption);
        command.SetHandler(async (identifier, keepUserData, confirm) =>
        {
            if (!EnsureConfigured(graphService)) return;
            if (!confirm)
            {
                AnsiConsole.MarkupLine($"[yellow]This will factory-reset {Markup.Escape(identifier)}. Re-run with --confirm to proceed.[/]");
                return;
            }
            var id = await ResolveDeviceIdAsync(graphService!, identifier);
            ReportAction(await graphService!.WipeDeviceAsync(id!, keepUserData: keepUserData, confirmed: true), "wipe");
        }, idArg, keepUserDataOption, confirmOption);
        return command;
    }

    private static Command CreateRetireCommand(GraphService? graphService)
    {
        var command = new Command("retire", "Remove company data and unenroll a device");
        var idArg = new Argument<string>(name: "identifier", description: "Serial number or managedDevice id");
        var confirmOption = new Option<bool>(aliases: ["--confirm"], description: "Required to actually retire");
        command.AddArgument(idArg);
        command.AddOption(confirmOption);
        command.SetHandler(async (identifier, confirm) =>
        {
            if (!EnsureConfigured(graphService)) return;
            if (!confirm)
            {
                AnsiConsole.MarkupLine($"[yellow]This will unenroll {Markup.Escape(identifier)}. Re-run with --confirm to proceed.[/]");
                return;
            }
            var id = await ResolveDeviceIdAsync(graphService!, identifier);
            ReportAction(await graphService!.RetireDeviceAsync(id!, confirmed: true), "retire");
        }, idArg, confirmOption);
        return command;
    }

    private static Command CreateCimianPushCommand(GraphService? graphService)
    {
        var command = new Command("cimian-push", "Deploy the Cimian push-trigger remediation to a group");
        var groupArg = new Argument<string>(name: "group", description: "Target group name or id");
        command.AddArgument(groupArg);
        command.SetHandler(async (group) =>
        {
            if (!EnsureConfigured(graphService)) return;
            var result = await graphService!.DeployCimianPushRemediationAsync(group, confirmed: true);
            if (result.Success) AnsiConsole.MarkupLine($"[green]Deployed[/] Cimian push remediation to {Markup.Escape(group)}");
            else AnsiConsole.MarkupLine($"[red]Failed:[/] {Markup.Escape(result.Message ?? "unknown error")}");
        }, groupArg);
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
