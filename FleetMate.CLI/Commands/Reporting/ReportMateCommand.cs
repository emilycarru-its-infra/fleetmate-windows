#nullable disable warnings
using System.CommandLine;
using FleetMate.Core.Services.Reporting;
using Spectre.Console;

namespace FleetMate.Commands.Reporting;

public static class ReportMateCommand
{
    public static Command Create(ReportMateService reportMate)
    {
        var command = new Command("reportmate", "ReportMate fleet reporting commands");

        command.AddCommand(DevicesSubcommand(reportMate));
        command.AddCommand(DeviceSubcommand(reportMate));
        command.AddCommand(InstallsSubcommand(reportMate));
        command.AddCommand(ErrorsSubcommand(reportMate));
        command.AddCommand(NetworkSubcommand(reportMate));

        return command;
    }

    private static Command DevicesSubcommand(ReportMateService reportMate)
    {
        var cmd = new Command("devices", "List all fleet devices from ReportMate");
        cmd.SetHandler(async () =>
        {
            var devices = await reportMate.GetDevicesAsync();
            if (devices.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No devices found.[/]");
                return;
            }

            var table = new Table();
            table.Border = TableBorder.Rounded;
            table.Title = new TableTitle($"[cyan]Fleet Devices ({devices.Count})[/]");
            table.AddColumn("Serial");
            table.AddColumn("Hostname");
            table.AddColumn("Model");
            table.AddColumn("OS Version");
            table.AddColumn("Last Check-In");

            foreach (var d in devices.OrderBy(d => d.Hostname))
            {
                table.AddRow(
                    d.SerialNumber ?? "-",
                    d.Hostname ?? "-",
                    d.Model ?? "-",
                    d.OsVersion ?? "-",
                    d.LastCheckIn?.ToString("yyyy-MM-dd HH:mm") ?? "-");
            }

            AnsiConsole.Write(table);
        });
        return cmd;
    }

    private static Command DeviceSubcommand(ReportMateService reportMate)
    {
        var queryArg = new Argument<string>("query", "Serial number, hostname, or search term");
        var cmd = new Command("device", "Get full details for a specific device") { queryArg };
        cmd.SetHandler(async (string query) =>
        {
            var device = await reportMate.FindDeviceAsync(query);
            if (device == null)
            {
                AnsiConsole.MarkupLine("[red]Device not found.[/]");
                return;
            }

            var table = new Table();
            table.Border = TableBorder.Rounded;
            table.Title = new TableTitle($"[cyan]{device.Hostname ?? device.SerialNumber}[/]");
            table.AddColumn("Property");
            table.AddColumn("Value");

            table.AddRow("Serial", device.SerialNumber ?? "-");
            table.AddRow("Hostname", device.Hostname ?? "-");
            table.AddRow("Model", device.Model ?? "-");
            table.AddRow("OS Version", device.OsVersion ?? "-");
            table.AddRow("Last Check-In", device.LastCheckIn?.ToString("yyyy-MM-dd HH:mm") ?? "-");

            AnsiConsole.Write(table);

            // Show full device details if available
            var full = await reportMate.GetFullDeviceAsync(device.SerialNumber);
            if (full != null)
            {
                AnsiConsole.MarkupLine($"\n[bold]IP Address:[/] {full.IpAddress ?? "-"}");
                AnsiConsole.MarkupLine($"[bold]Console User:[/] {full.ConsoleUser ?? "-"}");

                if (full.Installs?.Count > 0)
                {
                    var installTable = new Table();
                    installTable.Border = TableBorder.Rounded;
                    installTable.Title = new TableTitle($"[cyan]Installs ({full.Installs.Count})[/]");
                    installTable.AddColumn("Name");
                    installTable.AddColumn("Version");
                    installTable.AddColumn("Status");
                    installTable.AddColumn("Date");

                    foreach (var i in full.Installs.Take(50))
                    {
                        installTable.AddRow(
                            i.Name ?? "-",
                            i.Version ?? "-",
                            i.Status ?? "-",
                            i.Date?.ToString("yyyy-MM-dd") ?? "-");
                    }

                    AnsiConsole.Write(installTable);
                }
            }
        }, queryArg);
        return cmd;
    }

