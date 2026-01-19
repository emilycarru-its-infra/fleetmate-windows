using System.CommandLine;
using System.Text.Json;
using FleetMate.Models.Tdx;
using FleetMate.Services;
using Spectre.Console;

namespace FleetMate.Commands;

public static class TdxCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static Command Create(TdxService? tdxService, ReportMateService? reportMate)
    {
        var command = new Command("tdx", "TeamDynamix ticket management");

        command.AddCommand(CreateTicketsCommand(tdxService));
        command.AddCommand(CreateTicketCommand(tdxService));
        command.AddCommand(CreateCreateCommand(tdxService));
        command.AddCommand(CreateCommentCommand(tdxService));
        command.AddCommand(CreateFromErrorCommand(tdxService, reportMate));
        command.AddCommand(CreateStatusesCommand(tdxService));
        command.AddCommand(CreateAssetsCommand(tdxService));
        command.AddCommand(CreateAssetCommand(tdxService));

        return command;
    }

    private static Command CreateTicketsCommand(TdxService? tdxService)
    {
        var command = new Command("tickets", "List tickets");

        var statusOption = new Option<string?>(
            aliases: ["--status", "-s"],
            description: "Filter by status class (New, None, InProcess, Completed, Cancelled)");

        var typeOption = new Option<int?>(
            aliases: ["--type", "-t"],
            description: "Filter by type ID");

        var searchOption = new Option<string?>(
            aliases: ["--search", "-q"],
            description: "Search text");

        var openOption = new Option<bool>(
            aliases: ["--open"],
            description: "Show only open tickets");

        var limitOption = new Option<int>(
            aliases: ["--limit", "-n"],
            getDefaultValue: () => 25,
            description: "Maximum results (default: 25)");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddOption(statusOption);
        command.AddOption(typeOption);
        command.AddOption(searchOption);
        command.AddOption(openOption);
        command.AddOption(limitOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (status, typeId, search, openOnly, limit, json) =>
        {
            if (!EnsureConfigured(tdxService)) return;

            var request = new TicketSearchRequest
            {
                MaxResults = limit
            };

            if (!string.IsNullOrEmpty(status))
            {
                request.StatusClassNames = new List<string> { status };
            }

            if (typeId.HasValue)
            {
                request.TypeIds = new List<int> { typeId.Value };
            }

            if (!string.IsNullOrEmpty(search))
            {
                request.SearchText = search;
            }

            if (openOnly)
            {
                request.StatusClassNames = new List<string> { "New", "None" };
                request.IsClosed = false;
            }

            List<TdxTicket> tickets = new();

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Fetching tickets...", async ctx =>
                {
                    tickets = await tdxService!.SearchTicketsAsync(request, limit);
                });

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(tickets, JsonOptions));
                return;
            }

            if (tickets.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No tickets found[/]");
                return;
            }

            DisplayTickets(tickets);
        }, statusOption, typeOption, searchOption, openOption, limitOption, jsonOption);

        return command;
    }

    private static Command CreateAssetsCommand(TdxService? tdxService)
    {
        var command = new Command("assets", "Search assets (partial results)");

        var searchOption = new Option<string?>(
            aliases: ["--search", "-q"],
            description: "Search text (name, tag, serial, etc.)");

        var limitOption = new Option<int>(
            aliases: ["--limit", "-n"],
            getDefaultValue: () => 25,
            description: "Maximum results (default: 25)");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddOption(searchOption);
        command.AddOption(limitOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (search, limit, json) =>
        {
            if (!EnsureConfigured(tdxService)) return;

            List<TdxAsset> assets = new();

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Searching assets...", async ctx =>
                {
                    assets = await tdxService!.SearchAssetsAsync(search, limit);
                });

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(assets, JsonOptions));
                return;
            }

            if (assets.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No assets found[/]");
                return;
            }

            DisplayAssets(assets);
        }, searchOption, limitOption, jsonOption);

        return command;
    }

    private static Command CreateAssetCommand(TdxService? tdxService)
    {
        var command = new Command("asset", "Get asset details by ID");

        var idArg = new Argument<int>(
            name: "id",
            description: "Asset ID");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddArgument(idArg);
        command.AddOption(jsonOption);

        command.SetHandler(async (id, json) =>
        {
            if (!EnsureConfigured(tdxService)) return;

            TdxAsset? asset = null;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Fetching asset...", async ctx =>
                {
                    asset = await tdxService!.GetAssetAsync(id);
                });

            if (asset == null)
            {
                AnsiConsole.MarkupLine("[yellow]Asset not found[/]");
                return;
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(asset, JsonOptions));
                return;
            }

            DisplayAsset(asset);
        }, idArg, jsonOption);

        return command;
    }

    private static Command CreateTicketCommand(TdxService? tdxService)
    {
        var command = new Command("ticket", "Get ticket details");

        var idArg = new Argument<int>(
            name: "id",
            description: "Ticket ID");

        var feedOption = new Option<bool>(
            aliases: ["--feed", "-f"],
            description: "Include feed/comments");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddArgument(idArg);
        command.AddOption(feedOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (id, includeFeed, json) =>
        {
            if (!EnsureConfigured(tdxService)) return;

            TdxTicket? ticket = null;
            List<TdxFeedEntry> feed = new();

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Fetching ticket {id}...", async ctx =>
                {
                    ticket = await tdxService!.GetTicketAsync(id);
                    if (ticket != null && includeFeed)
                    {
                        feed = await tdxService.GetTicketFeedAsync(id);
                    }
                });

            if (ticket == null)
            {
                AnsiConsole.MarkupLine($"[yellow]Ticket not found: {id}[/]");
                return;
            }

            if (json)
            {
                var result = includeFeed
                    ? new { Ticket = ticket, Feed = feed } as object
                    : ticket;
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                return;
            }

            DisplayTicketDetail(ticket);

            if (includeFeed && feed.Count > 0)
            {
                DisplayFeed(feed);
            }
        }, idArg, feedOption, jsonOption);

        return command;
    }

    private static Command CreateCreateCommand(TdxService? tdxService)
    {
        var command = new Command("create", "Create a new ticket");

        var titleArg = new Argument<string>(
            name: "title",
            description: "Ticket title");

        var descriptionOption = new Option<string?>(
            aliases: ["--description", "-d"],
            description: "Ticket description");

        var typeOption = new Option<int?>(
            aliases: ["--type", "-t"],
            description: "Type ID");

        var priorityOption = new Option<int?>(
            aliases: ["--priority", "-p"],
            description: "Priority ID");

        var requestorOption = new Option<string?>(
            aliases: ["--requestor", "-r"],
            description: "Requestor email");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddArgument(titleArg);
        command.AddOption(descriptionOption);
        command.AddOption(typeOption);
        command.AddOption(priorityOption);
        command.AddOption(requestorOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (title, description, typeId, priorityId, requestorEmail, json) =>
        {
            if (!EnsureConfigured(tdxService)) return;

            var request = new CreateTicketRequest
            {
                Title = title,
                Description = description
            };

            if (typeId.HasValue)
            {
                request.TypeId = typeId.Value;
            }

            if (priorityId.HasValue)
            {
                request.PriorityId = priorityId.Value;
            }

            if (!string.IsNullOrEmpty(requestorEmail))
            {
                request.RequestorEmail = requestorEmail;
            }

            TdxTicket? ticket = null;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Creating ticket...", async ctx =>
                {
                    ticket = await tdxService!.CreateTicketAsync(request);
                });

            if (ticket == null)
            {
                AnsiConsole.MarkupLine("[red]Failed to create ticket[/]");
                return;
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(ticket, JsonOptions));
                return;
            }

            AnsiConsole.MarkupLine($"[green]Created ticket {ticket.Id}:[/] {Markup.Escape(ticket.Title)}");
            if (!string.IsNullOrEmpty(ticket.Uri))
            {
                AnsiConsole.MarkupLine($"[dim]URL:[/] {ticket.Uri}");
            }
        }, titleArg, descriptionOption, typeOption, priorityOption, requestorOption, jsonOption);

        return command;
    }

    private static Command CreateCommentCommand(TdxService? tdxService)
    {
        var command = new Command("comment", "Add a comment to a ticket");

        var idArg = new Argument<int>(
            name: "id",
            description: "Ticket ID");

        var commentArg = new Argument<string>(
            name: "comment",
            description: "Comment text");

        var privateOption = new Option<bool>(
            aliases: ["--private", "-p"],
            description: "Make comment private (not visible to requestor)");

        command.AddArgument(idArg);
        command.AddArgument(commentArg);
        command.AddOption(privateOption);

        command.SetHandler(async (id, comment, isPrivate) =>
        {
            if (!EnsureConfigured(tdxService)) return;

            bool success = false;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Adding comment to ticket {id}...", async ctx =>
                {
                    success = await tdxService!.AddCommentAsync(id, comment, isPrivate);
                });

            if (success)
            {
                AnsiConsole.MarkupLine($"[green]Added comment to ticket {id}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Failed to add comment to ticket {id}[/]");
            }
        }, idArg, commentArg, privateOption);

        return command;
    }

    private static Command CreateFromErrorCommand(TdxService? tdxService, ReportMateService? reportMate)
    {
        var command = new Command("from-error", "Create a ticket from a deployment error");

        var deviceArg = new Argument<string>(
            name: "device",
            description: "Device name");

        var itemArg = new Argument<string>(
            name: "item",
            description: "Software item name with error");

        var typeOption = new Option<int?>(
            aliases: ["--type", "-t"],
            description: "Ticket type ID");

        var priorityOption = new Option<int?>(
            aliases: ["--priority", "-p"],
            description: "Priority ID");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddArgument(deviceArg);
        command.AddArgument(itemArg);
        command.AddOption(typeOption);
        command.AddOption(priorityOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (device, itemName, typeId, priorityId, json) =>
        {
            if (!EnsureConfigured(tdxService)) return;

            if (reportMate == null)
            {
                AnsiConsole.MarkupLine("[red]ReportMate is not configured[/]");
                return;
            }

            TdxTicket? ticket = null;
            string deviceName = device;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Creating ticket from error...", async ctx =>
                {
                    // Fetch error details from ReportMate
                    ctx.Status("Fetching error details...");
                    var installs = await reportMate.GetDeviceInstallsAsync(device);
                    var failedInstall = installs.FirstOrDefault(i =>
                        i.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase) && i.IsError);

                    if (failedInstall == null)
                    {
                        AnsiConsole.MarkupLine($"[yellow]No error found for {itemName} on {device}[/]");
                        return;
                    }

                    deviceName = failedInstall.DeviceName ?? device;
                    var errorMessage = failedInstall.LastError ?? failedInstall.CurrentStatus ?? "Unknown error";

                    // Get device info
                    var deviceInfo = await reportMate.FindDeviceAsync(device);
                    if (deviceInfo != null)
                    {
                        deviceName = deviceInfo.DisplayName;
                    }

                    // Build ticket
                    var title = $"[FleetMate] {deviceName}: {itemName} deployment failure";
                    var description = $"""
                        ## Deployment Error Report

                        **Device:** {deviceName}
                        **Software:** {itemName}
                        **Status:** {failedInstall.CurrentStatus ?? "Error"}

                        ### Error Message
                        {errorMessage}

                        ### Additional Details
                        - Serial: {deviceInfo?.SerialNumber ?? "Unknown"}
                        - IP Address: {deviceInfo?.IpAddress ?? "Unknown"}
                        - Last Seen: {deviceInfo?.LastSeen?.ToString("g") ?? "Unknown"}

                        ---
                        *Ticket created by FleetMate CLI*
                        """;

                    var request = new CreateTicketRequest
                    {
                        Title = title,
                        Description = description
                    };

                    if (typeId.HasValue)
                    {
                        request.TypeId = typeId.Value;
                    }

                    if (priorityId.HasValue)
                    {
                        request.PriorityId = priorityId.Value;
                    }

                    ctx.Status("Creating ticket...");
                    ticket = await tdxService!.CreateTicketAsync(request);
                });

            if (ticket == null)
            {
                AnsiConsole.MarkupLine("[red]Failed to create ticket from error[/]");
                return;
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(ticket, JsonOptions));
                return;
            }

            AnsiConsole.MarkupLine($"[green]Created ticket {ticket.Id}[/] from {device}/{itemName} error");
            if (!string.IsNullOrEmpty(ticket.Uri))
            {
                AnsiConsole.MarkupLine($"[dim]URL:[/] {ticket.Uri}");
            }
        }, deviceArg, itemArg, typeOption, priorityOption, jsonOption);

        return command;
    }

    private static Command CreateStatusesCommand(TdxService? tdxService)
    {
        var command = new Command("statuses", "List available ticket statuses, types, and priorities");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddOption(jsonOption);

        command.SetHandler(async (json) =>
        {
            if (!EnsureConfigured(tdxService)) return;

            Dictionary<int, string> statuses = new();
            Dictionary<int, string> types = new();
            Dictionary<int, string> priorities = new();

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Fetching reference data...", async ctx =>
                {
                    statuses = await tdxService!.GetStatusesAsync();
                    types = await tdxService.GetTypesAsync();
                    priorities = await tdxService.GetPrioritiesAsync();
                });

            if (json)
            {
                var result = new { Statuses = statuses, Types = types, Priorities = priorities };
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                return;
            }

            // Display statuses
            if (statuses.Count > 0)
            {
                AnsiConsole.Write(new Rule("[bold]Statuses[/]").LeftJustified());
                var statusTable = new Table { Border = TableBorder.Simple };
                statusTable.AddColumn("ID");
                statusTable.AddColumn("Name");
                foreach (var s in statuses.OrderBy(x => x.Key))
                {
                    statusTable.AddRow(s.Key.ToString(), Markup.Escape(s.Value));
                }
                AnsiConsole.Write(statusTable);
            }

            // Display types
            if (types.Count > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Rule("[bold]Types[/]").LeftJustified());
                var typeTable = new Table { Border = TableBorder.Simple };
                typeTable.AddColumn("ID");
                typeTable.AddColumn("Name");
                foreach (var t in types.OrderBy(x => x.Key))
                {
                    typeTable.AddRow(t.Key.ToString(), Markup.Escape(t.Value));
                }
                AnsiConsole.Write(typeTable);
            }

            // Display priorities
            if (priorities.Count > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Rule("[bold]Priorities[/]").LeftJustified());
                var priorityTable = new Table { Border = TableBorder.Simple };
                priorityTable.AddColumn("ID");
                priorityTable.AddColumn("Name");
                foreach (var p in priorities.OrderBy(x => x.Key))
                {
                    priorityTable.AddRow(p.Key.ToString(), Markup.Escape(p.Value));
                }
                AnsiConsole.Write(priorityTable);
            }
        }, jsonOption);

        return command;
    }

    private static bool EnsureConfigured(TdxService? tdx)
    {
        if (tdx != null) return true;

        AnsiConsole.MarkupLine("[red]TeamDynamix is not configured.[/]");
        AnsiConsole.MarkupLine("Set the following environment variables:");
        AnsiConsole.MarkupLine("  [cyan]TDX_BASE_URL[/]         - Your TDX Web API URL");
        AnsiConsole.MarkupLine("  [cyan]TDX_APP_ID[/]           - Your TDX application ID");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("Authentication (choose one):");
        AnsiConsole.MarkupLine("  Admin: [cyan]TDX_BEID[/] + [cyan]TDX_WEB_SERVICES_KEY[/]");
        AnsiConsole.MarkupLine("  User:  [cyan]TDX_USERNAME[/] + [cyan]TDX_PASSWORD[/]");
        return false;
    }

    private static void DisplayTickets(List<TdxTicket> tickets)
    {
        var table = new Table();
        table.Border = TableBorder.Rounded;
        table.AddColumn("ID");
        table.AddColumn("Title");
        table.AddColumn("Status");
        table.AddColumn("Priority");
        table.AddColumn("Requestor");
        table.AddColumn("Created");

        foreach (var ticket in tickets)
        {
            var statusColor = ticket.StatusClass?.ToLowerInvariant() switch
            {
                "new" => "cyan",
                "none" => "blue",
                "inprocess" => "yellow",
                "completed" => "green",
                "cancelled" => "dim",
                _ => "white"
            };

            table.AddRow(
                ticket.Id.ToString(),
                Markup.Escape(TruncateText(ticket.Title, 40)),
                $"[{statusColor}]{ticket.StatusName ?? "-"}[/]",
                ticket.PriorityName ?? "-",
                Markup.Escape(TruncateText(ticket.RequestorName ?? "-", 20)),
                ticket.CreatedDate.ToString("MM/dd/yy"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]Showing {tickets.Count} tickets[/]");
    }

    private static void DisplayTicketDetail(TdxTicket ticket)
    {
        var statusColor = ticket.IsClosed ? "dim" : (ticket.IsOpen ? "green" : "yellow");

        var panel = new Panel(
            new Rows(
                new Markup($"[bold]#{ticket.Id}[/] {Markup.Escape(ticket.Title)}"),
                new Text(""),
                new Markup($"[dim]Status:[/] [{statusColor}]{ticket.StatusName}[/] ({ticket.StatusClass})"),
                new Markup($"[dim]Priority:[/] {ticket.PriorityName ?? "-"}"),
                new Markup($"[dim]Type:[/] {ticket.TypeName ?? "-"}"),
                new Text(""),
                new Markup($"[dim]Requestor:[/] {Markup.Escape(ticket.RequestorName ?? "-")}"),
                new Markup($"[dim]Email:[/] {ticket.RequestorEmail ?? "-"}"),
                new Markup($"[dim]Responsible:[/] {Markup.Escape(ticket.ResponsibleFullName ?? ticket.ResponsibleGroupName ?? "-")}"),
                new Text(""),
                new Markup($"[dim]Created:[/] {ticket.CreatedDate:g} by {Markup.Escape(ticket.CreatedFullName ?? "-")}"),
                new Markup($"[dim]Modified:[/] {ticket.ModifiedDate?.ToString("g") ?? "-"}"),
                new Markup($"[dim]Days Old:[/] {ticket.DaysOld}"),
                new Text(""),
                new Markup($"[dim]SLA:[/] {(ticket.IsSlaViolated ? "[red]Violated[/]" : "[green]OK[/]")} - Respond by: {ticket.RespondByDate?.ToString("g") ?? "-"}")
            ))
        {
            Header = new PanelHeader(" TDX Ticket "),
            Border = BoxBorder.Rounded
        };

        AnsiConsole.Write(panel);

        if (!string.IsNullOrEmpty(ticket.Description))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[dim]Description[/]").LeftJustified());
            AnsiConsole.WriteLine(ticket.Description);
        }

        if (!string.IsNullOrEmpty(ticket.Uri))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[dim]URL:[/] {ticket.Uri}");
        }
    }

    private static void DisplayAssets(List<TdxAsset> assets)
    {
        var table = new Table();
        table.Border = TableBorder.Rounded;
        table.AddColumn("ID");
        table.AddColumn("Tag");
        table.AddColumn("Name");
        table.AddColumn("Serial");
        table.AddColumn("Status");
        table.AddColumn("Location");

        foreach (var asset in assets)
        {
            table.AddRow(
                asset.Id.ToString(),
                asset.Tag ?? "-",
                Markup.Escape(TruncateText(asset.Name ?? "-", 30)),
                Markup.Escape(TruncateText(asset.SerialNumber ?? "-", 20)),
                asset.Status ?? "-",
                Markup.Escape(TruncateText(asset.Location ?? "-", 20))
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]Showing {assets.Count} assets[/]");
    }

    private static void DisplayAsset(TdxAsset asset)
    {
        var panel = new Panel(
            new Rows(
                new Markup($"[bold]{Markup.Escape(asset.Name ?? "(no name)")}[/]"),
                new Text(""),
                new Markup($"[dim]ID:[/] {asset.Id}"),
                new Markup($"[dim]Tag:[/] {asset.Tag ?? "-"}"),
                new Markup($"[dim]Serial:[/] {asset.SerialNumber ?? "-"}"),
                new Markup($"[dim]Model:[/] {asset.Model ?? "-"}"),
                new Markup($"[dim]Manufacturer:[/] {asset.Manufacturer ?? "-"}"),
                new Markup($"[dim]Type:[/] {asset.ProductType ?? "-"}"),
                new Markup($"[dim]Status:[/] {asset.Status ?? "-"}"),
                new Markup($"[dim]Location:[/] {asset.Location ?? "-"}"),
                new Markup($"[dim]External ID:[/] {asset.ExternalId ?? "-"}")
            ))
        {
            Header = new PanelHeader(" TDX Asset "),
            Border = BoxBorder.Rounded
        };

        AnsiConsole.Write(panel);
    }

    private static void DisplayFeed(List<TdxFeedEntry> feed)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[dim]Comments/Feed[/]").LeftJustified());

        foreach (var entry in feed.OrderByDescending(e => e.CreatedDate).Take(10))
        {
            var visibility = entry.IsPrivate ? "[dim](Private)[/]" : "";
            AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(entry.CreatedFullName ?? "Unknown")}[/] {visibility} - [dim]{entry.CreatedDate:g}[/]");
            if (!string.IsNullOrEmpty(entry.Body))
            {
                // Strip HTML if rich text
                var body = entry.IsRichHtml
                    ? System.Text.RegularExpressions.Regex.Replace(entry.Body, "<[^>]*>", "")
                    : entry.Body;
                AnsiConsole.WriteLine($"  {TruncateText(body.Trim(), 200)}");
            }
            AnsiConsole.WriteLine();
        }
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text.Substring(0, maxLength - 3) + "...";
    }
}
