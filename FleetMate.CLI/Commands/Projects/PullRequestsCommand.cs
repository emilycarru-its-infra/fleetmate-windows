using System.CommandLine;
using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Spectre.Console;

namespace FleetMate.Commands.Projects;

/// <summary>
/// <c>fleetmate prs</c> — the signed-in user's pull request queue across Azure
/// DevOps and GitHub, in the two sections the Azure DevOps web queue uses.
/// </summary>
public static class PullRequestsCommand
{
    public static Command Create(FleetMateConfig config)
    {
        var command = new Command("prs", "Show my pull requests across Azure DevOps and GitHub");

        var source = new Option<string>("--source",
            () => "all",
            "Which provider to query: all, devops, github");

        var status = new Option<string>("--status",
            () => "active",
            "Azure DevOps PR status: active, completed, abandoned, all");

        var json = new Option<bool>(new[] { "--json", "-j" }, "Output as JSON");

        command.AddOption(source);
        command.AddOption(status);
        command.AddOption(json);

        command.SetHandler(
            async (string src, string st, bool asJson) => await ExecuteAsync(config, src, st, asJson),
            source, status, json);

        return command;
    }

    private static async Task ExecuteAsync(
        FleetMateConfig config, string source, string status, bool asJson)
    {
        var wantDevOps = source is "all" or "devops";
        var wantGitHub = source is "all" or "github";

        var queue = new PullRequestQueue();

        // Both providers are queried concurrently and neither can sink the other:
        // each returns its failure as an entry in Errors rather than throwing.
        var tasks = new List<Task<PullRequestQueue>>();

        if (wantDevOps && !string.IsNullOrWhiteSpace(config.AzureDevOps?.Organization))
        {
            tasks.Add(Task.Run(async () =>
            {
                using var devops = new AzureDevOpsService(config.AzureDevOps!);
                return await devops.GetMyPullRequestsAsync(status);
            }));
        }

        if (wantGitHub && config.Tasks?.Providers?.GitHub is { } gh)
        {
            tasks.Add(Task.Run(async () =>
            {
                using var github = new GitHubPullRequestService(gh);
                return await github.GetMyPullRequestsAsync();
            }));
        }

        if (tasks.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No pull request providers are configured.[/]");
            return;
        }

        foreach (var result in await Task.WhenAll(tasks)) queue.Merge(result);

        if (asJson) PrintJson(queue);
        else PrintQueue(queue);
    }

    private static void PrintQueue(PullRequestQueue queue)
    {
        foreach (var relation in new[] { PullRequestRelation.CreatedByMe, PullRequestRelation.AssignedToMe })
        {
            var section = queue.Section(relation);
            if (section.Count == 0) continue;

            var table = new Table { Border = TableBorder.Rounded };
            table.Title = new TableTitle($"[cyan]{relation.SectionTitle()}[/] [dim]({section.Count})[/]");
            table.AddColumn("");
            table.AddColumn("PR");
            table.AddColumn("Title");
            table.AddColumn("Repository");
            table.AddColumn("Into");
            table.AddColumn("State");

            foreach (var pr in section)
            {
                var state = pr.State switch
                {
                    PullRequestState.Draft => "[dim]draft[/]",
                    PullRequestState.Merged => "[green]merged[/]",
                    PullRequestState.Closed => "[red]closed[/]",
                    _ => pr.HasConflicts ? "[red]conflicts[/]" : "[green]open[/]",
                };

                table.AddRow(
                    Markup.Escape(pr.Source.ShortName()),
                    Markup.Escape(pr.Reference),
                    Markup.Escape(Truncate(pr.Title, 60)),
                    Markup.Escape($"{pr.Container}/{pr.Repository}"),
                    Markup.Escape(pr.TargetBranch),
                    state);
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        }

        if (queue.IsEmpty)
        {
            AnsiConsole.MarkupLine("[green]No open pull requests.[/]");
        }

        // Report partial failures rather than passing off an incomplete queue as
        // the whole picture.
        foreach (var error in queue.Errors)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]![/] {Markup.Escape(error.Source.DisplayName())} could not be reached: " +
                $"[dim]{Markup.Escape(error.Message)}[/]");
        }
    }

    private static void PrintJson(PullRequestQueue queue)
    {
        var payload = new
        {
            createdByMe = queue.Section(PullRequestRelation.CreatedByMe).Select(Describe),
            assignedToMe = queue.Section(PullRequestRelation.AssignedToMe).Select(Describe),
            errors = queue.Errors.Select(e => new { source = e.Source.ToString(), message = e.Message }),
        };

        Console.WriteLine(JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object Describe(UnifiedPullRequest pr) => new
    {
        source = pr.Source.ToString(),
        number = pr.Number,
        reference = pr.Reference,
        title = pr.Title,
        author = pr.AuthorName,
        container = pr.Container,
        repository = pr.Repository,
        sourceBranch = pr.SourceBranch,
        targetBranch = pr.TargetBranch,
        state = pr.State.ToString(),
        hasConflicts = pr.HasConflicts,
        commentCount = pr.CommentCount,
        createdAt = pr.CreatedAt,
        updatedAt = pr.UpdatedAt,
        url = pr.WebUrl,
        reviewers = pr.Reviewers.Select(r => new
        {
            name = r.DisplayName,
            vote = r.Vote.ToString(),
            required = r.IsRequired,
        }),
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
