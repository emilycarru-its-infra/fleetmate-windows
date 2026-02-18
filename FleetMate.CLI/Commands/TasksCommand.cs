using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using FleetMate.Config;
using FleetMate.Core.Models.Tasks;
using FleetMate.Core.Services.Tasks;
using Spectre.Console;

namespace FleetMate.Commands;

/// <summary>
/// Unified task management commands across all configured providers
/// </summary>
public static class TasksCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static Command Create(FleetMateConfig config)
    {
        var command = new Command("tasks", "Unified task management across all providers");

        command.AddCommand(CreateListCommand(config));
        command.AddCommand(CreateShowCommand(config));
        command.AddCommand(CreateCreateCommand(config));
        command.AddCommand(CreateUpdateCommand(config));
        command.AddCommand(CreateBucketsCommand(config));
        command.AddCommand(CreateLabelsCommand(config));
        command.AddCommand(CreateSyncCommand(config));
        command.AddCommand(CreateProvidersCommand(config));

        return command;
    }

    private static Command CreateListCommand(FleetMateConfig config)
    {
        var command = new Command("list", "List tasks from all providers");

        var providerOption = new Option<string?>(
            aliases: ["--provider", "-p"],
            description: "Filter by provider (github, gitea, azdo)");

        var stateOption = new Option<string?>(
            aliases: ["--state", "-s"],
            description: "Filter by state (open, in-progress, closed)");

        var labelOption = new Option<string[]>(
            aliases: ["--label", "-l"],
            description: "Filter by label(s)") { AllowMultipleArgumentsPerToken = true };

        var bucketOption = new Option<string?>(
            aliases: ["--bucket", "-b"],
            description: "Filter by bucket/milestone");

        var assigneeOption = new Option<string?>(
            aliases: ["--assignee", "-a"],
            description: "Filter by assignee");

        var searchOption = new Option<string?>(
            aliases: ["--search", "-q"],
            description: "Search in title/description");

        var limitOption = new Option<int>(
            aliases: ["--limit", "-n"],
            getDefaultValue: () => 50,
            description: "Maximum results per provider");

        var closedOption = new Option<bool>(
            aliases: ["--closed"],
            description: "Include closed tasks");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddOption(providerOption);
        command.AddOption(stateOption);
        command.AddOption(labelOption);
        command.AddOption(bucketOption);
        command.AddOption(assigneeOption);
        command.AddOption(searchOption);
        command.AddOption(limitOption);
        command.AddOption(closedOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (InvocationContext ctx) =>
        {
            var provider = ctx.ParseResult.GetValueForOption(providerOption);
            var state = ctx.ParseResult.GetValueForOption(stateOption);
            var labels = ctx.ParseResult.GetValueForOption(labelOption);
            var bucket = ctx.ParseResult.GetValueForOption(bucketOption);
            var assignee = ctx.ParseResult.GetValueForOption(assigneeOption);
            var search = ctx.ParseResult.GetValueForOption(searchOption);
            var limit = ctx.ParseResult.GetValueForOption(limitOption);
            var includeClosed = ctx.ParseResult.GetValueForOption(closedOption);
            var json = ctx.ParseResult.GetValueForOption(jsonOption);

            var registry = await CreateRegistry(config);
            
            if (!registry.HasEnabledProviders)
            {
                AnsiConsole.MarkupLine("[yellow]No task providers are configured and enabled.[/]");
                return;
            }

            var filter = new TaskFilter
            {
                SearchText = search,
                Bucket = bucket,
                Limit = limit,
                IncludeClosed = includeClosed
            };

            if (!string.IsNullOrEmpty(state))
            {
                filter.States = ParseStates(state);
            }
            if (labels?.Length > 0)
            {
                filter.Labels = labels.ToList();
            }
            if (!string.IsNullOrEmpty(assignee))
            {
                filter.Assignees = [assignee];
            }

            List<UnifiedTask> tasks;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Fetching tasks...", async _ =>
                {
                    if (!string.IsNullOrEmpty(provider))
                    {
                        tasks = await registry.ListTasksAsync(provider, filter);
                    }
                    else
                    {
                        tasks = await registry.ListAllTasksAsync(filter);
                    }
                });

            // Re-fetch for display (status context ended)
            tasks = !string.IsNullOrEmpty(provider)
                ? await registry.ListTasksAsync(provider, filter)
                : await registry.ListAllTasksAsync(filter);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(tasks, JsonOptions));
                return;
            }

            if (tasks.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No tasks found.[/]");
                return;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Provider")
                .AddColumn("ID")
                .AddColumn("Title")
                .AddColumn("State")
                .AddColumn("Bucket")
                .AddColumn("Labels");

            foreach (var task in tasks.OrderBy(t => t.State).ThenBy(t => t.Provider))
            {
                var stateColor = task.State switch
                {
                    TaskState.Open => "green",
                    TaskState.InProgress => "blue",
                    TaskState.Closed => "dim",
                    _ => "white"
                };

                table.AddRow(
                    $"[cyan]{task.Provider}[/]",
                    task.Id,
                    task.Title.Length > 50 ? task.Title[..50] + "..." : task.Title,
                    $"[{stateColor}]{task.State}[/]",
                    task.Bucket ?? "-",
                    task.Labels?.Count > 0 ? string.Join(", ", task.Labels.Take(3)) : "-"
                );
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[dim]Total: {tasks.Count} tasks[/]");

        });

        return command;
    }

    private static Command CreateShowCommand(FleetMateConfig config)
    {
        var command = new Command("show", "Show task details");

        var providerArg = new Argument<string>("provider", "Provider (github, gitea, azdo)");
        var idArg = new Argument<string>("id", "Task ID");
        var jsonOption = new Option<bool>("--json", "Output as JSON");

        command.AddArgument(providerArg);
        command.AddArgument(idArg);
        command.AddOption(jsonOption);

        command.SetHandler(async (provider, id, json) =>
        {
            var registry = await CreateRegistry(config);
            var task = await registry.GetTaskAsync(provider, id);

            if (task == null)
            {
                AnsiConsole.MarkupLine($"[red]Task {provider}#{id} not found[/]");
                return;
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(task, JsonOptions));
                return;
            }

            var panel = new Panel(new Markup($"""
                [bold]Title:[/] {Markup.Escape(task.Title)}
                [bold]State:[/] {task.State}
                [bold]Provider:[/] {task.Provider}
                [bold]ID:[/] {task.Id}
                [bold]Bucket:[/] {task.Bucket ?? "-"}
                [bold]Assignees:[/] {(task.Assignees?.Count > 0 ? string.Join(", ", task.Assignees) : "-")}
                [bold]Labels:[/] {(task.Labels?.Count > 0 ? string.Join(", ", task.Labels) : "-")}
                [bold]Priority:[/] {task.Priority?.ToString() ?? "-"}
                [bold]Due:[/] {task.DueDate?.ToString("yyyy-MM-dd") ?? "-"}
                [bold]Created:[/] {task.CreatedAt.ToString("g")}
                [bold]Updated:[/] {task.UpdatedAt.ToString("g")}
                [bold]URL:[/] {task.ExternalUrl ?? "-"}
                
                [bold]Description:[/]
                {Markup.Escape(task.Description ?? "(no description)")}
                """))
            {
                Header = new PanelHeader($"{task.Provider}#{task.Id}")
            };

            AnsiConsole.Write(panel);

        }, providerArg, idArg, jsonOption);

        return command;
    }

    private static Command CreateCreateCommand(FleetMateConfig config)
    {
        var command = new Command("create", "Create a new task");

        var providerArg = new Argument<string>("provider", "Provider (github, gitea, azdo)");
        var titleOption = new Option<string>(["--title", "-t"], "Task title") { IsRequired = true };
        var descOption = new Option<string?>(["--description", "-d"], "Task description");
        var labelOption = new Option<string[]>(["--label", "-l"], "Labels") { AllowMultipleArgumentsPerToken = true };
        var assigneeOption = new Option<string[]>(["--assignee", "-a"], "Assignees") { AllowMultipleArgumentsPerToken = true };
        var bucketOption = new Option<string?>(["--bucket", "-b"], "Bucket/milestone");
        var priorityOption = new Option<int?>(["--priority", "-p"], "Priority (1=highest, 4=lowest)");

        command.AddArgument(providerArg);
        command.AddOption(titleOption);
        command.AddOption(descOption);
        command.AddOption(labelOption);
        command.AddOption(assigneeOption);
        command.AddOption(bucketOption);
        command.AddOption(priorityOption);

        command.SetHandler(async (provider, title, desc, labels, assignees, bucket, priority) =>
        {
            var registry = await CreateRegistry(config);

            var request = new CreateTaskRequest
            {
                Title = title,
                Description = desc,
                Labels = labels?.ToList(),
                Assignees = assignees?.ToList(),
                Bucket = bucket,
                Priority = priority
            };

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Creating task...", async _ =>
                {
                    var task = await registry.CreateTaskAsync(provider, request);
                    AnsiConsole.MarkupLine($"[green]Created task {task.Provider}#{task.Id}:[/] {task.Title}");
                    if (!string.IsNullOrEmpty(task.ExternalUrl))
                    {
                        AnsiConsole.MarkupLine($"[dim]{task.ExternalUrl}[/]");
                    }
                });

        }, providerArg, titleOption, descOption, labelOption, assigneeOption, bucketOption, priorityOption);

        return command;
    }

    private static Command CreateUpdateCommand(FleetMateConfig config)
    {
        var command = new Command("update", "Update an existing task");

        var providerArg = new Argument<string>("provider", "Provider");
        var idArg = new Argument<string>("id", "Task ID");
        var titleOption = new Option<string?>(["--title", "-t"], "New title");
        var stateOption = new Option<string?>(["--state", "-s"], "New state (open, in-progress, closed)");
        var labelOption = new Option<string[]?>(["--label", "-l"], "Replace labels");
        var assigneeOption = new Option<string[]?>(["--assignee", "-a"], "Replace assignees");

        command.AddArgument(providerArg);
        command.AddArgument(idArg);
        command.AddOption(titleOption);
        command.AddOption(stateOption);
        command.AddOption(labelOption);
        command.AddOption(assigneeOption);

        command.SetHandler(async (provider, id, title, state, labels, assignees) =>
        {
            var registry = await CreateRegistry(config);

            var request = new UpdateTaskRequest
            {
                Title = title,
                Labels = labels?.ToList(),
                Assignees = assignees?.ToList()
            };

            if (!string.IsNullOrEmpty(state))
            {
                request.State = state.ToLower() switch
                {
                    "closed" => TaskState.Closed,
                    "in-progress" or "inprogress" or "active" => TaskState.InProgress,
                    _ => TaskState.Open
                };
            }

            var task = await registry.UpdateTaskAsync(provider, id, request);
            AnsiConsole.MarkupLine($"[green]Updated[/] {task.Provider}#{task.Id}: {task.Title}");

        }, providerArg, idArg, titleOption, stateOption, labelOption, assigneeOption);

        return command;
    }

    private static Command CreateBucketsCommand(FleetMateConfig config)
    {
        var command = new Command("buckets", "List available buckets/milestones");

        var providerOption = new Option<string?>(["--provider", "-p"], "Filter by provider");
        var jsonOption = new Option<bool>("--json", "Output as JSON");

        command.AddOption(providerOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (provider, json) =>
        {
            var registry = await CreateRegistry(config);
            
            var allBuckets = new Dictionary<string, List<TaskBucket>>();

            foreach (var p in registry.GetProviders())
            {
                if (!string.IsNullOrEmpty(provider) && p.ProviderId != provider) continue;
                
                var buckets = await p.ListBucketsAsync();
                allBuckets[p.ProviderId] = buckets;
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(allBuckets, JsonOptions));
                return;
            }

            foreach (var (providerId, buckets) in allBuckets)
            {
                AnsiConsole.MarkupLine($"[bold cyan]{providerId}[/]");
                foreach (var bucket in buckets)
                {
                    AnsiConsole.MarkupLine($"  {bucket.Name} [dim]({bucket.Id})[/]");
                }
            }

        }, providerOption, jsonOption);

        return command;
    }

    private static Command CreateLabelsCommand(FleetMateConfig config)
    {
        var command = new Command("labels", "List available labels");

        var providerOption = new Option<string?>(["--provider", "-p"], "Filter by provider");
        var jsonOption = new Option<bool>("--json", "Output as JSON");

        command.AddOption(providerOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (provider, json) =>
        {
            var registry = await CreateRegistry(config);
            
            var allLabels = new Dictionary<string, List<TaskLabel>>();

            foreach (var p in registry.GetProviders())
            {
                if (!string.IsNullOrEmpty(provider) && p.ProviderId != provider) continue;
                
                var labels = await p.ListLabelsAsync();
                allLabels[p.ProviderId] = labels;
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(allLabels, JsonOptions));
                return;
            }

            foreach (var (providerId, labels) in allLabels)
            {
                AnsiConsole.MarkupLine($"[bold cyan]{providerId}[/]");
                foreach (var label in labels)
                {
                    var colorStyle = !string.IsNullOrEmpty(label.Color) ? $"[{label.Color}]●[/] " : "";
                    AnsiConsole.MarkupLine($"  {colorStyle}{label.Name}");
                }
            }

        }, providerOption, jsonOption);

        return command;
    }

    private static Command CreateSyncCommand(FleetMateConfig config)
    {
        var command = new Command("sync", "Sync tasks to external destinations");

        var plannerOption = new Option<bool>("--planner", "Sync to Microsoft Planner");
        var markdownOption = new Option<bool>("--markdown", "Sync to/from markdown file");
        var dryRunOption = new Option<bool>("--dry-run", "Show what would be synced without making changes");

        command.AddOption(plannerOption);
        command.AddOption(markdownOption);
        command.AddOption(dryRunOption);

        command.SetHandler(async (planner, markdown, dryRun) =>
        {
            var registry = await CreateRegistry(config);
            var tasks = await registry.ListAllTasksAsync(new TaskFilter { IncludeClosed = false });

            if (dryRun)
            {
                AnsiConsole.MarkupLine($"[yellow]Dry run:[/] Would sync {tasks.Count} tasks");
                foreach (var task in tasks.Take(10))
                {
                    AnsiConsole.MarkupLine($"  - {task.Provider}#{task.Id}: {task.Title}");
                }
                if (tasks.Count > 10)
                {
                    AnsiConsole.MarkupLine($"  [dim]... and {tasks.Count - 10} more[/]");
                }
                return;
            }

            if (planner)
            {
                var plannerService = new PlannerSyncService(config);
                if (!plannerService.IsEnabled)
                {
                    AnsiConsole.MarkupLine("[yellow]Planner sync not configured[/]");
                }
                else if (await plannerService.AuthenticateAsync())
                {
                    var result = await plannerService.SyncTasksAsync(tasks);
                    AnsiConsole.MarkupLine(result.Success 
                        ? $"[green]{result.Message}[/]" 
                        : $"[red]{result.Message}[/]");
                }
            }

            if (markdown)
            {
                var mdService = new MarkdownSyncService(config);
                if (!mdService.IsEnabled)
                {
                    AnsiConsole.MarkupLine("[yellow]Markdown sync not configured[/]");
                }
                else
                {
                    var result = await mdService.SyncBidirectionalAsync(tasks);
                    AnsiConsole.MarkupLine(result.Success 
                        ? $"[green]{result.Message}[/]" 
                        : $"[red]{result.Message}[/]");
                }
            }

            if (!planner && !markdown)
            {
                AnsiConsole.MarkupLine("[yellow]Specify --planner or --markdown to sync[/]");
            }

        }, plannerOption, markdownOption, dryRunOption);

        return command;
    }

    private static Command CreateProvidersCommand(FleetMateConfig config)
    {
        var command = new Command("providers", "List configured task providers");

        command.SetHandler(async () =>
        {
            var registry = await CreateRegistry(config);

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Provider")
                .AddColumn("Name")
                .AddColumn("Status")
                .AddColumn("Authenticated");

            foreach (var provider in registry.GetProviders())
            {
                var status = provider.IsEnabled ? "[green]Enabled[/]" : "[dim]Disabled[/]";
                var auth = provider.IsEnabled 
                    ? (await provider.AuthenticateAsync() ? "[green]Yes[/]" : "[red]No[/]")
                    : "[dim]-[/]";

                table.AddRow(provider.ProviderId, provider.ProviderName, status, auth);
            }

            AnsiConsole.Write(table);
        });

        return command;
    }

    private static async Task<TaskProviderRegistry> CreateRegistry(FleetMateConfig config)
    {
        var registry = new TaskProviderRegistry();

        // Register all providers
        var azdo = new AzureDevOpsTaskProvider(config);
        var github = new GitHubTaskProvider(config);
        var gitea = new GiteaTaskProvider(config);

        registry.RegisterProvider(azdo);
        registry.RegisterProvider(github);
        registry.RegisterProvider(gitea);

        // Authenticate enabled providers
        foreach (var provider in registry.GetProviders().Where(p => p.IsEnabled))
        {
            await provider.AuthenticateAsync();
        }

        return registry;
    }

    private static List<TaskState>? ParseStates(string state)
    {
        return state.ToLower() switch
        {
            "open" => [TaskState.Open],
            "in-progress" or "inprogress" or "active" => [TaskState.InProgress],
            "closed" or "done" or "resolved" => [TaskState.Closed],
            "all" => [TaskState.Open, TaskState.InProgress, TaskState.Closed],
            _ => null
        };
    }
}
