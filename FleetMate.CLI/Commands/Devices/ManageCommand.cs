using System.CommandLine;
using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Manage;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Manage;
using FleetMate.Core.Services.Reporting;
using Spectre.Console;

namespace FleetMate.Commands.Devices;

/// <summary>
/// Lab operations from the terminal, the same engine as the Manage tab:
/// list rooms from the roster, scan a room, run a command across it, and
/// audit a command library.
/// </summary>
public static class ManageCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static Command Create(FleetMateConfig config, ReportMateService? reportMate)
    {
        var command = new Command("manage", "Lab operations: rooms, scans and fleet command runs from the roster");
        command.AddCommand(CreateRoomsCommand(config));
        command.AddCommand(CreateScanCommand(config, reportMate));
        command.AddCommand(CreateRunCommand(config, reportMate));
        command.AddCommand(CreateLibraryCommand(config));
        command.AddCommand(CreateAuditCommand(config));
        return command;
    }

    // ── rooms ────────────────────────────────────────────────────────────

    private static Command CreateRoomsCommand(FleetMateConfig config)
    {
        var cmd = new Command("rooms", "List rooms and groups from the roster");
        var sectionOption = new Option<string?>(aliases: ["--section", "-s"], description: "labs, kiosks, staff or faculty (default: all)");
        var jsonOption = new Option<bool>(aliases: ["--json"], description: "Output as JSON");
        cmd.AddOption(sectionOption);
        cmd.AddOption(jsonOption);

        cmd.SetHandler((section, json) =>
        {
            var roster = LoadRoster(config);
            if (roster == null) return;

            var sections = Enum.GetValues<RosterSection>()
                .Where(s => string.IsNullOrEmpty(section) || s.ToString().Equals(section, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (json)
            {
                var payload = sections.Select(s => new { Section = s.ToString(), Rooms = roster.Rooms(s).Select(r => new { r.Number, r.DisplayName, r.Count }) });
                Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
                return;
            }

            foreach (var s in sections)
            {
                var table = new Table().Border(TableBorder.Rounded).Title($"[bold]{s}[/]");
                table.AddColumn("Room");
                table.AddColumn("Name");
                table.AddColumn(new TableColumn("Machines").RightAligned());
                foreach (var room in roster.Rooms(s))
                    table.AddRow(Markup.Escape(room.Number), Markup.Escape(room.DisplayName ?? ""), room.Count.ToString());
                AnsiConsole.Write(table);
            }
            AnsiConsole.MarkupLine($"[dim]{roster.Source.Count} machines in the roster, {roster.RetiredCount} retired hidden[/]");
        }, sectionOption, jsonOption);

        return cmd;
    }

    // ── scan ─────────────────────────────────────────────────────────────

    private static Command CreateScanCommand(FleetMateConfig config, ReportMateService? reportMate)
    {
        var cmd = new Command("scan", "Scan a room: resolve addresses and check whether SSH and RDP answer");
        var roomArg = new Argument<string>("room", "Room number, lab name, or search text");
        var jsonOption = new Option<bool>(aliases: ["--json"], description: "Output as JSON");
        cmd.AddArgument(roomArg);
        cmd.AddOption(jsonOption);

        cmd.SetHandler(async (roomQuery, json) =>
        {
            var roster = LoadRoster(config);
            if (roster == null) return;
            var room = FindRoom(roster, roomQuery);
            if (room == null) return;

            var (results, summary) = await ScanAsync(config, reportMate, room, json ? null : new Progress<string>(s => AnsiConsole.MarkupLine($"[dim]{Markup.Escape(s)}[/]")));

            if (json)
            {
                var payload = room.Computers.Select(c =>
                {
                    var r = results[c.Serial];
                    return new { c.Serial, Name = c.FriendlyName, c.Hostname, r.Ip, Source = r.Source.ToString(), State = r.State.ToString(), r.SshOpen, r.RdpOpen, r.AddressCollectedAt };
                });
                Console.WriteLine(JsonSerializer.Serialize(new { Room = room.Name, Summary = summary, Machines = payload }, JsonOptions));
                return;
            }

            var table = new Table().Border(TableBorder.Rounded).Title($"[bold]{Markup.Escape(room.Name)}[/]");
            table.AddColumn("Machine");
            table.AddColumn("Hostname");
            table.AddColumn("Address");
            table.AddColumn("State");
            table.AddColumn("SSH");
            table.AddColumn("RDP");
            foreach (var c in room.Computers)
            {
                var r = results[c.Serial];
                var state = r.State switch
                {
                    HostState.Online => "[green]online[/]",
                    HostState.Unreachable => "[yellow]no answer[/]",
                    _ => "[dim]offline[/]"
                };
                var addr = Markup.Escape(r.Ip) + (r.AddressIsStale ? " [yellow](stale)[/]" : "");
                table.AddRow(Markup.Escape(c.FriendlyName), Markup.Escape(c.Hostname), addr, state, r.SshOpen ? "[green]open[/]" : "[dim]-[/]", r.RdpOpen ? "[green]open[/]" : "[dim]-[/]");
            }
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[dim]{summary.Mode.Label()}: {summary.Online} online, {summary.Resolved}/{summary.Total} resolved in {summary.Duration.TotalSeconds:0.0}s[/]");
        }, roomArg, jsonOption);

        return cmd;
    }

    // ── run ──────────────────────────────────────────────────────────────

    private static Command CreateRunCommand(FleetMateConfig config, ReportMateService? reportMate)
    {
        var cmd = new Command("run", "Run a PowerShell command on every online machine in a room");
        var roomArg = new Argument<string>("room", "Room number, lab name, or search text");
        var commandArg = new Argument<string>("command", "PowerShell to run, or a library command label");
        var yesOption = new Option<bool>(aliases: ["--yes", "-y"], description: "Skip the confirmation for caution and destructive commands");
        var concurrencyOption = new Option<int>(aliases: ["--concurrent", "-n"], getDefaultValue: () => 12, description: "Parallel connections");
        var jsonOption = new Option<bool>(aliases: ["--json"], description: "Output as JSON");
        cmd.AddArgument(roomArg);
        cmd.AddArgument(commandArg);
        cmd.AddOption(yesOption);
        cmd.AddOption(concurrencyOption);
        cmd.AddOption(jsonOption);

        cmd.SetHandler(async (roomQuery, commandText, yes, concurrency, json) =>
        {
            var manage = config.Manage ?? new ManageConfig();
            if (!manage.HasSshKey)
            {
                AnsiConsole.MarkupLine($"[red]No SSH key at {Markup.Escape(manage.ResolvedSshKeyPath)}.[/] Set SecureShellKeyPath in the desktop settings or place the key there.");
                return;
            }

            var roster = LoadRoster(config);
            if (roster == null) return;
            var room = FindRoom(roster, roomQuery);
            if (room == null) return;

            // A library label runs the library command; anything else is literal PowerShell.
            var library = CommandLibrary.Load(manage.ResolvedCommandsPath);
            if (!File.Exists(manage.ResolvedCommandsPath)) library = CommandLibrary.LoadBundled();
            var libraryCommand = library.SelectMany(c => c.Commands).FirstOrDefault(c => c.Label.Equals(commandText, StringComparison.OrdinalIgnoreCase));
            var script = libraryCommand?.Command ?? commandText;
            var label = libraryCommand?.Label ?? "Custom command";
            var trust = libraryCommand?.TrustLevel ?? TrustInference.Infer(script);

            if (PlaceholderTemplate.Detect(label, script) is { } template)
            {
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(label)} needs values for {string.Join(", ", template.Placeholders)}; fill them in and pass the resolved command.[/]");
                return;
            }

            var (results, summary) = await ScanAsync(config, reportMate, room, json ? null : new Progress<string>(s => AnsiConsole.MarkupLine($"[dim]{Markup.Escape(s)}[/]")));
            var targets = room.Computers.Where(c => results[c.Serial].State == HostState.Online && results[c.Serial].SshOpen)
                .Select(c => new RunTarget(c, results[c.Serial].Ip)).ToList();
            if (targets.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No machine in the room answered on SSH.[/]");
                return;
            }

            if (trust != CommandTrustLevel.Safe && !yes)
            {
                if (json)
                {
                    AnsiConsole.MarkupLine($"[red]{trust.Label()} command needs --yes when running with --json.[/]");
                    return;
                }
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(trust.WarningMessage())}[/]");
                if (!AnsiConsole.Confirm($"Run [bold]{Markup.Escape(label)}[/] on {targets.Count} machine(s)?", false)) return;
            }

            using var ssh = new SecureShellService(manage.ToSecureShellConfig());
            var runner = new CommandRunner(new SecureShellRemoteRunner(ssh)) { Concurrency = Math.Max(1, concurrency) };
            var observer = new ConsoleObserver(targets, json);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            await runner.RunAsync(targets, script, observer, cts.Token);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { Room = room.Name, Command = script, Results = observer.Snapshot() }, JsonOptions));
                return;
            }
            observer.PrintSummary();
        }, roomArg, commandArg, yesOption, concurrencyOption, jsonOption);

        return cmd;
    }

    private sealed class ConsoleObserver : IRunObserver
    {
        private readonly Dictionary<string, RunTarget> _targets;
        private readonly Dictionary<string, (CommandRunStatus status, int? exit, string output, string stderr, string? error)> _results = new();
        private readonly Dictionary<string, System.Text.StringBuilder> _output = new();
        private readonly bool _quiet;
        private readonly object _lock = new();

        public ConsoleObserver(IEnumerable<RunTarget> targets, bool quiet)
        {
            _targets = targets.ToDictionary(t => t.Computer.Serial);
            _quiet = quiet;
        }

        public void Started(string serial) { lock (_lock) _output[serial] = new System.Text.StringBuilder(); }

        public void Output(string serial, string chunk) { lock (_lock) { if (_output.TryGetValue(serial, out var sb)) sb.Append(chunk); } }

        public void Finished(string serial, CommandRunStatus status, int? exitCode, string stderr, string? error)
        {
            string output;
            lock (_lock)
            {
                output = _output.TryGetValue(serial, out var sb) ? sb.ToString() : "";
                _results[serial] = (status, exitCode, output, stderr, error);
            }
            if (_quiet) return;
            var name = _targets.TryGetValue(serial, out var t) ? t.Computer.FriendlyName : serial;
            var colour = status == CommandRunStatus.Success ? "green" : status == CommandRunStatus.Offline ? "grey" : "red";
            AnsiConsole.MarkupLine($"[{colour}]{Markup.Escape(name)}[/] [dim]{Markup.Escape(status.Label(exitCode).ToLowerInvariant())}[/]");
            foreach (var line in output.TrimEnd().Split('\n').Where(l => l.Trim().Length > 0))
                AnsiConsole.MarkupLine("    " + Markup.Escape(line.TrimEnd('\r')));
            var err = string.IsNullOrWhiteSpace(stderr) ? error : stderr;
            if (!string.IsNullOrWhiteSpace(err))
                AnsiConsole.MarkupLine("    [red]" + Markup.Escape(err.Trim()) + "[/]");
        }

        public void PrintSummary()
        {
            var ok = _results.Count(r => r.Value.status == CommandRunStatus.Success);
            var offline = _results.Count(r => r.Value.status == CommandRunStatus.Offline);
            var failed = _results.Count - ok - offline;
            AnsiConsole.MarkupLine($"[bold]{ok} succeeded[/], {failed} failed, {offline} offline of {_results.Count}");
        }

        public IEnumerable<object> Snapshot() => _results.Select(kv => new
        {
            Serial = kv.Key,
            Name = _targets.TryGetValue(kv.Key, out var t) ? t.Computer.FriendlyName : kv.Key,
            Ip = _targets.TryGetValue(kv.Key, out var t2) ? t2.Ip : "",
            Status = kv.Value.status.ToString(),
            ExitCode = kv.Value.exit,
            Output = kv.Value.output,
            Stderr = kv.Value.stderr,
            Error = kv.Value.error
        });
    }

    // ── library / audit ──────────────────────────────────────────────────

    private static Command CreateLibraryCommand(FleetMateConfig config)
    {
        var cmd = new Command("library", "List the command library (per-user file, or the bundled one)");
        var bundledOption = new Option<bool>(aliases: ["--bundled"], description: "Show the library that ships with FleetMate");
        cmd.AddOption(bundledOption);

        cmd.SetHandler(bundled =>
        {
            var manage = config.Manage ?? new ManageConfig();
            var categories = bundled || !File.Exists(manage.ResolvedCommandsPath) ? CommandLibrary.LoadBundled() : CommandLibrary.Load(manage.ResolvedCommandsPath);
            var source = bundled || !File.Exists(manage.ResolvedCommandsPath) ? "bundled" : manage.ResolvedCommandsPath;
            var table = new Table().Border(TableBorder.Rounded).Title($"[bold]Command library[/] [dim]({Markup.Escape(source)})[/]");
            table.AddColumn("Category");
            table.AddColumn("Label");
            table.AddColumn("Trust");
            foreach (var c in categories)
                foreach (var x in c.Commands)
                {
                    var colour = x.TrustLevel switch { CommandTrustLevel.Destructive => "red", CommandTrustLevel.Caution => "yellow", _ => "green" };
                    table.AddRow(Markup.Escape(c.Name), Markup.Escape(x.Label), $"[{colour}]{x.TrustLevel.ToYaml()}[/]");
                }
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[dim]{categories.Sum(c => c.Commands.Count)} commands in {categories.Count} categories[/]");
        }, bundledOption);

        return cmd;
    }

    private static Command CreateAuditCommand(FleetMateConfig config)
    {
        var cmd = new Command("audit", "Check a command library for duplicates, empty commands and understated trust");
        var pathArg = new Argument<string?>("path", () => null, "Library file (default: the per-user library, else the bundled one)");
        cmd.AddArgument(pathArg);

        cmd.SetHandler(path =>
        {
            var manage = config.Manage ?? new ManageConfig();
            List<CommandCategory> categories;
            string source;
            if (!string.IsNullOrEmpty(path)) { categories = CommandLibrary.Load(path); source = path; }
            else if (File.Exists(manage.ResolvedCommandsPath)) { categories = CommandLibrary.Load(manage.ResolvedCommandsPath); source = manage.ResolvedCommandsPath; }
            else { categories = CommandLibrary.LoadBundled(); source = "bundled"; }

            var issues = CommandLibrary.Audit(categories);
            var total = categories.Sum(c => c.Commands.Count);
            var byTrust = categories.SelectMany(c => c.Commands).GroupBy(c => c.TrustLevel).ToDictionary(g => g.Key, g => g.Count());
            AnsiConsole.MarkupLine($"Commands audited: [bold]{total}[/] in {categories.Count} categories ({Markup.Escape(source)})");
            AnsiConsole.MarkupLine($"Trust levels: {byTrust.GetValueOrDefault(CommandTrustLevel.Safe)} safe, {byTrust.GetValueOrDefault(CommandTrustLevel.Caution)} caution, {byTrust.GetValueOrDefault(CommandTrustLevel.Destructive)} destructive");
            var tooLong = categories.SelectMany(c => c.Commands.Select(x => (c.Name, x))).Where(t => !RemoteScriptEncoder.Fits(t.x.Command)).ToList();
            foreach (var (category, x) in tooLong)
                AnsiConsole.MarkupLine($"[red]error[/] {Markup.Escape(category)} / {Markup.Escape(x.Label)}: too long to send as an encoded command");
            foreach (var issue in issues)
            {
                var colour = issue.Severity switch { CommandAuditSeverity.Error => "red", CommandAuditSeverity.Warning => "yellow", _ => "dim" };
                AnsiConsole.MarkupLine($"[{colour}]{issue.Severity.ToString().ToLowerInvariant()}[/] {Markup.Escape(issue.Category)} / {Markup.Escape(issue.Label)}: {Markup.Escape(issue.Message)}");
            }
            var errors = issues.Count(i => i.Severity == CommandAuditSeverity.Error) + tooLong.Count;
            AnsiConsole.MarkupLine($"Summary: {errors} errors, {issues.Count(i => i.Severity == CommandAuditSeverity.Warning)} warnings");
            Environment.ExitCode = errors > 0 ? 1 : 0;
        }, pathArg);

        return cmd;
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static FleetRoster? LoadRoster(FleetMateConfig config)
    {
        var manage = config.Manage ?? new ManageConfig();
        var path = ManageConfig.ExpandHome(manage.RosterPath);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            AnsiConsole.MarkupLine("[red]No roster configured.[/] Set ManageRosterPath (the enrollment computers.csv) in the desktop settings.");
            return null;
        }
        var roster = new RosterLoader { IncludeRetired = manage.IncludeRetired, IncludeProvisioning = manage.IncludeProvisioning }.Load(path);
        if (roster.Source.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]Roster at {Markup.Escape(path)} is empty or has no serial column.[/]");
            return null;
        }
        return roster;
    }

    internal static RosterRoom? FindRoom(FleetRoster roster, string query)
    {
        var q = query.Trim();
        var rooms = Enum.GetValues<RosterSection>().SelectMany(roster.Rooms).ToList();
        var exact = rooms.FirstOrDefault(r => r.Number.Equals(q, StringComparison.OrdinalIgnoreCase) || r.Name.Equals(q, StringComparison.OrdinalIgnoreCase)
                                              || (r.DisplayName?.Equals(q, StringComparison.OrdinalIgnoreCase) ?? false));
        if (exact != null) return exact;

        var matches = rooms.Where(r => r.Number.Contains(q, StringComparison.OrdinalIgnoreCase) || (r.DisplayName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1)
        {
            AnsiConsole.MarkupLine($"[yellow]'{Markup.Escape(q)}' matches {matches.Count} rooms:[/] {string.Join(", ", matches.Select(m => Markup.Escape(m.Name)))}");
            return null;
        }

        // Search text: any machine field, as a temporary room.
        var hits = roster.Source.Where(c => GuiLikeMatch(c, q)).ToList();
        if (hits.Count > 0) return new RosterRoom { Number = $"Search: {q}", Computers = hits };

        AnsiConsole.MarkupLine($"[red]No room or machine matches '{Markup.Escape(q)}'.[/] Try 'fleetmate manage rooms'.");
        return null;
    }

    private static bool GuiLikeMatch(RosterComputer c, string q) =>
        c.Hostname.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Serial.Contains(q, StringComparison.OrdinalIgnoreCase)
        || c.Allocation.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Username.Contains(q, StringComparison.OrdinalIgnoreCase)
        || c.Asset.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Location.Contains(q, StringComparison.OrdinalIgnoreCase)
        || c.Fleet.Contains(q, StringComparison.OrdinalIgnoreCase);

    private static async Task<(Dictionary<string, HostScanResult> results, ScanSummary summary)> ScanAsync(
        FleetMateConfig config, ReportMateService? reportMate, RosterRoom room, IProgress<string>? progress)
    {
        // A placeholder URL means ReportMate is not really configured; scanning
        // still works over DNS and the probes.
        var url = config.ReportMateUrl ?? "";
        var directory = reportMate != null && !url.Contains("example", StringComparison.OrdinalIgnoreCase) ? new ReportMateDeviceDirectory(reportMate) : null;
        var scanner = new HostScanner(directory, new NetworkReachabilityProbe());
        return await scanner.ScanAsync(room.Computers, null, progress, CancellationToken.None);
    }
}
