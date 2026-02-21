using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace FleetMate.Commands.Projects;

/// <summary>
/// GitHub Projects v2 commands — board, list, items, and management.
/// Inspired by Backlog.md's terminal kanban + markdown export.
/// </summary>
public static class ProjectsCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static Command Create(FleetMateConfig config)
    {
        var command = new Command("projects", "GitHub Projects v2 board and management");

        command.AddCommand(CreateListCommand(config));
        command.AddCommand(CreateShowCommand(config));
        command.AddCommand(CreateItemsCommand(config));
        command.AddCommand(CreateBoardCommand(config));
        command.AddCommand(CreateAddCommand(config));
        command.AddCommand(CreateDraftCommand(config));
        command.AddCommand(CreateMoveCommand(config));
        command.AddCommand(CreateExportCommand(config));
        command.AddCommand(CreateFieldsCommand(config));

        return command;
    }

    // ──────────────────────────── List Projects ────────────────────────────

    private static Command CreateListCommand(FleetMateConfig config)
    {
        var command = new Command("list", "List GitHub Projects v2");

        var scopeOption = new Option<string?>(["--scope", "-s"], "Scope: org, user, repo (default: from config)");
        var ownerOption = new Option<string?>(["--owner", "-o"], "Owner (org or user login)");
        var repoOption = new Option<string?>(["--repo", "-r"], "Repository name (for repo scope)");
        var closedOption = new Option<bool>("--closed", "Include closed projects");
        var jsonOption = new Option<bool>("--json", "Output as JSON");

        command.AddOption(scopeOption);
        command.AddOption(ownerOption);
        command.AddOption(repoOption);
        command.AddOption(closedOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var service = await CreateService(config);
            if (service == null) return;

            var ghConfig = config.Tasks?.Providers?.GitHub;
            var scope = ParseScope(ctx.ParseResult.GetValueForOption(scopeOption) ?? ghConfig?.ProjectScope ?? "organization");
            var owner = ctx.ParseResult.GetValueForOption(ownerOption) ?? ghConfig?.Organization ?? ghConfig?.Owner ?? "";
            var repo = ctx.ParseResult.GetValueForOption(repoOption) ?? ghConfig?.Repo;
            var closed = ctx.ParseResult.GetValueForOption(closedOption);
            var json = ctx.ParseResult.GetValueForOption(jsonOption);

            var projects = await service.ListProjectsAsync(scope, owner, repo, closed);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(projects, JsonOptions));
                return;
            }

            if (projects.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No projects found.[/]");
                return;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("#")
                .AddColumn("Title")
                .AddColumn("Description")
                .AddColumn("Status")
                .AddColumn("Updated");

            foreach (var p in projects)
            {
                var status = p.Closed ? "[dim]Closed[/]" : (p.Public ? "[green]Public[/]" : "[blue]Private[/]");
                table.AddRow(
                    p.Number.ToString(),
                    Markup.Escape(p.Title),
                    Markup.Escape(p.ShortDescription ?? "").Length > 40
                        ? Markup.Escape(p.ShortDescription![..40]) + "..."
                        : Markup.Escape(p.ShortDescription ?? "-"),
                    status,
                    p.UpdatedAt.ToString("yyyy-MM-dd")
                );
            }

            AnsiConsole.Write(table);
        });

        return command;
    }

    // ──────────────────────────── Show Project ────────────────────────────

    private static Command CreateShowCommand(FleetMateConfig config)
    {
        var command = new Command("show", "Show project details");

        var numberArg = new Argument<int>("number", "Project number");
        var jsonOption = new Option<bool>("--json", "Output as JSON");

        command.AddArgument(numberArg);
        command.AddOption(jsonOption);

        command.SetHandler(async (number, json) =>
        {
            var service = await CreateService(config);
            if (service == null) return;

            var ghConfig = config.Tasks?.Providers?.GitHub;
            var scope = ParseScope(ghConfig?.ProjectScope ?? "organization");
            var owner = ghConfig?.Organization ?? ghConfig?.Owner ?? "";

            var project = await service.GetProjectAsync(scope, owner, number, ghConfig?.Repo);
            if (project == null)
            {
                AnsiConsole.MarkupLine($"[red]Project #{number} not found.[/]");
                return;
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(project, JsonOptions));
                return;
            }

            var fields = await service.ListProjectFieldsAsync(project.Id);
            var views = await service.ListProjectViewsAsync(project.Id);

            AnsiConsole.Write(new Panel(new Markup($"""
                [bold]Title:[/] {Markup.Escape(project.Title)}
                [bold]Number:[/] #{project.Number}
                [bold]Description:[/] {Markup.Escape(project.ShortDescription ?? "-")}
                [bold]URL:[/] {project.Url ?? "-"}
                [bold]Visibility:[/] {(project.Public ? "Public" : "Private")}
                [bold]Status:[/] {(project.Closed ? "Closed" : "Open")}
                [bold]Created:[/] {project.CreatedAt:yyyy-MM-dd}
                [bold]Updated:[/] {project.UpdatedAt:yyyy-MM-dd}
                
                [bold]Fields:[/] {string.Join(", ", fields.Select(f => $"{f.Name} ({f.DataType})"))}
                [bold]Views:[/] {string.Join(", ", views.Select(v => $"{v.Name} ({v.Layout})"))}
                """))
            {
                Header = new PanelHeader($"Project #{project.Number}")
            });

        }, numberArg, jsonOption);

        return command;
    }

    // ──────────────────────────── Items ────────────────────────────

    private static Command CreateItemsCommand(FleetMateConfig config)
    {
        var command = new Command("items", "List project items");

        var projectOption = new Option<int?>(["--project", "-p"], "Project number (default: from config)");
        var limitOption = new Option<int>(["--limit", "-n"], () => 50, "Max items");
        var archivedOption = new Option<bool>("--archived", "Include archived items");
        var jsonOption = new Option<bool>("--json", "Output as JSON");

        command.AddOption(projectOption);
        command.AddOption(limitOption);
        command.AddOption(archivedOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var service = await CreateService(config);
            if (service == null) return;

            var projectId = await ResolveProjectId(service, config,
                ctx.ParseResult.GetValueForOption(projectOption));
            if (projectId == null) return;

            var limit = ctx.ParseResult.GetValueForOption(limitOption);
            var archived = ctx.ParseResult.GetValueForOption(archivedOption);
            var json = ctx.ParseResult.GetValueForOption(jsonOption);

            var items = await service.ListProjectItemsAsync(projectId, limit, archived);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(items, JsonOptions));
                return;
            }

            if (items.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No items found.[/]");
                return;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Type")
                .AddColumn("Title")
                .AddColumn("Status")
                .AddColumn("Assignees")
                .AddColumn("Labels");

            foreach (var item in items)
            {
                var title = item.Content?.Title ?? item.DraftContent?.Title ?? "(redacted)";
                var status = item.FieldValues
                    .FirstOrDefault(fv => fv.FieldName.Equals("Status", StringComparison.OrdinalIgnoreCase))
                    ?.SingleSelectValue ?? "-";
                var assignees = item.Content?.Assignees.Count > 0
                    ? string.Join(", ", item.Content.Assignees.Take(3))
                    : "-";
                var labels = item.Content?.Labels.Count > 0
                    ? string.Join(", ", item.Content.Labels.Take(3))
                    : "-";

                var typeIcon = item.Type switch
                {
                    "ISSUE" => "[green]●[/]",
                    "PULL_REQUEST" => "[purple]⊙[/]",
                    "DRAFT_ISSUE" => "[dim]○[/]",
                    _ => "[dim]?[/]"
                };

                table.AddRow(typeIcon, Markup.Escape(title), status, assignees, labels);
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[dim]{items.Count} items[/]");
        });

        return command;
    }

    // ──────────────────────────── Board (Terminal Kanban) ────────────────────────────

    private static Command CreateBoardCommand(FleetMateConfig config)
    {
        var command = new Command("board", "Display terminal kanban board (Backlog.md-inspired)");

        var projectOption = new Option<int?>(["--project", "-p"], "Project number");
        var limitOption = new Option<int>(["--limit", "-n"], () => 100, "Max items");
        var compactOption = new Option<bool>(["--compact", "-c"], "Compact card style");
        var jsonOption = new Option<bool>("--json", "Output board data as JSON");

        command.AddOption(projectOption);
        command.AddOption(limitOption);
        command.AddOption(compactOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var service = await CreateService(config);
            if (service == null) return;

            var projectId = await ResolveProjectId(service, config,
                ctx.ParseResult.GetValueForOption(projectOption));
            if (projectId == null) return;

            var limit = ctx.ParseResult.GetValueForOption(limitOption);
            var compact = ctx.ParseResult.GetValueForOption(compactOption);
            var json = ctx.ParseResult.GetValueForOption(jsonOption);

            // Get status field to determine columns
            var statusField = await service.GetStatusFieldAsync(projectId);
            if (statusField == null)
            {
                AnsiConsole.MarkupLine("[yellow]No Status field found on this project.[/]");
                return;
            }

            var items = await service.ListProjectItemsAsync(projectId, limit);

            // Group items by status column
            var columns = new Dictionary<string, List<GitHubProjectItem>>();
            foreach (var opt in statusField.Options)
            {
                columns[opt.Name] = new List<GitHubProjectItem>();
            }
            columns["(No Status)"] = new List<GitHubProjectItem>();

            foreach (var item in items)
            {
                var statusValue = item.FieldValues
                    .FirstOrDefault(fv => fv.FieldName.Equals("Status", StringComparison.OrdinalIgnoreCase))
                    ?.SingleSelectValue;

                if (statusValue != null && columns.ContainsKey(statusValue))
                    columns[statusValue].Add(item);
                else
                    columns["(No Status)"].Add(item);
            }

            // Remove empty "(No Status)" column
            if (columns["(No Status)"].Count == 0)
                columns.Remove("(No Status)");

            if (json)
            {
                var boardData = columns.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.Select(i => new
                    {
                        id = i.Id,
                        type = i.Type,
                        title = i.Content?.Title ?? i.DraftContent?.Title ?? "",
                        assignees = i.Content?.Assignees ?? new(),
                        labels = i.Content?.Labels ?? new()
                    }));
                Console.WriteLine(JsonSerializer.Serialize(boardData, JsonOptions));
                return;
            }

            // Render terminal kanban board
            RenderKanbanBoard(columns, statusField, compact);
        });

        return command;
    }

    private static void RenderKanbanBoard(
        Dictionary<string, List<GitHubProjectItem>> columns,
        GitHubProjectField statusField, bool compact)
    {
        var columnList = columns.ToList();
        var maxItems = columnList.Max(c => c.Value.Count);

        // Color map for status columns
        var colorMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["todo"] = "white",
            ["backlog"] = "grey",
            ["in progress"] = "blue",
            ["in review"] = "purple",
            ["done"] = "green",
            ["closed"] = "dim"
        };

        string GetColumnColor(string name)
        {
            foreach (var (key, color) in colorMap)
                if (name.Contains(key, StringComparison.OrdinalIgnoreCase)) return color;
            return "yellow";
        }

        // Build the board using Spectre.Console columns
        var grid = new Grid();
        foreach (var _ in columnList)
            grid.AddColumn(new GridColumn().PadRight(2));

        // Header row
        var headers = columnList.Select(c =>
        {
            var color = GetColumnColor(c.Key);
            var count = c.Value.Count;
            return new Markup($"[bold {color}]{Markup.Escape(c.Key)}[/] [dim]({count})[/]");
        }).ToArray();
        grid.AddRow(headers.Select(h => (IRenderable)h).ToArray());

        // Separator
        grid.AddRow(columnList.Select(_ => (IRenderable)new Markup("[dim]─────────────────────[/]")).ToArray());

        // Card rows
        for (int i = 0; i < maxItems; i++)
        {
            var row = columnList.Select(c =>
            {
                if (i >= c.Value.Count)
                    return (IRenderable)new Markup("");

                var item = c.Value[i];
                return (IRenderable)RenderCard(item, compact);
            }).ToArray();
            grid.AddRow(row);
        }

        AnsiConsole.Write(grid);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]Total: {columns.Values.Sum(c => c.Count)} items across {columns.Count} columns[/]");
    }

    private static IRenderable RenderCard(GitHubProjectItem item, bool compact)
    {
        var title = item.Content?.Title ?? item.DraftContent?.Title ?? "(untitled)";
        var truncatedTitle = title.Length > 28 ? title[..28] + "…" : title;

        var typeIcon = item.Type switch
        {
            "ISSUE" => "[green]●[/]",
            "PULL_REQUEST" => "[purple]⊙[/]",
            "DRAFT_ISSUE" => "[dim]○[/]",
            _ => " "
        };

        if (compact)
        {
            return new Markup($"{typeIcon} {Markup.Escape(truncatedTitle)}");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{typeIcon} [bold]{Markup.Escape(truncatedTitle)}[/]");

        if (item.Content?.Assignees.Count > 0)
        {
            var assignees = string.Join(", ", item.Content.Assignees.Take(2));
            sb.AppendLine($"  [dim]@{assignees}[/]");
        }

        if (item.Content?.Labels.Count > 0)
        {
            var labels = string.Join(" ", item.Content.Labels.Take(3).Select(l => $"[cyan][{Markup.Escape(l)}][/]"));
            sb.AppendLine($"  {labels}");
        }

        return new Markup(sb.ToString().TrimEnd());
    }

    // ──────────────────────────── Add Item ────────────────────────────

    private static Command CreateAddCommand(FleetMateConfig config)
    {
        var command = new Command("add", "Add an issue or PR to a project");

        var contentIdArg = new Argument<string>("content-id", "Issue/PR node ID");
        var projectOption = new Option<int?>(["--project", "-p"], "Project number");

        command.AddArgument(contentIdArg);
        command.AddOption(projectOption);

        command.SetHandler(async (contentId, projectNum) =>
        {
            var service = await CreateService(config);
            if (service == null) return;

            var projectId = await ResolveProjectId(service, config, projectNum);
            if (projectId == null) return;

            var itemId = await service.AddItemToProjectAsync(projectId, contentId);
            AnsiConsole.MarkupLine($"[green]Added item to project:[/] {itemId}");

        }, contentIdArg, projectOption);

        return command;
    }

    // ──────────────────────────── Create Draft ────────────────────────────

    private static Command CreateDraftCommand(FleetMateConfig config)
    {
        var command = new Command("create-draft", "Create a draft issue in a project");

        var titleOption = new Option<string>(["--title", "-t"], "Draft title") { IsRequired = true };
        var bodyOption = new Option<string?>(["--body", "-b"], "Draft body");
        var statusOption = new Option<string?>(["--status", "-s"], "Initial status column");
        var projectOption = new Option<int?>(["--project", "-p"], "Project number");

        command.AddOption(titleOption);
        command.AddOption(bodyOption);
        command.AddOption(statusOption);
        command.AddOption(projectOption);

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var service = await CreateService(config);
            if (service == null) return;

            var projectId = await ResolveProjectId(service, config,
                ctx.ParseResult.GetValueForOption(projectOption));
            if (projectId == null) return;

            var title = ctx.ParseResult.GetValueForOption(titleOption)!;
            var body = ctx.ParseResult.GetValueForOption(bodyOption);
            var status = ctx.ParseResult.GetValueForOption(statusOption);

            var itemId = await service.AddDraftItemAsync(projectId, title, body);

            // Move to status if specified
            if (!string.IsNullOrEmpty(status))
            {
                var statusField = await service.GetStatusFieldAsync(projectId);
                if (statusField != null)
                {
                    var option = statusField.Options.FirstOrDefault(o =>
                        o.Name.Equals(status, StringComparison.OrdinalIgnoreCase));
                    if (option != null)
                        await service.MoveItemToStatusAsync(projectId, itemId, statusField.Id, option.Id);
                }
            }

            AnsiConsole.MarkupLine($"[green]Created draft:[/] {Markup.Escape(title)} ({itemId})");
        });

        return command;
    }

    // ──────────────────────────── Move Item ────────────────────────────

    private static Command CreateMoveCommand(FleetMateConfig config)
    {
        var command = new Command("move", "Move an item to a different status column");

        var itemIdArg = new Argument<string>("item-id", "Project item ID");
        var statusArg = new Argument<string>("status", "Target status column name");
        var projectOption = new Option<int?>(["--project", "-p"], "Project number");

        command.AddArgument(itemIdArg);
        command.AddArgument(statusArg);
        command.AddOption(projectOption);

        command.SetHandler(async (itemId, status, projectNum) =>
        {
            var service = await CreateService(config);
            if (service == null) return;

            var projectId = await ResolveProjectId(service, config, projectNum);
            if (projectId == null) return;

            var statusField = await service.GetStatusFieldAsync(projectId);
            if (statusField == null)
            {
                AnsiConsole.MarkupLine("[red]No Status field found.[/]");
                return;
            }

            var option = statusField.Options.FirstOrDefault(o =>
                o.Name.Equals(status, StringComparison.OrdinalIgnoreCase));
            if (option == null)
            {
                AnsiConsole.MarkupLine($"[red]Status '{status}' not found.[/] Available: {string.Join(", ", statusField.Options.Select(o => o.Name))}");
                return;
            }

            await service.MoveItemToStatusAsync(projectId, itemId, statusField.Id, option.Id);
            AnsiConsole.MarkupLine($"[green]Moved item to {option.Name}[/]");

        }, itemIdArg, statusArg, projectOption);

        return command;
    }

    // ──────────────────────────── Export to Markdown ────────────────────────────

    private static Command CreateExportCommand(FleetMateConfig config)
    {
        var command = new Command("export", "Export project board to markdown");

        var projectOption = new Option<int?>(["--project", "-p"], "Project number");
        var outputOption = new Option<string?>(["--output", "-o"], "Output file path (default: stdout)");

        command.AddOption(projectOption);
        command.AddOption(outputOption);

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var service = await CreateService(config);
            if (service == null) return;

            var projectId = await ResolveProjectId(service, config,
                ctx.ParseResult.GetValueForOption(projectOption));
            if (projectId == null) return;

            var output = ctx.ParseResult.GetValueForOption(outputOption);

            var statusField = await service.GetStatusFieldAsync(projectId);
            var items = await service.ListProjectItemsAsync(projectId, 200);

            var md = new StringBuilder();
            md.AppendLine("# Project Board");
            md.AppendLine();
            md.AppendLine($"*Exported {DateTime.Now:yyyy-MM-dd HH:mm}*");
            md.AppendLine();

            if (statusField != null)
            {
                // Group by status
                var grouped = new Dictionary<string, List<GitHubProjectItem>>();
                foreach (var opt in statusField.Options)
                    grouped[opt.Name] = new List<GitHubProjectItem>();
                grouped["(No Status)"] = new List<GitHubProjectItem>();

                foreach (var item in items)
                {
                    var sv = item.FieldValues
                        .FirstOrDefault(fv => fv.FieldName.Equals("Status", StringComparison.OrdinalIgnoreCase))
                        ?.SingleSelectValue;
                    if (sv != null && grouped.ContainsKey(sv))
                        grouped[sv].Add(item);
                    else
                        grouped["(No Status)"].Add(item);
                }

                foreach (var (column, columnItems) in grouped)
                {
                    if (columnItems.Count == 0 && column == "(No Status)") continue;

                    md.AppendLine($"## {column} ({columnItems.Count})");
                    md.AppendLine();

                    foreach (var item in columnItems)
                    {
                        var title = item.Content?.Title ?? item.DraftContent?.Title ?? "(untitled)";
                        var typeLabel = item.Type switch
                        {
                            "ISSUE" => "Issue",
                            "PULL_REQUEST" => "PR",
                            "DRAFT_ISSUE" => "Draft",
                            _ => "?"
                        };

                        var parts = new List<string> { $"- **[{typeLabel}]** {title}" };

                        if (item.Content?.Assignees.Count > 0)
                            parts.Add($"  - Assignees: {string.Join(", ", item.Content.Assignees.Select(a => $"@{a}"))}");
                        if (item.Content?.Labels.Count > 0)
                            parts.Add($"  - Labels: {string.Join(", ", item.Content.Labels.Select(l => $"`{l}`"))}");
                        if (item.Content?.Url != null)
                            parts.Add($"  - {item.Content.Url}");

                        md.AppendLine(string.Join(Environment.NewLine, parts));
                    }
                    md.AppendLine();
                }
            }
            else
            {
                // Flat list
                foreach (var item in items)
                {
                    var title = item.Content?.Title ?? item.DraftContent?.Title ?? "(untitled)";
                    md.AppendLine($"- {title}");
                }
            }

            var markdown = md.ToString();

            if (!string.IsNullOrEmpty(output))
            {
                await File.WriteAllTextAsync(output, markdown);
                AnsiConsole.MarkupLine($"[green]Exported to {output}[/]");
            }
            else
            {
                Console.Write(markdown);
            }
        });

        return command;
    }

    // ──────────────────────────── Fields ────────────────────────────

    private static Command CreateFieldsCommand(FleetMateConfig config)
    {
        var command = new Command("fields", "List project fields and their options");

        var projectOption = new Option<int?>(["--project", "-p"], "Project number");
        var jsonOption = new Option<bool>("--json", "Output as JSON");

        command.AddOption(projectOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var service = await CreateService(config);
            if (service == null) return;

            var projectId = await ResolveProjectId(service, config,
                ctx.ParseResult.GetValueForOption(projectOption));
            if (projectId == null) return;

            var json = ctx.ParseResult.GetValueForOption(jsonOption);
            var fields = await service.ListProjectFieldsAsync(projectId);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(fields, JsonOptions));
                return;
            }

            foreach (var field in fields)
            {
                AnsiConsole.MarkupLine($"[bold]{Markup.Escape(field.Name)}[/] [dim]({field.DataType})[/]");
                foreach (var opt in field.Options)
                {
                    var color = !string.IsNullOrEmpty(opt.Color) ? $"[{opt.Color}]●[/] " : "  ";
                    AnsiConsole.MarkupLine($"  {color}{Markup.Escape(opt.Name)} [dim]({opt.Id})[/]");
                }
                foreach (var iter in field.Iterations)
                {
                    AnsiConsole.MarkupLine($"  ⟳ {Markup.Escape(iter.Title)} [dim]{iter.StartDate}[/]");
                }
            }
        });

        return command;
    }

    // ──────────────────────────── Helpers ────────────────────────────

    private static async Task<GitHubProjectsService?> CreateService(FleetMateConfig config)
    {
        var ghConfig = config.Tasks?.Providers?.GitHub;
        if (ghConfig == null || !ghConfig.Enabled)
        {
            AnsiConsole.MarkupLine("[yellow]GitHub provider is not configured or enabled.[/]");
            AnsiConsole.MarkupLine("[dim]Configure tasks.providers.github in your config.yaml[/]");
            return null;
        }

        var service = new GitHubProjectsService(ghConfig);
        if (!await service.AuthenticateAsync())
        {
            AnsiConsole.MarkupLine("[red]Failed to authenticate with GitHub.[/]");
            return null;
        }
        return service;
    }

    private static async Task<string?> ResolveProjectId(
        GitHubProjectsService service, FleetMateConfig config, int? projectNumber)
    {
        var ghConfig = config.Tasks?.Providers?.GitHub;
        var scope = ParseScope(ghConfig?.ProjectScope ?? "organization");
        var owner = ghConfig?.Organization ?? ghConfig?.Owner ?? "";
        var num = projectNumber ?? ghConfig?.ProjectNumber;

        if (num.HasValue)
        {
            var project = await service.GetProjectAsync(scope, owner, num.Value, ghConfig?.Repo);
            if (project == null)
            {
                AnsiConsole.MarkupLine($"[red]Project #{num} not found.[/]");
                return null;
            }
            return project.Id;
        }

        // Use first project
        var projects = await service.ListProjectsAsync(scope, owner, ghConfig?.Repo, limit: 1);
        if (!projects.Any())
        {
            AnsiConsole.MarkupLine("[red]No projects found. Specify --project or configure project_number.[/]");
            return null;
        }

        return projects.First().Id;
    }

    private static ProjectScope ParseScope(string scope) => scope.ToLowerInvariant() switch
    {
        "user" => ProjectScope.User,
        "repository" or "repo" => ProjectScope.Repository,
        _ => ProjectScope.Organization
    };
}
