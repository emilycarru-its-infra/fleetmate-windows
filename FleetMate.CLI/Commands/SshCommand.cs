using System.CommandLine;
using System.Text.Json;
using FleetMate.Models.Ssh;
using FleetMate.Services;
using Spectre.Console;

namespace FleetMate.Commands;

public static class SshCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static Command Create(SshService? sshService, ReportMateService? reportMate)
    {
        var command = new Command("ssh", "Remote SSH command execution on fleet devices");

        command.AddCommand(CreateExecCommand(sshService));
        command.AddCommand(CreateBatchCommand(sshService, reportMate));
        command.AddCommand(CreateTestCommand(sshService));
        command.AddCommand(CreateLogsCommand(sshService));

        return command;
    }

    private static Command CreateExecCommand(SshService? sshService)
    {
        var command = new Command("exec", "Execute command on a single device");

        var deviceArg = new Argument<string>(
            name: "device",
            description: "Device identifier (serial, hostname, IP, or asset tag)");

        var commandArg = new Argument<string>(
            name: "command",
            description: "Command to execute on the remote device");

        var usernameOption = new Option<string?>(
            aliases: ["--username", "-u"],
            description: "Override default SSH username");

        var timeoutOption = new Option<int?>(
            aliases: ["--timeout", "-t"],
            description: "Command timeout in seconds");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output result as JSON");

        command.AddArgument(deviceArg);
        command.AddArgument(commandArg);
        command.AddOption(usernameOption);
        command.AddOption(timeoutOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (device, cmd, username, timeout, json) =>
        {
            if (!EnsureConfigured(sshService)) return;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Connecting to {device}...", async ctx =>
                {
                    var result = await sshService!.ExecuteAsync(device, cmd, username);

                    ctx.Status("Command completed");

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                        return;
                    }

                    DisplayResult(result);
                });
        }, deviceArg, commandArg, usernameOption, timeoutOption, jsonOption);

        return command;
    }

    private static Command CreateBatchCommand(SshService? sshService, ReportMateService? reportMate)
    {
        var command = new Command("batch", "Execute command on multiple devices");

        var commandArg = new Argument<string>(
            name: "command",
            description: "Command to execute on all devices");

        var devicesOption = new Option<string[]>(
            aliases: ["--devices", "-d"],
            description: "Specific devices to target (comma-separated)")
        { AllowMultipleArgumentsPerToken = true };

        var locationOption = new Option<string?>(
            aliases: ["--location", "-l"],
            description: "Filter devices by location");

        var catalogOption = new Option<string?>(
            aliases: ["--catalog", "-c"],
            description: "Filter devices by catalog");

        var usernameOption = new Option<string?>(
            aliases: ["--username", "-u"],
            description: "Override default SSH username");

        var concurrentOption = new Option<int?>(
            aliases: ["--concurrent", "-n"],
            description: "Max concurrent connections (default: 10)");

        var stopOnErrorOption = new Option<bool>(
            aliases: ["--stop-on-error"],
            description: "Stop batch on first failure");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output results as JSON");

        command.AddArgument(commandArg);
        command.AddOption(devicesOption);
        command.AddOption(locationOption);
        command.AddOption(catalogOption);
        command.AddOption(usernameOption);
        command.AddOption(concurrentOption);
        command.AddOption(stopOnErrorOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (cmd, devices, location, catalog, username, concurrent, stopOnError, json) =>
        {
            if (!EnsureConfigured(sshService)) return;

            // Get target devices
            var targets = new List<string>();

            if (devices.Length > 0)
            {
                targets.AddRange(devices);
            }
            else if (reportMate != null)
            {
                var allDevices = await reportMate.GetDevicesAsync();

                var filtered = allDevices.AsEnumerable();
                if (!string.IsNullOrEmpty(location))
                    filtered = filtered.Where(d => d.Location?.Contains(location, StringComparison.OrdinalIgnoreCase) == true);
                if (!string.IsNullOrEmpty(catalog))
                    filtered = filtered.Where(d => d.Catalog?.Equals(catalog, StringComparison.OrdinalIgnoreCase) == true);

                targets.AddRange(filtered
                    .Where(d => !string.IsNullOrEmpty(d.IpAddress))
                    .Select(d => d.IpAddress));
            }

            if (targets.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No target devices found. Use --devices or configure location/catalog filters.[/]");
                return;
            }

            AnsiConsole.MarkupLine($"[cyan]Executing on {targets.Count} device(s)...[/]");

            var result = await sshService!.ExecuteBatchAsync(
                targets, cmd, username, stopOnError);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                return;
            }

            DisplayBatchResult(result);

        }, commandArg, devicesOption, locationOption, catalogOption,
            usernameOption, concurrentOption, stopOnErrorOption, jsonOption);

        return command;
    }

    private static Command CreateTestCommand(SshService? sshService)
    {
        var command = new Command("test", "Test SSH connectivity to a device");

        var deviceArg = new Argument<string>(
            name: "device",
            description: "Device identifier (serial, hostname, IP, or asset tag)");

        var usernameOption = new Option<string?>(
            aliases: ["--username", "-u"],
            description: "Override default SSH username");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output result as JSON");

        command.AddArgument(deviceArg);
        command.AddOption(usernameOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (device, username, json) =>
        {
            if (!EnsureConfigured(sshService)) return;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Testing connection to {device}...", async ctx =>
                {
                    var result = await sshService!.TestConnectionAsync(device, username);

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                        return;
                    }

                    if (result.Success)
                    {
                        AnsiConsole.MarkupLine($"[green]Connection successful![/]");

                        var table = new Table();
                        table.Border = TableBorder.Rounded;
                        table.AddColumn("Property");
                        table.AddColumn("Value");

                        table.AddRow("Host", result.Host);
                        if (!string.IsNullOrEmpty(result.DeviceName))
                            table.AddRow("Device", result.DeviceName);
                        table.AddRow("Username", result.Username);
                        table.AddRow("Server Version", result.ServerVersion ?? "Unknown");
                        table.AddRow("Duration", $"{result.Duration.TotalMilliseconds:F0}ms");

                        AnsiConsole.Write(table);
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]Connection failed![/]");
                        AnsiConsole.MarkupLine($"[dim]Host:[/] {result.Host}");
                        AnsiConsole.MarkupLine($"[dim]Error:[/] {Markup.Escape(result.ErrorMessage ?? "Unknown error")}");
                    }
                });
        }, deviceArg, usernameOption, jsonOption);

        return command;
    }

    private static Command CreateLogsCommand(SshService? sshService)
    {
        var command = new Command("logs", "Fetch Cimian logs from a remote device");

        var deviceArg = new Argument<string>(
            name: "device",
            description: "Device identifier (serial, hostname, IP, or asset tag)");

        var tailOption = new Option<int>(
            aliases: ["--tail", "-n"],
            getDefaultValue: () => 50,
            description: "Number of lines to fetch (default: 50)");

        var errorsOption = new Option<bool>(
            aliases: ["--errors", "-e"],
            description: "Show only ERROR and WARN entries");

        var usernameOption = new Option<string?>(
            aliases: ["--username", "-u"],
            description: "Override default SSH username");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output result as JSON");

        command.AddArgument(deviceArg);
        command.AddOption(tailOption);
        command.AddOption(errorsOption);
        command.AddOption(usernameOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (device, tail, errorsOnly, username, json) =>
        {
            if (!EnsureConfigured(sshService)) return;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Fetching logs from {device}...", async ctx =>
                {
                    var result = await sshService!.GetLogsAsync(device, tail, errorsOnly, username);

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                        return;
                    }

                    if (!result.Success)
                    {
                        AnsiConsole.MarkupLine($"[red]Failed to fetch logs![/]");
                        if (!string.IsNullOrEmpty(result.ErrorMessage))
                            AnsiConsole.MarkupLine($"[dim]Error:[/] {Markup.Escape(result.ErrorMessage)}");
                        if (!string.IsNullOrEmpty(result.Stderr))
                            AnsiConsole.MarkupLine($"[dim]Stderr:[/] {Markup.Escape(result.Stderr)}");
                        return;
                    }

                    var deviceName = result.DeviceName ?? result.Host;
                    AnsiConsole.Write(new Rule($"[cyan]Logs from {Markup.Escape(deviceName)}[/]").LeftJustified());

                    // Highlight log lines
                    foreach (var line in result.Stdout.Split('\n'))
                    {
                        var trimmed = line.TrimEnd('\r');
                        if (string.IsNullOrWhiteSpace(trimmed)) continue;

                        if (trimmed.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                            AnsiConsole.MarkupLine($"[red]{Markup.Escape(trimmed)}[/]");
                        else if (trimmed.Contains("WARN", StringComparison.OrdinalIgnoreCase))
                            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(trimmed)}[/]");
                        else
                            Console.WriteLine(trimmed);
                    }
                });
        }, deviceArg, tailOption, errorsOption, usernameOption, jsonOption);

        return command;
    }

    private static bool EnsureConfigured(SshService? ssh)
    {
        if (ssh != null) return true;

        AnsiConsole.MarkupLine("[red]SSH is not configured.[/]");
        AnsiConsole.MarkupLine("Add SSH configuration to your config file (~/.fleetmate/config.yaml):");
        AnsiConsole.MarkupLine("  [cyan]ssh:[/]");
        AnsiConsole.MarkupLine("    [cyan]privateKeyPath:[/] ~/.ssh/id_rsa");
        AnsiConsole.MarkupLine("    [cyan]defaultUsername:[/] ithelp");
        return false;
    }

    private static void DisplayResult(SshResult result)
    {
        var deviceName = result.DeviceName ?? result.Host;
        var statusColor = result.Success ? "green" : "red";
        var statusText = result.Success ? "Success" : "Failed";

        AnsiConsole.Write(new Rule($"[{statusColor}]{statusText}[/] - {Markup.Escape(deviceName)}").LeftJustified());

        var table = new Table();
        table.Border = TableBorder.Simple;
        table.AddColumn("Property");
        table.AddColumn("Value");

        table.AddRow("Host", result.Host);
        if (!string.IsNullOrEmpty(result.DeviceName))
            table.AddRow("Device", result.DeviceName);
        table.AddRow("Username", result.Username);
        table.AddRow("Exit Code", result.ExitCode.ToString());
        table.AddRow("Duration", $"{result.Duration.TotalSeconds:F2}s");

        AnsiConsole.Write(table);

        if (!string.IsNullOrWhiteSpace(result.Stdout))
        {
            AnsiConsole.Write(new Rule("[dim]Output[/]").LeftJustified());
            Console.WriteLine(result.Stdout);
        }

        if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            AnsiConsole.Write(new Rule("[yellow]Stderr[/]").LeftJustified());
            Console.WriteLine(result.Stderr);
        }

        if (result.Error != null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(result.Error.Message)}");
        }
    }

    private static void DisplayBatchResult(SshBatchResult result)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Batch Execution Results[/]").LeftJustified());

        var table = new Table();
        table.Border = TableBorder.Rounded;
        table.AddColumn("Host");
        table.AddColumn("Device");
        table.AddColumn("Status");
        table.AddColumn("Exit");
        table.AddColumn("Duration");

        foreach (var r in result.Results.OrderBy(r => r.Host))
        {
            var statusColor = r.Success ? "green" : "red";
            var statusText = r.Success ? "OK" : "FAIL";

            table.AddRow(
                r.Host,
                r.DeviceName ?? "-",
                $"[{statusColor}]{statusText}[/]",
                r.ExitCode.ToString(),
                $"{r.Duration.TotalSeconds:F1}s");
        }

        AnsiConsole.Write(table);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]Total:[/] {result.TotalCount} | " +
            $"[green]Success:[/] {result.SuccessCount} | " +
            $"[red]Failed:[/] {result.FailedCount} | " +
            $"[dim]Duration:[/] {result.TotalDuration.TotalSeconds:F2}s");
    }
}
