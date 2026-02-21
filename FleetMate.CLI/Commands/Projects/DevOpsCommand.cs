using System.CommandLine;
using System.Text.Json;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Tickets;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Reporting;
using Spectre.Console;

namespace FleetMate.Commands.Projects;

/// <summary>
/// Azure DevOps work item management commands
/// </summary>
public static class DevOpsCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static Command Create(AzureDevOpsService? adoService, ReportMateService? reportMate)
    {
        var command = new Command("devops", "Azure DevOps work item management");

        command.AddCommand(CreateItemsCommand(adoService));
        command.AddCommand(CreateItemCommand(adoService));
        command.AddCommand(CreateCreateCommand(adoService));
        command.AddCommand(CreateUpdateCommand(adoService));
        command.AddCommand(CreateSprintsCommand(adoService));
        command.AddCommand(CreateBoardsCommand(adoService));
        command.AddCommand(CreateFromErrorCommand(adoService, reportMate));

        return command;
    }

    private static Command CreateItemsCommand(AzureDevOpsService? adoService)
    {
        var command = new Command("items", "List work items");

        var queryOption = new Option<string?>(
            aliases: ["--query", "-q"],
            description: "WIQL query string");

        var stateOption = new Option<string?>(
            aliases: ["--state", "-s"],
            description: "Filter by state (e.g., New, Active, Closed)");

        var typeOption = new Option<string?>(
            aliases: ["--type", "-t"],
            description: "Filter by type (e.g., Bug, Task, User Story)");

        var assignedOption = new Option<string?>(
            aliases: ["--assigned", "-a"],
            description: "Filter by assigned to");

        var limitOption = new Option<int>(
            aliases: ["--limit", "-n"],
            getDefaultValue: () => 50,
            description: "Maximum results (default: 50)");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddOption(queryOption);
        command.AddOption(stateOption);
        command.AddOption(typeOption);
        command.AddOption(assignedOption);
        command.AddOption(limitOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (query, state, type, assigned, limit, json) =>
        {
            if (!EnsureConfigured(adoService)) return;

            List<WorkItem> items;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Fetching work items...", async ctx =>
                {
                    if (!string.IsNullOrEmpty(query))
                    {
                        items = await adoService!.QueryWorkItemsAsync(query);
                    }
                    else
                    {
                        items = await adoService!.GetWorkItemsAsync(state, type, assigned, limit);
                    }

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(items, JsonOptions));
                        return;
                    }

                    if (items.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[yellow]No work items found[/]");
                        return;
                    }

                    DisplayWorkItems(items);
                });
        }, queryOption, stateOption, typeOption, assignedOption, limitOption, jsonOption);

        return command;
    }

    private static Command CreateItemCommand(AzureDevOpsService? adoService)
    {
        var command = new Command("item", "Get work item details");

        var idArg = new Argument<int>(
            name: "id",
            description: "Work item ID");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddArgument(idArg);
        command.AddOption(jsonOption);

        command.SetHandler(async (id, json) =>
        {
            if (!EnsureConfigured(adoService)) return;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Fetching work item {id}...", async ctx =>
                {
                    var item = await adoService!.GetWorkItemAsync(id);

                    if (item == null)
                    {
                        AnsiConsole.MarkupLine($"[yellow]Work item {id} not found[/]");
                        return;
                    }

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(item, JsonOptions));
                        return;
                    }

                    DisplayWorkItemDetail(item);
                });
        }, idArg, jsonOption);

        return command;
    }

    private static Command CreateCreateCommand(AzureDevOpsService? adoService)
    {
        var command = new Command("create", "Create a new work item");

        var titleArg = new Argument<string>(
            name: "title",
            description: "Work item title");

        var typeOption = new Option<string>(
            aliases: ["--type", "-t"],
            getDefaultValue: () => "Bug",
            description: "Work item type (default: Bug)");

        var descriptionOption = new Option<string?>(
            aliases: ["--description", "-d"],
            description: "Description");

        var assignedOption = new Option<string?>(
            aliases: ["--assigned", "-a"],
            description: "Assign to user");

        var priorityOption = new Option<int?>(
            aliases: ["--priority", "-p"],
            description: "Priority (1-4)");

        var tagsOption = new Option<string?>(
            aliases: ["--tags"],
            description: "Tags (comma-separated)");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddArgument(titleArg);
        command.AddOption(typeOption);
        command.AddOption(descriptionOption);
        command.AddOption(assignedOption);
        command.AddOption(priorityOption);
        command.AddOption(tagsOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (title, type, description, assigned, priority, tags, json) =>
        {
            if (!EnsureConfigured(adoService)) return;

            var request = new CreateWorkItemRequest
            {
                Title = title,
                Type = type,
                Description = description,
                AssignedTo = assigned,
                Priority = priority,
                Tags = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList()
            };

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Creating work item...", async ctx =>
                {
                    var item = await adoService!.CreateWorkItemAsync(request);

                    if (item == null)
                    {
                        AnsiConsole.MarkupLine("[red]Failed to create work item[/]");
                        return;
                    }

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(item, JsonOptions));
                        return;
                    }

                    AnsiConsole.MarkupLine($"[green]Created work item {item.Id}[/]");
                    DisplayWorkItemDetail(item);
                });
        }, titleArg, typeOption, descriptionOption, assignedOption, priorityOption, tagsOption, jsonOption);

        return command;
    }

    private static Command CreateUpdateCommand(AzureDevOpsService? adoService)
    {
        var command = new Command("update", "Update a work item");

        var idArg = new Argument<int>(
            name: "id",
            description: "Work item ID");

        var stateOption = new Option<string?>(
            aliases: ["--state", "-s"],
            description: "New state");

        var assignedOption = new Option<string?>(
            aliases: ["--assigned", "-a"],
            description: "Reassign to user");

        var priorityOption = new Option<int?>(
            aliases: ["--priority", "-p"],
            description: "New priority (1-4)");

        var commentOption = new Option<string?>(
            aliases: ["--comment", "-c"],
            description: "Add comment");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddArgument(idArg);
        command.AddOption(stateOption);
        command.AddOption(assignedOption);
        command.AddOption(priorityOption);
        command.AddOption(commentOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (id, state, assigned, priority, comment, json) =>
        {
            if (!EnsureConfigured(adoService)) return;

            var request = new UpdateWorkItemRequest
            {
                State = state,
                AssignedTo = assigned,
                Priority = priority,
                Comment = comment
            };

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Updating work item {id}...", async ctx =>
                {
                    var item = await adoService!.UpdateWorkItemAsync(id, request);

                    if (item == null)
                    {
                        AnsiConsole.MarkupLine("[red]Failed to update work item[/]");
                        return;
                    }

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(item, JsonOptions));
                        return;
                    }

                    AnsiConsole.MarkupLine($"[green]Updated work item {item.Id}[/]");
                    DisplayWorkItemDetail(item);
                });
        }, idArg, stateOption, assignedOption, priorityOption, commentOption, jsonOption);

        return command;
    }

    private static Command CreateSprintsCommand(AzureDevOpsService? adoService)
    {
        var command = new Command("sprints", "List sprints/iterations");

        var currentOption = new Option<bool>(
            aliases: ["--current"],
            description: "Show only current sprint");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddOption(currentOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (current, json) =>
        {
            if (!EnsureConfigured(adoService)) return;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Fetching sprints...", async ctx =>
                {
                    var sprints = await adoService!.GetSprintsAsync();

                    if (current)
                    {
                        sprints = sprints.Where(s => s.IsCurrent).ToList();
                    }

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(sprints, JsonOptions));
                        return;
                    }

                    if (sprints.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[yellow]No sprints found[/]");
                        return;
                    }

                    DisplaySprints(sprints);
                });
        }, currentOption, jsonOption);

        return command;
    }

    private static Command CreateBoardsCommand(AzureDevOpsService? adoService)
    {
        var command = new Command("boards", "List boards");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddOption(jsonOption);

        command.SetHandler(async (json) =>
        {
            if (!EnsureConfigured(adoService)) return;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Fetching boards...", async ctx =>
                {
                    var boards = await adoService!.GetBoardsAsync();

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(boards, JsonOptions));
                        return;
                    }

                    if (boards.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[yellow]No boards found[/]");
                        return;
                    }

                    var table = new Table();
                    table.Border = TableBorder.Rounded;
                    table.AddColumn("Name");
                    table.AddColumn("ID");

                    foreach (var board in boards)
                    {
                        table.AddRow(board.Name, board.Id);
                    }

                    AnsiConsole.Write(table);
                });
        }, jsonOption);

        return command;
    }

    private static Command CreateFromErrorCommand(AzureDevOpsService? adoService, ReportMateService? reportMate)
    {
        var command = new Command("from-error", "Create work item from FleetMate error");

        var deviceArg = new Argument<string>(
            name: "device",
            description: "Device name or serial");

        var itemArg = new Argument<string>(
            name: "item",
            description: "Package/item name");

        var assignedOption = new Option<string?>(
            aliases: ["--assigned", "-a"],
            description: "Assign to user");

        var priorityOption = new Option<int>(
            aliases: ["--priority", "-p"],
            getDefaultValue: () => 2,
            description: "Priority (1-4, default: 2)");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddArgument(deviceArg);
        command.AddArgument(itemArg);
        command.AddOption(assignedOption);
        command.AddOption(priorityOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (device, item, assigned, priority, json) =>
        {
            if (!EnsureConfigured(adoService)) return;

            string errorMessage = "Error details not available";
            string deviceName = device;

            // Try to get error details from ReportMate
            if (reportMate != null)
            {
                var installs = await reportMate.GetDeviceInstallsAsync(device);
                var failedInstall = installs.FirstOrDefault(i =>
                    i.ItemName.Equals(item, StringComparison.OrdinalIgnoreCase) && i.IsError);

                if (failedInstall != null)
                {
                    deviceName = failedInstall.DeviceName ?? device;
                    errorMessage = failedInstall.LastError ?? failedInstall.CurrentStatus ?? "Unknown error";
                }

                var deviceInfo = await reportMate.FindDeviceAsync(device);
                if (deviceInfo != null)
                {
                    deviceName = deviceInfo.DisplayName;
                }
            }

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Creating work item from error...", async ctx =>
                {
                    var workItem = await adoService!.CreateFromErrorAsync(
                        deviceName, item, errorMessage, assigned, priority);

                    if (workItem == null)
                    {
                        AnsiConsole.MarkupLine("[red]Failed to create work item[/]");
                        return;
                    }

                    if (json)
                    {
                        Console.WriteLine(JsonSerializer.Serialize(workItem, JsonOptions));
                        return;
                    }

                    AnsiConsole.MarkupLine($"[green]Created work item {workItem.Id} for {item} failure on {deviceName}[/]");
                    DisplayWorkItemDetail(workItem);
                });
        }, deviceArg, itemArg, assignedOption, priorityOption, jsonOption);

        return command;
    }

    private static bool EnsureConfigured(AzureDevOpsService? ado)
    {
        if (ado != null) return true;

        AnsiConsole.MarkupLine("[red]Azure DevOps is not configured.[/]");
        AnsiConsole.MarkupLine("Add Azure DevOps configuration to your config file (~/.fleetmate/config.yaml):");
        AnsiConsole.MarkupLine("  [cyan]azureDevOps:[/]");
        AnsiConsole.MarkupLine("    [cyan]organization:[/] your-org");
        AnsiConsole.MarkupLine("    [cyan]project:[/] your-project");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("Then log in with: [cyan]az login[/]");
        return false;
    }

    private static void DisplayWorkItems(List<WorkItem> items)
    {
        var table = new Table();
        table.Border = TableBorder.Rounded;
        table.AddColumn("ID");
        table.AddColumn("Type");
        table.AddColumn("Title");
        table.AddColumn("State");
        table.AddColumn("Assigned To");
        table.AddColumn("Priority");

        foreach (var item in items)
        {
            var stateColor = item.State.ToLowerInvariant() switch
            {
                "new" => "blue",
                "active" or "in progress" => "yellow",
                "resolved" or "closed" or "done" => "green",
                _ => "dim"
            };

            var priorityColor = item.Priority switch
            {
                1 => "red",
                2 => "yellow",
                3 => "dim",
                4 => "dim",
                _ => "dim"
            };

            table.AddRow(
                item.Id.ToString(),
                item.WorkItemType,
                Markup.Escape(item.Title.Length > 50 ? item.Title[..47] + "..." : item.Title),
                $"[{stateColor}]{item.State}[/]",
                Markup.Escape(item.AssignedTo ?? "-"),
                $"[{priorityColor}]{item.Priority?.ToString() ?? "-"}[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]Showing {items.Count} work items[/]");
    }

    private static void DisplayWorkItemDetail(WorkItem item)
    {
        var panel = new Panel(
            new Rows(
                new Markup($"[bold]{Markup.Escape(item.Title)}[/]"),
                new Text(""),
                new Markup($"[dim]Type:[/] {item.WorkItemType}"),
                new Markup($"[dim]State:[/] {item.State}"),
                new Markup($"[dim]Priority:[/] {item.Priority?.ToString() ?? "-"}"),
                new Markup($"[dim]Assigned:[/] {Markup.Escape(item.AssignedTo ?? "Unassigned")}"),
                new Markup($"[dim]Iteration:[/] {Markup.Escape(item.IterationPath ?? "-")}"),
                new Markup($"[dim]Tags:[/] {(item.Tags.Count > 0 ? string.Join(", ", item.Tags) : "-")}"),
                new Text(""),
                new Markup($"[dim]Created:[/] {item.CreatedDate?.ToString("g") ?? "-"}"),
                new Markup($"[dim]Changed:[/] {item.ChangedDate?.ToString("g") ?? "-"}")
            ))
        {
            Header = new PanelHeader($" Work Item {item.Id} "),
            Border = BoxBorder.Rounded
        };

        AnsiConsole.Write(panel);

        if (!string.IsNullOrEmpty(item.Description))
        {
            AnsiConsole.Write(new Rule("[dim]Description[/]").LeftJustified());
            // Strip HTML tags for display
            var plainDesc = System.Text.RegularExpressions.Regex.Replace(item.Description, "<[^>]+>", " ");
            plainDesc = System.Text.RegularExpressions.Regex.Replace(plainDesc, @"\s+", " ").Trim();
            if (plainDesc.Length > 500)
            {
                plainDesc = plainDesc[..497] + "...";
            }
            Console.WriteLine(plainDesc);
        }
    }

    private static void DisplaySprints(List<Sprint> sprints)
    {
        var table = new Table();
        table.Border = TableBorder.Rounded;
        table.AddColumn("Name");
        table.AddColumn("Status");
        table.AddColumn("Start");
        table.AddColumn("End");

        foreach (var sprint in sprints.OrderBy(s => s.StartDate))
        {
            var status = sprint.Attributes?.TimeFrame ?? "unknown";
            var statusColor = status switch
            {
                "current" => "green",
                "future" => "blue",
                "past" => "dim",
                _ => "dim"
            };

            table.AddRow(
                sprint.Name,
                $"[{statusColor}]{status}[/]",
                sprint.StartDate?.ToString("yyyy-MM-dd") ?? "-",
                sprint.FinishDate?.ToString("yyyy-MM-dd") ?? "-");
        }

        AnsiConsole.Write(table);
    }
}
