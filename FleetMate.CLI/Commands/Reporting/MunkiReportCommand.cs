using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Reporting;
using FleetMate.Core.Services.Reporting;
using Spectre.Console;
using static FleetMate.Commands.Devices.AutopilotCommand;
using static FleetMate.Commands.Devices.IntuneLifecycleCommands;

namespace FleetMate.Commands.Reporting;

/// <summary>
/// Query the MunkiReport database over SSH (macOS parity): devices, device,
/// info, installs, errors, stale and query. "munkireport" alone lists devices.
/// The server comes from config (munkiReportSshHost and friends) or the
/// MUNKIREPORT_SSH_* environment variables.
/// </summary>
public static class MunkiReportCommand
{
    public static Command Create(FleetMateConfig config)
    {
        var service = new MunkiReportService(config);
        var command = new Command("munkireport", "Query MunkiReport database");
        var devices = CreateDevices(service);
        foreach (var option in devices.Options) command.AddOption(option);
        command.Handler = devices.Handler;
        command.AddCommand(devices);
        command.AddCommand(CreateDevice(service));
        command.AddCommand(CreateInfo(service));
        command.AddCommand(CreateInstalls(service));
        command.AddCommand(CreateErrors(service));
        command.AddCommand(CreateStale(service));
        command.AddCommand(CreateQuery(service));
        return command;
    }

    private static Option<bool> LongJson() => new("--json", "Output as JSON");

