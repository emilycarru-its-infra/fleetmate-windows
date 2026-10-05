using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using Spectre.Console;

namespace FleetMate.Commands.Devices;

/// <summary>
/// The intune subcommands that match the macOS CLI by name, flags and output:
/// noncompliant, fresh-start, delete-record, offboard and laps. Destructive
/// ones refuse to run without --confirm, and --dry-run resolves the device and
/// prints the request without sending it.
/// </summary>
public static class IntuneLifecycleCommands
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ── noncompliant ────────────────────────────────────────────────────

    public static Command CreateNonCompliant(GraphService? graph)
    {
        var command = new Command("noncompliant", "List non-compliant devices");
        var limit = new Option<int>(new[] { "-l", "--limit" }, () => 50, "Maximum results");
        var json = JsonFlag();
        command.AddOption(limit);
        command.AddOption(json);
        command.SetHandler(async context =>
        {
            if (!Configured(graph, context)) return;
            var devices = await graph!.GetNonCompliantDevicesAsync(context.ParseResult.GetValueForOption(limit));
            if (devices.Count == 0)
            {
                AnsiConsole.MarkupLine("\n[green]No non-compliant devices found![/]\n");
                return;
            }
            if (context.ParseResult.GetValueForOption(json))
            {
                Console.WriteLine(JsonSerializer.Serialize(devices, Json));
                return;
            }
            AnsiConsole.MarkupLine($"\n[bold red]Non-Compliant Devices[/] ({devices.Count} found)\n");
            foreach (var d in devices)
            {
                AnsiConsole.MarkupLine($"[cyan][[{Esc(d.SerialNumber ?? "-")}]][/] [bold]{Esc(string.IsNullOrEmpty(d.DeviceName) ? "Unnamed" : d.DeviceName)}[/]");
                AnsiConsole.WriteLine($"  User: {d.UserDisplayName ?? d.UserPrincipalName ?? "-"}");
                AnsiConsole.WriteLine($"  Last Sync: {d.LastSyncDateTime?.ToString("yyyy-MM-ddTHH:mm:ssZ") ?? "-"}");
                AnsiConsole.WriteLine();
            }
        });
        return command;
    }

    // ── fresh-start ─────────────────────────────────────────────────────

    public static Command CreateFreshStart(GraphService? graph)
    {
        var command = new Command("fresh-start", "Windows: reinstall Windows, keeping enrollment (DESTRUCTIVE)");
        var id = IdentifierArgument();
        // Mac's --keep-user-data / --no-keep-user-data, default on.
        var keep = new Option<bool>("--keep-user-data", () => true, "Preserve the user's data and account");
        var noKeep = new Option<bool>("--no-keep-user-data", "Remove the user's data and account");
        var dryRun = DryRunFlag();
        var confirm = ConfirmFlag("Required to actually perform the Fresh Start");
        foreach (var o in new Option[] { keep, noKeep, dryRun, confirm }) command.AddOption(o);
        command.AddArgument(id);
        command.SetHandler(async context =>
        {
            var identifier = context.ParseResult.GetValueForArgument(id);
            var isDryRun = context.ParseResult.GetValueForOption(dryRun);
            var keepUserData = context.ParseResult.GetValueForOption(keep) && !context.ParseResult.GetValueForOption(noKeep);
            if (!Gate(context, isDryRun, confirm,
                    $"This will reinstall Windows on {identifier}. Re-run with --confirm to proceed, or --dry-run to see what would be sent."))
                return;
            if (!Configured(graph, context)) return;

            if (await ResolveTargetAsync(graph!, identifier, context) is not { } device) return;
            var platform = DevicePlatforms.From(device.OperatingSystem);
            if (platform != DevicePlatform.Windows)
            {
                Fail(context, $"{device.DeviceName ?? identifier} is {platform.DisplayName()} — Fresh Start is Windows only.");
                return;
            }
            var target = device.Id;
            if (isDryRun)
            {
                DryRunHeader(device, identifier);
                AnsiConsole.WriteLine($"  POST managedDevices/{target}/cleanWindowsDevice");
                AnsiConsole.WriteLine($"       {{\"keepUserData\":{(keepUserData ? "true" : "false")}}}");
                AnsiConsole.MarkupLine("\n[cyan]Dry run — nothing was sent.[/]");
                return;
            }
            Report(context, await graph.FreshStartDeviceAsync(target, keepUserData, confirmed: true), "fresh start");
        });
        return command;
    }

    // ── delete-record ───────────────────────────────────────────────────

    public static Command CreateDeleteRecord(GraphService? graph)
    {
        var command = new Command("delete-record", "Delete the Intune managedDevice record");
        // The Windows CLI called this "delete"; keep that working.
        command.AddAlias("delete");
        var id = IdentifierArgument();
        var dryRun = DryRunFlag();
        var confirm = ConfirmFlag("Required to actually delete the record");
        command.AddArgument(id);
        command.AddOption(dryRun);
        command.AddOption(confirm);
        command.SetHandler(async context =>
        {
            var identifier = context.ParseResult.GetValueForArgument(id);
            var isDryRun = context.ParseResult.GetValueForOption(dryRun);
            if (!Gate(context, isDryRun, confirm,
                    $"This will delete the Intune record for {identifier}. Re-run with --confirm to proceed, or --dry-run to see what would be sent."))
                return;
            if (!Configured(graph, context)) return;

            if (await ResolveTargetAsync(graph!, identifier, context) is not { } device) return;
            var target = device.Id;
            if (isDryRun)
            {
                DryRunHeader(device, identifier);
                AnsiConsole.WriteLine($"  DELETE managedDevices/{target}");
                AnsiConsole.MarkupLine("\n[cyan]Dry run — nothing was sent.[/]");
                return;
            }
            Report(context, await graph.DeleteManagedDeviceAsync(target, confirmed: true), "delete record");
        });
        return command;
    }

    // ── offboard ────────────────────────────────────────────────────────

    public static Command CreateOffboard(GraphService? graph)
    {
        var command = new Command("offboard", "Decommission a device across Intune, Autopilot and Entra (DESTRUCTIVE)");
        var id = IdentifierArgument();
        var action = new Option<string>("--action", () => "wipe", "Terminal Intune action: wipe, retire, or none")
            .FromAmong("wipe", "retire", "none");
        var keepUserData = new Option<bool>("--keep-user-data", "Windows: keep user data on the wipe");
        var protectedWipe = new Option<bool>("--protected", "Windows: protected wipe");
        var unlockCode = new Option<string?>("--unlock-code", "macOS/iOS: recovery lock PIN applied with the wipe");
        var obliteration = new Option<string?>("--obliteration", "macOS 12+: Erase All Content and Settings behaviour")
            .FromAmong(WipeOptions.ObliterationBehaviors);
        var deleteAutopilot = new Option<bool>("--delete-autopilot", "Delete the Windows Autopilot registration");
        var entra = new Option<string>("--entra", () => "none", "Entra device object: none, disable, or delete")
            .FromAmong("none", "disable", "delete");
        var deleteRecord = new Option<bool>("--delete-record", "Delete the Intune managedDevice record last");
        var dryRun = new Option<bool>("--dry-run", "Resolve every downstream record and print the plan without writing anything");
        var confirm = ConfirmFlag("Required to actually offboard");
        command.AddArgument(id);
        foreach (var o in new Option[] { action, keepUserData, protectedWipe, unlockCode, obliteration, deleteAutopilot, entra, deleteRecord, dryRun, confirm })
            command.AddOption(o);

        command.SetHandler(async context =>
        {
            var r = context.ParseResult;
            var identifier = r.GetValueForArgument(id);
            var isDryRun = r.GetValueForOption(dryRun);
            if (!Gate(context, isDryRun, confirm,
                    $"This will offboard {identifier}. Re-run with --confirm to proceed, or --dry-run to see the plan."))
                return;
            if (!Configured(graph, context)) return;

            var plan = BuildPlan(r.GetValueForOption(action)!, r.GetValueForOption(keepUserData), r.GetValueForOption(protectedWipe),
                r.GetValueForOption(unlockCode), r.GetValueForOption(obliteration), r.GetValueForOption(deleteAutopilot),
                r.GetValueForOption(entra)!, r.GetValueForOption(deleteRecord));
            var offboarder = new DeviceOffboarder(graph!);

            var resolution = await offboarder.ResolveAsync(identifier);
            if (resolution.Error != null || resolution.IsAmbiguous)
            {
                RefuseResolution(context, identifier, resolution);
                return;
            }
            if (resolution.Device is { } device)
            {
                PrintTarget(device);
                var platform = DevicePlatforms.From(device.OperatingSystem);
                if (isDryRun)
                {
                    DryRunHeader(device, identifier);
                    PrintPlan(await offboarder.PreviewAsync(device, plan));
                    PrintDroppedWipeOptions(plan.WipeOptions, platform);
                    AnsiConsole.MarkupLine("\n[cyan]Dry run — nothing was sent.[/]");
                    return;
                }
                AnsiConsole.MarkupLine($"[cyan]Offboarding {Esc(device.DeviceName ?? identifier)} ({platform.DisplayName()})[/]");
                PrintOutcome(context, await offboarder.OffboardAsync(device, plan));
                return;
            }

            // No Intune record. The Autopilot identity and the Entra object it
            // stamped outlive that record, and they are what a half-finished
            // cleanup leaves behind, so resolve those rather than refuse.
            if (!CliTargets.IsSerial(identifier))
            {
                Fail(context, $"No Intune record matches {identifier}, and only a serial number can find the directory records it left behind.");
                return;
            }
            var records = await offboarder.ResolveOrphanRecordsAsync(identifier);
            if (records.IsEmpty)
            {
                Fail(context, $"No Intune, Autopilot or Entra record matches {identifier}");
                return;
            }
            var name = records.Entra?.DisplayName ?? records.Autopilot?.SerialNumber ?? identifier;
            AnsiConsole.MarkupLine($"[yellow]{Esc(name)} has no Intune record — cleaning up the directory records it left behind[/]");
            if (isDryRun)
            {
                PrintPlan(DeviceOffboarder.PreviewOrphan(identifier, records, plan));
                AnsiConsole.MarkupLine("\n[cyan]Dry run — nothing was sent.[/]");
                return;
            }
            PrintOutcome(context, await offboarder.OffboardOrphanAsync(identifier, records, plan));
        });
        return command;
    }

    /// <summary>The plan the offboard flags describe.</summary>
    public static OffboardPlan BuildPlan(string action, bool keepUserData, bool protectedWipe, string? unlockCode,
        string? obliteration, bool deleteAutopilot, string entra, bool deleteRecord) => new()
    {
        TerminalAction = action switch { "retire" => OffboardTerminalAction.Retire, "none" => OffboardTerminalAction.None, _ => OffboardTerminalAction.Wipe },
        WipeOptions = new WipeOptions
        {
            KeepUserData = keepUserData,
            UseProtectedWipe = protectedWipe,
            MacOsUnlockCode = unlockCode,
            ObliterationBehavior = obliteration,
        },
        DeleteAutopilotRegistration = deleteAutopilot,
        EntraAction = entra switch { "disable" => OffboardEntraAction.Disable, "delete" => OffboardEntraAction.Delete, _ => OffboardEntraAction.None },
        DeleteIntuneRecord = deleteRecord,
    };

    private static void PrintPlan(IEnumerable<OffboardPlannedStep> steps)
    {
        foreach (var step in steps)
        {
            if (step.WillRun)
            {
                AnsiConsole.MarkupLine($"  [green]→ {Esc(step.Step)}[/]");
                AnsiConsole.WriteLine($"       {step.Detail}");
            }
            else
            {
                AnsiConsole.MarkupLine($"  [yellow]– {Esc(step.Step)}: {Esc(step.Detail)}[/]");
            }
        }
    }

    private static void PrintOutcome(InvocationContext context, OffboardResult result)
    {
        foreach (var step in result.Steps)
        {
            switch (step.Outcome)
            {
                case OffboardOutcome.Succeeded: AnsiConsole.MarkupLine($"  [green]✓ {Esc(step.Step)}[/]"); break;
                case OffboardOutcome.Skipped: AnsiConsole.MarkupLine($"  [yellow]– {Esc(step.Step)}: {Esc(step.Detail ?? "skipped")}[/]"); break;
                default: AnsiConsole.MarkupLine($"  [red]✗ {Esc(step.Step)}: {Esc(step.Detail ?? "unknown error")}[/]"); break;
            }
        }
        if (!result.Success) context.ExitCode = 1;
    }

    /// <summary>Options the platform will ignore, named so a dry run doesn't promise them.</summary>
    internal static List<string> DroppedWipeOptions(WipeOptions options, DevicePlatform platform)
    {
        var body = options.RequestBody(platform);
        var dropped = new List<string>();
        if (options.KeepUserData && body["keepUserData"]?.GetValue<bool>() != true)
            dropped.Add($"--keep-user-data (not supported on {platform.DisplayName()})");
        if (options.UseProtectedWipe && !body.ContainsKey("useProtectedWipe")) dropped.Add("--protected (Windows only)");
        if (!string.IsNullOrEmpty(options.MacOsUnlockCode) && !body.ContainsKey("macOsUnlockCode")) dropped.Add("--unlock-code (macOS/iOS only)");
        if (options.ObliterationBehavior != null && !body.ContainsKey("obliterationBehavior")) dropped.Add("--obliteration (macOS only)");
        return dropped;
    }

    private static void PrintDroppedWipeOptions(WipeOptions options, DevicePlatform platform)
    {
        var dropped = DroppedWipeOptions(options, platform);
        if (dropped.Count == 0) return;
        AnsiConsole.MarkupLine("\n[yellow]Ignored for this platform:[/]");
        foreach (var d in dropped) AnsiConsole.WriteLine($"  {d}");
    }

    // ── laps ────────────────────────────────────────────────────────────

    public static Command CreateLaps(GraphService? graph)
    {
        var command = new Command("laps",
            "Retrieve the Intune-managed local administrator password (macOS LAPS for a Mac, Windows LAPS for a Windows device)");
        var serial = new Argument<string>("serialNumber", "Device serial number");
        var json = JsonFlag();
        command.AddArgument(serial);
        command.AddOption(json);
        command.SetHandler(async context =>
        {
            var serialNumber = context.ParseResult.GetValueForArgument(serial);
            if (!Configured(graph, context)) return;

            if (CliTargets.SerialFilter(serialNumber) is not { } filter)
            {
                Fail(context, "A serial number is letters, digits and hyphens only.");
                return;
            }
            var devices = await graph!.GetManagedDevicesAsync(filter, 5);
            if (devices.Count == 0) { Fail(context, $"No Intune managed device has serial number {serialNumber}"); return; }
            if (devices.Count > 1)
            {
                RefuseResolution(context, serialNumber, DeviceResolution.Many(devices));
                return;
            }
            var device = devices[0];
            PrintTarget(device);
            var platform = DevicePlatforms.From(device.OperatingSystem);
            var kind = platform switch
            {
                DevicePlatform.MacOS => RecoverySecretKind.MacOSLaps,
                DevicePlatform.Windows => RecoverySecretKind.WindowsLaps,
                _ => (RecoverySecretKind?)null,
            };
            if (kind == null) { Fail(context, $"LAPS is unavailable for platform {device.OperatingSystem ?? "unknown"}"); return; }

            try
            {
                // The value goes to stdout only: never to a log or a cache.
                var secret = (await graph.RevealRecoverySecretAsync(kind.Value, device)).First();
                var rotated = LapsTimestamp(secret.Detail);
                if (context.ParseResult.GetValueForOption(json))
                {
                    Console.WriteLine(JsonSerializer.Serialize(new SortedDictionary<string, string?>
                    {
                        ["adminAccountPassword"] = secret.Value,
                        ["passwordLastRotatedDateTime"] = rotated,
                    }, Json));
                    return;
                }
                var heading = platform == DevicePlatform.MacOS ? "macOS local administrator credential" : "Windows local administrator credential";
                AnsiConsole.MarkupLine($"\n[bold]{heading}[/]\n");
                AnsiConsole.MarkupLine($"  [blue]Serial:[/]        {Esc(serialNumber)}");
                Console.WriteLine($"  Password:      {secret.Value}");
                AnsiConsole.MarkupLine($"  [blue]Last rotated:[/]  {Esc(rotated ?? "-")}");
                AnsiConsole.WriteLine();
            }
            catch (RecoverySecretException ex)
            {
                Fail(context, $"Unable to retrieve the local administrator password: {ex.Message}");
            }
        });
        return command;
    }

    /// <summary>The timestamp in a reveal's detail line ("Last rotated …" / "… backed up …").</summary>
    internal static string? LapsTimestamp(string? detail)
    {
        if (string.IsNullOrEmpty(detail)) return null;
        foreach (var marker in new[] { "Last rotated ", "backed up " })
        {
            var i = detail.IndexOf(marker, StringComparison.Ordinal);
            if (i >= 0) return detail[(i + marker.Length)..].Trim();
        }
        return null;
    }

    // ── Shared ──────────────────────────────────────────────────────────

    internal static Argument<string> IdentifierArgument() => new("identifier", "Serial number or managedDevice id");

    internal static Option<bool> JsonFlag() => new(new[] { "-j", "--json" }, "Output as JSON");

    internal static Option<bool> DryRunFlag() => new("--dry-run", "Resolve the device and print the request without sending it");

    internal static Option<bool> ConfirmFlag(string description) => new("--confirm", description);

    /// <summary>
    /// A destructive command runs only with --confirm or --dry-run. Without
    /// either it explains and exits non-zero, as the macOS CLI does.
    /// </summary>
    internal static bool Gate(InvocationContext context, bool dryRun, Option<bool> confirm, string message)
    {
        if (dryRun || context.ParseResult.GetValueForOption(confirm)) return true;
        AnsiConsole.MarkupLine($"[yellow]{Esc(message)}[/]");
        context.ExitCode = 1;
        return false;
    }

    /// <summary>
    /// The one device a destructive command may act on, printed before
    /// anything is sent. Zero, several or a refused identifier fail the
    /// command with the reason, and several list the candidates.
    /// </summary>
    internal static async Task<IntuneDevice?> ResolveTargetAsync(GraphService graph, string identifier, InvocationContext context)
    {
        var resolution = await graph.ResolveManagedDeviceAsync(identifier);
        if (resolution.Device is { } device)
        {
            PrintTarget(device);
            return device;
        }
        RefuseResolution(context, identifier, resolution);
        return null;
    }

    internal static void RefuseResolution(InvocationContext context, string identifier, DeviceResolution resolution)
    {
        if (resolution.Error != null)
        {
            Fail(context, $"Refusing {identifier}: {resolution.Error}");
            return;
        }
        if (resolution.IsAmbiguous)
        {
            Fail(context, $"{identifier} matches {resolution.Candidates.Count} Intune records. Re-run with the managedDevice id of the one you mean:");
            foreach (var c in resolution.Candidates)
                AnsiConsole.WriteLine($"  {c.Id}  {c.DeviceName}  {c.SerialNumber}  last sync {c.LastSyncDateTime?.ToString("yyyy-MM-dd") ?? "-"}");
            return;
        }
        Fail(context, $"No Intune managed device matches {identifier} exactly");
    }

    internal static void PrintTarget(IntuneDevice device) =>
        AnsiConsole.MarkupLine($"[bold]Target:[/] {Esc(device.DeviceName ?? "-")}  serial {Esc(device.SerialNumber ?? "-")}  " +
                               $"{DevicePlatforms.From(device.OperatingSystem).DisplayName()}  id {Esc(device.Id)}");

    internal static bool Configured(GraphService? graph, InvocationContext context)
    {
        if (graph != null) return true;
        Fail(context, "Microsoft Graph not configured.");
        return false;
    }

    internal static void Fail(InvocationContext context, string message)
    {
        AnsiConsole.MarkupLine($"[red]{Esc(message)}[/]");
        context.ExitCode = 1;
    }

    private static void Report(InvocationContext context, GraphService.DeviceActionResult result, string action)
    {
        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]Sent {action} to 1 device(s)[/]");
            return;
        }
        AnsiConsole.MarkupLine($"[yellow]{action}: 0 ok, 1 failed[/]");
        AnsiConsole.MarkupLine($"[red]  {Esc(result.DeviceId)}: {Esc(result.Message ?? "unknown error")}[/]");
        context.ExitCode = 1;
    }

    private static void DryRunHeader(IntuneDevice? device, string identifier)
    {
        var platform = device == null ? "unknown platform" : DevicePlatforms.From(device.OperatingSystem).DisplayName();
        AnsiConsole.MarkupLine($"\n[bold]Dry run[/] — {Esc(device?.DeviceName ?? identifier)} ({platform})");
    }

    internal static string Esc(string s) => Markup.Escape(s);
}
