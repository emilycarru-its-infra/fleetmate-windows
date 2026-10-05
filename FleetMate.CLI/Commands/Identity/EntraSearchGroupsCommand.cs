using System.CommandLine;
using System.Text.Json;
using FleetMate.Core.Services;
using Spectre.Console;
using static FleetMate.Commands.Devices.IntuneLifecycleCommands;

namespace FleetMate.Commands.Identity;

/// <summary><c>entra search-groups</c>: groups whose name starts with a query (macOS parity).</summary>
public static class EntraSearchGroupsCommand
{
    public static Command Create(GraphService? graph)
    {
        var command = new Command("search-groups", "Search for groups by name");
        var query = new Argument<string>("query", "Search query (group name prefix)");
        var limit = new Option<int>(new[] { "-l", "--limit" }, () => 20, "Maximum results");
        var json = JsonFlag();
        command.AddArgument(query);
        command.AddOption(limit);
        command.AddOption(json);
        command.SetHandler(async context =>
        {
            if (!Configured(graph, context)) return;
            var q = context.ParseResult.GetValueForArgument(query);
            var groups = await graph!.SearchGroupsAsync(q, context.ParseResult.GetValueForOption(limit));
            if (groups.Count == 0)
            {
                AnsiConsole.MarkupLine($"\n[yellow]No groups found matching: {Esc(q)}[/]\n");
                return;
            }
            if (context.ParseResult.GetValueForOption(json))
            {
                Console.WriteLine(JsonSerializer.Serialize(groups, Json));
                return;
            }
            AnsiConsole.MarkupLine($"\n[bold]Groups matching '{Esc(q)}'[/] ({groups.Count} found)\n");
            foreach (var g in groups)
            {
                var security = g.SecurityEnabled == true ? "[cyan][[S]][/]" : "";
                AnsiConsole.MarkupLine($"{security} [bold]{Esc(string.IsNullOrEmpty(g.DisplayName) ? "Unknown" : g.DisplayName)}[/]");
                if (!string.IsNullOrEmpty(g.Description))
                    AnsiConsole.WriteLine($"    {(g.Description.Length > 80 ? g.Description[..80] : g.Description)}");
            }
            AnsiConsole.WriteLine();
        });
        return command;
    }
}