    private static Command CreateDevices(MunkiReportService service)
    {
        var command = new Command("devices", "List all devices in MunkiReport");
        var type = new Option<string?>(new[] { "-t", "--type" }, "Filter by machine model type");
        var limit = new Option<int>(new[] { "-l", "--limit" }, () => 50, "Maximum number of devices to show");
        var json = LongJson();
        command.AddOption(type);
        command.AddOption(limit);
        command.AddOption(json);
        command.SetHandler(context => Run(context, service, async () =>
        {
            var r = context.ParseResult;
            var list = await service.GetDevicesAsync();
            if (r.GetValueForOption(type) is { Length: > 0 } t)
                list = list.Where(d => d.MachineModel.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
            list = list.Take(r.GetValueForOption(limit)).ToList();
            if (r.GetValueForOption(json)) { Console.WriteLine(JsonSerializer.Serialize(list, Json)); return; }
            AnsiConsole.MarkupLine($"\n[bold]MunkiReport Devices[/] ({list.Count} total)\n");
            Console.WriteLine($"{Col("Serial", 20)} {Col("Computer Name", 30)} {Col("OS Version", 15)} {Col("Last Check-in", 20)}");
            foreach (var d in list)
                Console.WriteLine($"{Col(d.SerialNumber, 20)} {Col(d.DisplayName, 30)} {Col(d.OsVersion, 15)} {Col(d.LastSeenFormatted, 20)}");
            Console.WriteLine();
        }));
        return command;
    }

    private static Command CreateDevice(MunkiReportService service)
    {
        var command = new Command("device", "Get details for a specific device");
        var serial = new Argument<string>("serial", "Device serial number or hostname");
        var json = LongJson();
        command.AddArgument(serial);
        command.AddOption(json);
        command.SetHandler(context => Run(context, service, async () =>
        {
            var s = context.ParseResult.GetValueForArgument(serial);
            if (await service.GetDeviceAsync(s) is not { } d) { Fail(context, $"Device not found: {s}"); return; }
            if (context.ParseResult.GetValueForOption(json)) { Console.WriteLine(JsonSerializer.Serialize(d, Json)); return; }
            AnsiConsole.MarkupLine($"\n[bold green]Device: {Esc(d.DisplayName)}[/]\n");
            AnsiConsole.WriteLine($"  Serial Number:  {d.SerialNumber}");
            AnsiConsole.WriteLine($"  Computer Name:  {(d.MachineName.Length == 0 ? d.Hostname : d.MachineName)}");
            AnsiConsole.WriteLine($"  OS Version:    {d.OsVersion}");
            AnsiConsole.WriteLine($"  Machine Model: {d.MachineModel}");
            AnsiConsole.WriteLine($"  CPU Type:      {d.CpuType}");
            AnsiConsole.WriteLine($"  Memory:        {d.PhysicalMemory / (1024d * 1024 * 1024):0.0} GB");
            AnsiConsole.WriteLine($"  Last Check-in: {d.LastSeenFormatted}");
            if (await service.GetMunkiInfoAsync(d.SerialNumber) is { } info)
            {
                AnsiConsole.MarkupLine("\n[bold]Munki Information[/]\n");
                AnsiConsole.WriteLine($"  Munki Version:     {info.Version}");
                AnsiConsole.WriteLine($"  Manifest:          {info.Manifest}");
                AnsiConsole.WriteLine($"  Run Type:          {info.RunType}");
                AnsiConsole.WriteLine($"  Duration:          {info.Duration}");
            }
            AnsiConsole.WriteLine();
        }));
        return command;
    }

    private static Command CreateInfo(MunkiReportService service)
    {
        var command = new Command("info", "Get Munki info for a device");
        var serial = new Argument<string>("serial", "Device serial number");
        var json = LongJson();
        command.AddArgument(serial);
        command.AddOption(json);
        command.SetHandler(context => Run(context, service, async () =>
        {
            var s = context.ParseResult.GetValueForArgument(serial);
            if (await service.GetMunkiInfoAsync(s) is not { } info) { Fail(context, $"Munki info not found for: {s}"); return; }
            if (context.ParseResult.GetValueForOption(json)) { Console.WriteLine(JsonSerializer.Serialize(info, Json)); return; }
            AnsiConsole.MarkupLine($"\n[bold green]Munki Info for {Esc(s)}[/]\n");
            AnsiConsole.WriteLine($"  Version:      {info.Version}");
            AnsiConsole.WriteLine($"  Manifest:     {info.Manifest}");
            AnsiConsole.WriteLine($"  Manifest URL: {info.ManifestUrl}");
            AnsiConsole.WriteLine($"  Run Type:     {info.RunType}");
            AnsiConsole.WriteLine($"  Duration:     {info.Duration}");
            AnsiConsole.WriteLine();
        }));
        return command;
    }

    private static Command CreateInstalls(MunkiReportService service)
    {
        var command = new Command("installs", "List managed installs for a device");
        var serial = new Argument<string>("serial", "Device serial number");
        var filter = new Option<string?>(new[] { "-f", "--filter" }, "Filter by package name");
        var json = LongJson();
        command.AddArgument(serial);
        command.AddOption(filter);
        command.AddOption(json);
        command.SetHandler(context => Run(context, service, async () =>
        {
            var r = context.ParseResult;
            var s = r.GetValueForArgument(serial);
            var installs = await service.GetManagedInstallsAsync(s);
            if (r.GetValueForOption(filter) is { Length: > 0 } f)
                installs = installs.Where(i => i.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            if (r.GetValueForOption(json)) { Console.WriteLine(JsonSerializer.Serialize(installs, Json)); return; }
            AnsiConsole.MarkupLine($"\n[bold]Managed Installs for {Esc(s)}[/] ({installs.Count} packages)\n");
            Console.WriteLine($"{Col("Name", 35)} {Col("Version", 20)} {Col("Status", 10)}");
            foreach (var i in installs)
            {
                var version = i.InstalledVersion.Length > 0 ? i.InstalledVersion : i.Version;
                var status = i.Status.ToLowerInvariant() switch
                {
                    "installed" => "[green]+ installed[/]",
                    "" => "-",
                    _ => $"[red]- {Esc(i.Status)}[/]",
                };
                AnsiConsole.MarkupLine($"{Esc(Col(i.Name, 35))} {Esc(Col(version, 20))} {status}");
            }
            AnsiConsole.WriteLine();
        }));
        return command;
    }

    private static Command CreateErrors(MunkiReportService service)
    {
        var command = new Command("errors", "List install errors across all devices");
        var limit = new Option<int>(new[] { "-l", "--limit" }, () => 50, "Maximum number of errors to show");
        var json = LongJson();
        command.AddOption(limit);
        command.AddOption(json);
        command.SetHandler(context => Run(context, service, async () =>
        {
            var errors = (await service.GetErrorsAsync()).Take(context.ParseResult.GetValueForOption(limit)).ToList();
            if (context.ParseResult.GetValueForOption(json)) { Console.WriteLine(JsonSerializer.Serialize(errors, Json)); return; }
            AnsiConsole.MarkupLine($"\n[bold red]Install Errors[/] ({errors.Count} total)\n");
            Console.WriteLine($"{Col("Package", 35)} {Col("Device", 25)} {Col("Status", 20)}");
            foreach (var e in errors)
                Console.WriteLine($"{Col(e.ItemName, 35)} {Col(e.Hostname.Length > 0 ? e.Hostname : e.SerialNumber, 25)} {Col(e.Status, 20)}");
            Console.WriteLine();
        }));
        return command;
    }

    private static Command CreateStale(MunkiReportService service)
    {
        var command = new Command("stale", "List devices that haven't checked in recently");
        var days = new Option<int>(new[] { "-d", "--days" }, () => 7, "Days since last check-in (default: 7)");
        var json = LongJson();
        command.AddOption(days);
        command.AddOption(json);
        command.SetHandler(context => Run(context, service, async () =>
        {
            var d = context.ParseResult.GetValueForOption(days);
            var cutoff = DateTime.Now.AddDays(-d);
            var stale = (await service.GetDevicesAsync()).Where(x => x.Timestamp is not { } t || t < cutoff).ToList();
            if (context.ParseResult.GetValueForOption(json)) { Console.WriteLine(JsonSerializer.Serialize(stale, Json)); return; }
            AnsiConsole.MarkupLine($"\n[bold yellow]Stale Devices[/] (no check-in for {d}+ days): {stale.Count}\n");
            Console.WriteLine($"{Col("Serial", 20)} {Col("Computer Name", 30)} {Col("Last Check-in", 20)}");
            foreach (var x in stale)
                Console.WriteLine($"{Col(x.SerialNumber, 20)} {Col(x.DisplayName, 30)} {Col(x.LastSeenFormatted, 20)}");
            Console.WriteLine();
        }));
        return command;
    }

    private static Command CreateQuery(MunkiReportService service)
    {
        var command = new Command("query", "Execute a raw SQL query");
        var sql = new Argument<string>("sql", "SQL query to execute");
        command.AddArgument(sql);
        command.SetHandler(context => Run(context, service, async () =>
        {
            var q = context.ParseResult.GetValueForArgument(sql);
            AnsiConsole.MarkupLine($"\n[bold]Executing Query:[/] {Esc(q)}\n");
            var rows = await service.ExecuteSqlAsync(q);
            if (rows.Count == 0) { AnsiConsole.MarkupLine("[yellow]No results[/]\n"); return; }
            var headers = rows[0].Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            var line = string.Join(" | ", headers);
            Console.WriteLine(line);
            Console.WriteLine(new string('-', line.Length));
            foreach (var row in rows)
                Console.WriteLine(string.Join(" | ", headers.Select(h => row.TryGetValue(h, out var v) ? v : "")));
            Console.WriteLine();
        }));
        return command;
    }

    /// <summary>Configured check plus error reporting, so every subcommand fails the same way.</summary>
    private static async Task Run(InvocationContext context, MunkiReportService service, Func<Task> body)
    {
        if (!service.IsConfigured)
        {
            Fail(context, "MunkiReport SSH is not configured. Set munkiReportSshHost in config or MUNKIREPORT_SSH_HOST.");
            return;
        }
        try { await body(); }
        catch (InvalidOperationException ex) { Fail(context, ex.Message); }
    }
}
