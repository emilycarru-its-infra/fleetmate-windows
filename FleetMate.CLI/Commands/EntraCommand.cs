using System.CommandLine;
using System.Text.Json;
using FleetMate.Models.Graph;
using FleetMate.Services;
using Spectre.Console;

namespace FleetMate.Commands;

/// <summary>
/// Entra ID (Azure AD) user and group management commands
/// </summary>
public static class EntraCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static Command Create(GraphService? graphService, ReportMateService? reportMate)
    {
        var command = new Command("entra", "Entra ID - users, groups, membership");

        command.AddCommand(CreateUserCommand(graphService));
        command.AddCommand(CreateGroupCommand(graphService));
        command.AddCommand(CreateCheckGroupCommand(graphService));

        return command;
    }

    private static Command CreateUserCommand(GraphService? graphService)
    {
        var command = new Command("user", "Get Entra user information");

        var upnArg = new Argument<string>(
            name: "upn",
            description: "User principal name (email) or ID");

        var includeGroupsOption = new Option<bool>(
            aliases: ["--groups", "-g"],
            getDefaultValue: () => true,
            description: "Include group memberships (default: true)");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddArgument(upnArg);
        command.AddOption(includeGroupsOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (upn, includeGroups, json) =>
        {
            if (!EnsureConfigured(graphService)) return;

            EntraUser? user = null;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Fetching user {upn}...", async ctx =>
                {
                    user = await graphService!.GetUserAsync(upn, includeGroups);
                });

            if (user == null)
            {
                AnsiConsole.MarkupLine($"[yellow]User not found: {upn}[/]");
                return;
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(user, JsonOptions));
                return;
            }

            DisplayUserDetail(user);
        }, upnArg, includeGroupsOption, jsonOption);

        return command;
    }

    private static Command CreateGroupCommand(GraphService? graphService)
    {
        var command = new Command("group", "Get Entra group information and members");

        var nameArg = new Argument<string>(
            name: "name",
            description: "Group name or ID");

        var membersOption = new Option<bool>(
            aliases: ["--members", "-m"],
            description: "Show group members");

        var limitOption = new Option<int>(
            aliases: ["--limit", "-n"],
            getDefaultValue: () => 50,
            description: "Maximum members to show (default: 50)");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddArgument(nameArg);
        command.AddOption(membersOption);
        command.AddOption(limitOption);
        command.AddOption(jsonOption);

        command.SetHandler(async (name, showMembers, limit, json) =>
        {
            if (!EnsureConfigured(graphService)) return;

            EntraGroup? group = null;
            List<EntraUser> members = new();

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Fetching group {name}...", async ctx =>
                {
                    group = Guid.TryParse(name, out _)
                        ? await graphService!.GetGroupByIdAsync(name)
                        : await graphService!.GetGroupByNameAsync(name);

                    if (group != null && showMembers)
                    {
                        members = await graphService!.GetGroupMembersAsync(group.Id, limit);
                    }
                });

            if (group == null)
            {
                AnsiConsole.MarkupLine($"[yellow]Group not found: {name}[/]");
                return;
            }

            group.Members = members;
            group.MemberCount = members.Count;

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(group, JsonOptions));
                return;
            }

            DisplayGroupDetail(group, showMembers);
        }, nameArg, membersOption, limitOption, jsonOption);

        return command;
    }

    private static Command CreateCheckGroupCommand(GraphService? graphService)
    {
        var command = new Command("check-group", "Check if a user is a member of a group");

        var upnArg = new Argument<string>(
            name: "upn",
            description: "User principal name (email)");

        var groupArg = new Argument<string>(
            name: "group",
            description: "Group name or ID");

        var jsonOption = new Option<bool>(
            aliases: ["--json"],
            description: "Output as JSON");

        command.AddArgument(upnArg);
        command.AddArgument(groupArg);
        command.AddOption(jsonOption);

        command.SetHandler(async (upn, group, json) =>
        {
            if (!EnsureConfigured(graphService)) return;

            bool isMember = false;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Checking if {upn} is in {group}...", async ctx =>
                {
                    isMember = await graphService!.CheckGroupMembershipAsync(upn, group);
                });

            if (json)
            {
                var result = new { User = upn, Group = group, IsMember = isMember };
                Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                return;
            }

            if (isMember)
            {
                AnsiConsole.MarkupLine($"[green]Yes[/] - {upn} [green]IS[/] a member of {group}");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]No[/] - {upn} [red]IS NOT[/] a member of {group}");
            }
        }, upnArg, groupArg, jsonOption);

        return command;
    }

    private static bool EnsureConfigured(GraphService? graph)
    {
        if (graph != null) return true;

        AnsiConsole.MarkupLine("[red]Entra ID is not configured.[/]");
        AnsiConsole.MarkupLine("Add Graph configuration to your config file (~/.fleetmate/config.yaml):");
        AnsiConsole.MarkupLine("  [cyan]graph:[/]");
        AnsiConsole.MarkupLine("    [cyan]useAzureCliAuth:[/] true");
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("Then log in with: [cyan]az login[/]");
        return false;
    }

    private static void DisplayUserDetail(EntraUser user)
    {
        var statusColor = user.AccountEnabled == true ? "green" : "red";

        var panel = new Panel(
            new Rows(
                new Markup($"[bold]{Markup.Escape(user.DisplayName)}[/]"),
                new Text(""),
                new Markup($"[dim]UPN:[/] {user.UserPrincipalName}"),
                new Markup($"[dim]Email:[/] {user.Mail ?? "-"}"),
                new Markup($"[dim]Status:[/] [{statusColor}]{(user.AccountEnabled == true ? "Enabled" : "Disabled")}[/]"),
                new Text(""),
                new Markup($"[dim]Title:[/] {user.JobTitle ?? "-"}"),
                new Markup($"[dim]Department:[/] {user.Department ?? "-"}"),
                new Markup($"[dim]Office:[/] {user.OfficeLocation ?? "-"}"),
                new Markup($"[dim]Company:[/] {user.CompanyName ?? "-"}"),
                new Text(""),
                new Markup($"[dim]Phone:[/] {user.MobilePhone ?? (user.BusinessPhones.FirstOrDefault() ?? "-")}"),
                new Markup($"[dim]Employee ID:[/] {user.EmployeeId ?? "-"}"),
                new Markup($"[dim]Created:[/] {user.CreatedDateTime?.ToString("g") ?? "-"}")
            ))
        {
            Header = new PanelHeader(" Entra User "),
            Border = BoxBorder.Rounded
        };

        AnsiConsole.Write(panel);

        if (user.MemberOf.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[dim]Group Memberships[/]").LeftJustified());

            var table = new Table();
            table.Border = TableBorder.Simple;
            table.AddColumn("Group Name");

            foreach (var group in user.MemberOf.OrderBy(g => g.DisplayName))
            {
                table.AddRow(Markup.Escape(group.DisplayName));
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[dim]Member of {user.MemberOf.Count} groups[/]");
        }
    }

    private static void DisplayGroupDetail(EntraGroup group, bool showMembers)
    {
        var typeDesc = group.IsDynamic ? "Dynamic" : (group.IsM365Group ? "Microsoft 365" : "Security");

        var panel = new Panel(
            new Rows(
                new Markup($"[bold]{Markup.Escape(group.DisplayName)}[/]"),
                new Text(""),
                new Markup($"[dim]ID:[/] {group.Id}"),
                new Markup($"[dim]Type:[/] {typeDesc}"),
                new Markup($"[dim]Description:[/] {Markup.Escape(group.Description ?? "-")}"),
                new Markup($"[dim]Mail:[/] {group.Mail ?? "-"}"),
                new Markup($"[dim]Created:[/] {group.CreatedDateTime?.ToString("g") ?? "-"}"),
                new Markup($"[dim]Synced from AD:[/] {(group.OnPremisesSyncEnabled == true ? "Yes" : "No")}")
            ))
        {
            Header = new PanelHeader(" Entra Group "),
            Border = BoxBorder.Rounded
        };

        AnsiConsole.Write(panel);

        if (showMembers && group.Members.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[dim]Members[/]").LeftJustified());

            var table = new Table();
            table.Border = TableBorder.Simple;
            table.AddColumn("Name");
            table.AddColumn("UPN");
            table.AddColumn("Title");

            foreach (var member in group.Members.OrderBy(m => m.DisplayName))
            {
                table.AddRow(
                    Markup.Escape(member.DisplayName),
                    member.UserPrincipalName,
                    Markup.Escape(member.JobTitle ?? "-"));
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[dim]Showing {group.Members.Count} members[/]");
        }
        else if (group.IsDynamic)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[dim]Membership rule:[/] {Markup.Escape(group.MembershipRule ?? "-")}");
        }
    }
}