    private static Command InstallsSubcommand(ReportMateService reportMate)
    {
        var cmd = new Command("installs", "List recent installs across the fleet");
        cmd.SetHandler(async () =>
        {
            var installs = await reportMate.GetInstallsAsync();
            if (installs.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No install records found.[/]");
                return;
            }

            var table = new Table();
            table.Border = TableBorder.Rounded;
            table.Title = new TableTitle($"[cyan]Recent Installs ({installs.Count})[/]");
            table.AddColumn("Name");
            table.AddColumn("Version");
            table.AddColumn("Status");
            table.AddColumn("Device");
            table.AddColumn("Date");

            foreach (var i in installs.Take(100))
            {
                table.AddRow(
                    i.Name ?? "-",
                    i.Version ?? "-",
                    i.Status ?? "-",
                    i.SerialNumber ?? "-",
                    i.Date?.ToString("yyyy-MM-dd") ?? "-");
            }

            AnsiConsole.Write(table);
        });
        return cmd;
    }

    private static Command ErrorsSubcommand(ReportMateService reportMate)
    {
        var cmd = new Command("errors", "Show fleet installation errors");
        var byDeviceOption = new Option<bool>("--by-device", "Group errors by device");
        var byItemOption = new Option<bool>("--by-item", "Group errors by item name");
        cmd.AddOption(byDeviceOption);
        cmd.AddOption(byItemOption);

        cmd.SetHandler(async (bool byDevice, bool byItem) =>
        {
            if (byDevice)
            {
                var errors = await reportMate.GetErrorsByDeviceAsync();
                var table = new Table();
                table.Border = TableBorder.Rounded;
                table.Title = new TableTitle($"[cyan]Errors by Device ({errors.Count})[/]");
                table.AddColumn("Serial");
                table.AddColumn("Hostname");
                table.AddColumn("Error Count");

                foreach (var e in errors.OrderByDescending(e => e.ErrorCount))
                {
                    table.AddRow(e.SerialNumber ?? "-", e.Hostname ?? "-", e.ErrorCount.ToString());
                }

                AnsiConsole.Write(table);
            }
            else if (byItem)
            {
                var errors = await reportMate.GetErrorsByItemAsync();
                var table = new Table();
                table.Border = TableBorder.Rounded;
                table.Title = new TableTitle($"[cyan]Errors by Item ({errors.Count})[/]");
                table.AddColumn("Name");
                table.AddColumn("Error Count");

                foreach (var e in errors.OrderByDescending(e => e.ErrorCount))
                {
                    table.AddRow(e.Name ?? "-", e.ErrorCount.ToString());
                }

                AnsiConsole.Write(table);
            }
            else
            {
                var errors = await reportMate.GetErrorsAsync();
                var table = new Table();
                table.Border = TableBorder.Rounded;
                table.Title = new TableTitle($"[cyan]Installation Errors ({errors.Count})[/]");
                table.AddColumn("Name");
                table.AddColumn("Version");
                table.AddColumn("Device");
                table.AddColumn("Date");

                foreach (var e in errors.Take(100))
                {
                    table.AddRow(
                        e.Name ?? "-",
                        e.Version ?? "-",
                        e.SerialNumber ?? "-",
                        e.Date?.ToString("yyyy-MM-dd") ?? "-");
                }

                AnsiConsole.Write(table);
            }
        }, byDeviceOption, byItemOption);
        return cmd;
    }

    private static Command NetworkSubcommand(ReportMateService reportMate)
    {
        var serialArg = new Argument<string?>("serial", () => null, "Serial number (omit for fleet overview)");
        var cmd = new Command("network", "Show fleet or device network information") { serialArg };

        cmd.SetHandler(async (string? serial) =>
        {
            if (serial != null)
            {
                var info = await reportMate.GetDeviceNetworkAsync(serial);
                if (info == null)
                {
                    AnsiConsole.MarkupLine($"[red]No network info for {serial}.[/]");
                    return;
                }

                var table = new Table();
                table.Border = TableBorder.Rounded;
                table.Title = new TableTitle($"[cyan]Network: {serial}[/]");
                table.AddColumn("Property");
                table.AddColumn("Value");

                table.AddRow("IP Address", info.IpAddress ?? "-");
                table.AddRow("Subnet", info.Subnet ?? "-");
                table.AddRow("SSID", info.Ssid ?? "-");
                table.AddRow("Interface", info.InterfaceName ?? "-");

                AnsiConsole.Write(table);
            }
            else
            {
                var fleet = await reportMate.GetFleetNetworkAsync();
                var table = new Table();
                table.Border = TableBorder.Rounded;
                table.Title = new TableTitle($"[cyan]Fleet Network ({fleet.Count})[/]");
                table.AddColumn("Serial");
                table.AddColumn("Hostname");
                table.AddColumn("IP Address");
                table.AddColumn("Subnet");

                foreach (var d in fleet)
                {
                    table.AddRow(d.SerialNumber ?? "-", d.Hostname ?? "-", d.IpAddress ?? "-", d.Subnet ?? "-");
                }

                AnsiConsole.Write(table);
            }
        }, serialArg);
        return cmd;
    }
}
