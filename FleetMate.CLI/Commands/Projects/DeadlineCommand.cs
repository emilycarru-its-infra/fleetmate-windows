using System.CommandLine;
using System.Text.Json;
using System.Text.RegularExpressions;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Tickets;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Reporting;
using Spectre.Console;

namespace FleetMate.Commands.Projects;

/// <summary>
/// Commands for auditing Deadline render farm configurations
/// Checks Deadline.ini files across render nodes to ensure proper user accounts
/// </summary>
public static class DeadlineCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Standard Deadline.ini locations on Windows
    private static readonly string[] DeadlineIniPaths = new[]
    {
        @"C:\Users\*\AppData\Local\Thinkbox\Deadline10\deadline.ini",
        @"C:\ProgramData\Thinkbox\Deadline10\deadline.ini"
    };

    public static Command Create(SecureShellService? secureShellService)
    {
        var command = new Command("deadline", "Audit Deadline render farm configurations");

        command.AddCommand(CreateAuditCommand(secureShellService));
        command.AddCommand(CreateCheckUserCommand(secureShellService));

        return command;
    }

    /// <summary>
    /// Audit all render nodes for Deadline.ini configurations
    /// </summary>
    private static Command CreateAuditCommand(SecureShellService? secureShellService)
    {
        var command = new Command("audit", "Audit Deadline.ini files across render nodes");

        var devicesOption = new Option<string[]>(
            aliases: ["--devices", "-d"],
            description: "Target devices (comma-separated hostnames or IPs)")
        { AllowMultipleArgumentsPerToken = true };

        var expectedUserOption = new Option<string>(
            aliases: ["--expected-user", "-u"],
            getDefaultValue: () => "dl-worker",
            description: "Expected render user account (default: dl-worker)");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output results as JSON");

        var fixOption = new Option<bool>(
            aliases: ["--violations-only", "-v"],
            description: "Show only devices with violations (non-expected users)");

        command.AddOption(devicesOption);
        command.AddOption(expectedUserOption);
        command.AddOption(jsonOption);
        command.AddOption(fixOption);

        command.SetHandler(async (devices, expectedUser, json, violationsOnly) =>
        {
            if (!EnsureConfigured(secureShellService)) return;

            if (devices.Length == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No devices specified. Use --devices to specify render nodes.[/]");
                AnsiConsole.MarkupLine("[dim]Example: fleetmate deadline audit --devices RENDER-NODE-01,RENDER-NODE-02[/]");
                return;
            }

            await AuditDeadlineConfigs(secureShellService!, devices, expectedUser, json, violationsOnly);

        }, devicesOption, expectedUserOption, jsonOption, fixOption);

        return command;
    }

    /// <summary>
    /// Check which user account is configured for Deadline on a single device
    /// </summary>
    private static Command CreateCheckUserCommand(SecureShellService? secureShellService)
    {
        var command = new Command("check", "Check Deadline user on a single device");

        var deviceArg = new Argument<string>(
            name: "device",
            description: "Device hostname or IP address");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output result as JSON");

        command.AddArgument(deviceArg);
        command.AddOption(jsonOption);

        command.SetHandler(async (device, json) =>
        {
            if (!EnsureConfigured(secureShellService)) return;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Checking Deadline config on {device}...", async ctx =>
                {
                    var result = await CheckDeviceDeadlineConfig(secureShellService!, device);

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                        return;
                    }

                    DisplayCheckResult(result);
                });

        }, deviceArg, jsonOption);

        return command;
    }

    private static async Task AuditDeadlineConfigs(
        SecureShellService ssh,
        string[] devices,
        string expectedUser,
        bool json,
        bool violationsOnly)
    {
        var results = new List<DeadlineAuditResult>();

        await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"[cyan]Auditing {devices.Length} render nodes[/]", maxValue: devices.Length);

                // Run in parallel with throttling
                var semaphore = new SemaphoreSlim(10); // Max 10 concurrent
                var tasks = devices.Select(async device =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var result = await CheckDeviceDeadlineConfig(ssh, device);
                        result.ExpectedUser = expectedUser;
                        result.IsViolation = !string.IsNullOrEmpty(result.CurrentUser) &&
                                            !result.CurrentUser.Equals(expectedUser, StringComparison.OrdinalIgnoreCase);
                        results.Add(result);
                        task.Increment(1);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);
            });

        // Filter if violations only
        var displayResults = violationsOnly
            ? results.Where(r => r.IsViolation || r.Error != null).ToList()
            : results;

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                auditedAt = DateTime.UtcNow,
                expectedUser,
                totalDevices = results.Count,
                violations = results.Count(r => r.IsViolation),
                errors = results.Count(r => r.Error != null),
                results = displayResults
            }, JsonOptions));
            return;
        }

        DisplayAuditResults(displayResults, expectedUser, results.Count);
    }

    private static async Task<DeadlineAuditResult> CheckDeviceDeadlineConfig(SecureShellService ssh, string device)
    {
        var result = new DeadlineAuditResult
        {
            Device = device,
            CheckedAt = DateTime.UtcNow
        };

        try
        {
            // PowerShell command to find Deadline.ini files and extract user info
            var command = @"powershell -Command ""
                $iniFiles = @()
                
                # Check per-user locations
                Get-ChildItem 'C:\Users' -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                    $userPath = Join-Path $_.FullName 'AppData\Local\Thinkbox\Deadline10\deadline.ini'
                    if (Test-Path $userPath) {
                        $iniFiles += @{
                            Path = $userPath
                            User = $_.Name
                            Content = Get-Content $userPath -Raw
                        }
                    }
                }
                
                # Check system location
                $sysPath = 'C:\ProgramData\Thinkbox\Deadline10\deadline.ini'
                if (Test-Path $sysPath) {
                    $iniFiles += @{
                        Path = $sysPath
                        User = 'SYSTEM'
                        Content = Get-Content $sysPath -Raw
                    }
                }
                
                $iniFiles | ConvertTo-Json -Depth 3
            """;

            var sshResult = await ssh.ExecuteAsync(device, command);

            if (!sshResult.Success)
            {
                result.Error = sshResult.ErrorMessage ?? "SSH connection failed";
                return result;
            }

            // Parse the JSON output
            if (string.IsNullOrWhiteSpace(sshResult.Stdout))
            {
                result.Error = "No Deadline.ini files found";
                return result;
            }

            var output = sshResult.Stdout.Trim();
            
            // Handle single object (not array) case
            if (output.StartsWith("{"))
            {
                output = $"[{output}]";
            }

            var iniInfos = JsonSerializer.Deserialize<List<DeadlineIniInfo>>(output, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (iniInfos == null || iniInfos.Count == 0)
            {
                result.Error = "No Deadline.ini files found";
                return result;
            }

            result.IniFiles = iniInfos;
            result.CurrentUser = iniInfos
                .Where(i => i.User != "SYSTEM")
                .Select(i => i.User)
                .FirstOrDefault();

            result.IniFilesFound = iniInfos.Count;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    private static void DisplayCheckResult(DeadlineAuditResult result)
    {
        var statusColor = result.Error != null ? "red" :
                         result.IsViolation ? "yellow" : "green";

        AnsiConsole.Write(new Rule($"[{statusColor}]Deadline Config: {result.Device}[/]").LeftJustified());

        if (result.Error != null)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(result.Error)}");
            return;
        }

        var table = new Table();
        table.Border = TableBorder.Rounded;
        table.AddColumn("Property");
        table.AddColumn("Value");

        table.AddRow("Device", result.Device);
        table.AddRow("Current User", result.CurrentUser ?? "[dim]none[/]");
        table.AddRow("INI Files Found", result.IniFilesFound.ToString());

        AnsiConsole.Write(table);

        if (result.IniFiles?.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Deadline.ini locations:[/]");
            foreach (var ini in result.IniFiles)
            {
                AnsiConsole.MarkupLine($"  • [cyan]{ini.User}[/]: {ini.Path}");
            }
        }
    }

    private static void DisplayAuditResults(List<DeadlineAuditResult> results, string expectedUser, int totalCount)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Deadline Render Farm Audit[/]").LeftJustified());
        AnsiConsole.MarkupLine($"[dim]Expected user:[/] [cyan]{expectedUser}[/]");
        AnsiConsole.WriteLine();

        var table = new Table();
        table.Border = TableBorder.Rounded;
        table.AddColumn("Device");
        table.AddColumn("Current User");
        table.AddColumn("Status");
        table.AddColumn("INI Files");

        foreach (var r in results.OrderBy(r => r.Device))
        {
            string statusText;
            string statusColor;

            if (r.Error != null)
            {
                statusText = "ERROR";
                statusColor = "red";
            }
            else if (r.IsViolation)
            {
                statusText = "VIOLATION";
                statusColor = "yellow";
            }
            else
            {
                statusText = "OK";
                statusColor = "green";
            }

            var userDisplay = r.Error != null ? $"[dim]{Markup.Escape(r.Error)}[/]" :
                             r.CurrentUser ?? "[dim]none[/]";

            table.AddRow(
                r.Device,
                userDisplay,
                $"[{statusColor}]{statusText}[/]",
                r.IniFilesFound.ToString()
            );
        }

        AnsiConsole.Write(table);

        // Summary
        var violations = results.Count(r => r.IsViolation);
        var errors = results.Count(r => r.Error != null);
        var ok = results.Count(r => !r.IsViolation && r.Error == null);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]Total:[/] {totalCount} | " +
            $"[green]OK:[/] {ok} | " +
            $"[yellow]Violations:[/] {violations} | " +
            $"[red]Errors:[/] {errors}");

        if (violations > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[yellow]⚠ Found {violations} device(s) with non-{expectedUser} users rendering![/]");
        }
    }

    private static bool EnsureConfigured(SecureShellService? ssh)
    {
        if (ssh != null) return true;

        AnsiConsole.MarkupLine("[red]SecureShell is not configured.[/]");
        AnsiConsole.MarkupLine("Add SecureShell configuration to your config file (~/.fleetmate/config.yaml):");
        AnsiConsole.MarkupLine("  [cyan]secureShell:[/]");
        AnsiConsole.MarkupLine("    [cyan]privateKeyPath:[/] ~/.ssh/id_rsa");
        AnsiConsole.MarkupLine("    [cyan]defaultUsername:[/] ithelp");
        return false;
    }

    // Result models
    private class DeadlineAuditResult
    {
        public string Device { get; set; } = "";
        public string? CurrentUser { get; set; }
        public string? ExpectedUser { get; set; }
        public bool IsViolation { get; set; }
        public int IniFilesFound { get; set; }
        public List<DeadlineIniInfo>? IniFiles { get; set; }
        public string? Error { get; set; }
        public DateTime CheckedAt { get; set; }
    }

    private class DeadlineIniInfo
    {
        public string Path { get; set; } = "";
        public string User { get; set; } = "";
        public string? Content { get; set; }
    }
}
