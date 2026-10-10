using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Services.Repos;
using Spectre.Console;

namespace FleetMate.Commands.Repos;

/// <summary>
/// <c>fleetmate repos</c> — the team's core repositories across Azure DevOps
/// and GitHub: what exists, where it is checked out, and git on those
/// checkouts. Every listing takes <c>--json</c>; agents are the main users.
///
/// Repository arguments accept a name, project/name (Azure DevOps),
/// owner/name (GitHub), org/project/name, a registry id (github:owner/name), or
/// a path to a registered checkout. Batch commands (status, fetch, pull) act on
/// every tracked repository when none is named. The registry lives in
/// repos.json in FleetMate's local application data folder, the catalog cache
/// in repos-catalog.json beside it.
/// </summary>
public static class ReposCommand
{
    public static Command Create(FleetMateConfig config, RepoManager? manager = null)
    {
        manager ??= new RepoManager();
        var command = new Command("repos", "Manage core repositories across Azure DevOps and GitHub");

        command.AddCommand(Catalog(config, manager));
        command.AddCommand(List(manager));
        command.AddCommand(Discover(manager));
        command.AddCommand(Link(manager));
        command.AddCommand(Clone(manager));
        command.AddCommand(Unlink(manager));
        command.AddCommand(Track(manager, true));
        command.AddCommand(Track(manager, false));
        command.AddCommand(Status(manager));
        command.AddCommand(Batch(manager, RepoBatchOperation.Fetch));
        command.AddCommand(Batch(manager, RepoBatchOperation.Pull));
        command.AddCommand(Push(manager));
        command.AddCommand(Commit(manager));
        command.AddCommand(Branch(manager));
        command.AddCommand(Diff(manager));
        command.AddCommand(Log(manager));
        command.AddCommand(Stats(manager));
        command.AddCommand(Files(manager));
        command.AddCommand(Grep(manager));
        command.AddCommand(SettingsCommand(manager));

        // `fleetmate repos` on its own lists the tracked repositories.
        command.SetHandler(() => Run(() => ListAsync(manager, false, false)));
        return command;
    }

    // ── Shared helpers ──────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOutput = new(RepoRegistryStore.Json)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    internal static void PrintJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOutput));

    /// <summary>
    /// Runs a handler, turning a refused or failed repository operation into
    /// its message on stderr and a failing exit code, not a stack trace.
    /// </summary>
    internal static async Task Run(Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (RepoException ex)
        {
            Fail(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Fail(ex.Message);
        }
    }

    internal static void Fail(string message)
    {
        Console.Error.WriteLine("error: " + message);
        Environment.ExitCode = 1;
    }

    private static Option<bool> JsonFlag() => new(new[] { "--json", "-j" }, "Output JSON");

    private static string Pad(string value, int width) =>
        value.Length >= width ? value[..Math.Max(0, width - 1)] + "…" : value.PadRight(width);

    private static string E(string? value) => Markup.Escape(value ?? "");

    internal static string Summary(RepoStatus status)
    {
        if (status.Error != null) return $"[red]{E(status.Error)}[/]";
        var parts = new List<string> { $"[cyan]{E(status.Branch ?? "(detached)")}[/]" };
        if (status.Upstream != null)
        {
            var sync = (status.Ahead > 0 ? $"↑{status.Ahead}" : "") + (status.Behind > 0 ? $"↓{status.Behind}" : "");
            parts.Add(sync.Length == 0 ? $"[grey]= {E(status.Upstream)}[/]" : $"[yellow]{sync} {E(status.Upstream)}[/]");
        }
        else
        {
            parts.Add("[grey]no upstream[/]");
        }
        if (status.IsClean)
        {
            parts.Add("[green]clean[/]");
        }
        else
        {
            var counts = new List<string>();
            if (status.Staged > 0) counts.Add($"{status.Staged} staged");
            if (status.Unstaged > 0) counts.Add($"{status.Unstaged} modified");
            if (status.Untracked > 0) counts.Add($"{status.Untracked} untracked");
            if (status.Conflicted > 0) counts.Add($"{status.Conflicted} conflicted");
            parts.Add($"[yellow]{string.Join(", ", counts)}[/]");
        }
        var extra = Math.Max(0, status.Worktrees.Count - 1);
        if (extra > 0) parts.Add($"[grey]{extra} worktree{(extra == 1 ? "" : "s")}[/]");
        return string.Join("  ", parts);
    }

    // ── catalog ─────────────────────────────────────────────────────────

    private static Command Catalog(FleetMateConfig config, RepoManager manager)
    {
        var command = new Command("catalog", "List every repository you can see in Azure DevOps and GitHub");
        var provider = new Option<string?>("--provider", "Limit to one provider: azdo or github");
        var filter = new Option<string?>(new[] { "--filter", "-f" }, "Only repositories whose name or scope contains this text");
        var cached = new Option<bool>("--cached", "Use the cached catalog instead of asking the providers");
        var archived = new Option<bool>("--archived", "Include archived repositories");
        var json = JsonFlag();
        command.AddOption(provider);
        command.AddOption(filter);
        command.AddOption(cached);
        command.AddOption(archived);
        command.AddOption(json);
        command.SetHandler(ctx => Run(async () =>
        {
            var r = ctx.ParseResult;
            var providerText = r.GetValueForOption(provider);
            RepoProvider? only = null;
            if (providerText != null && (only = RepoProviderExtensions.ParseCode(providerText)) is null or RepoProvider.Other)
                throw RepoException.InvalidArgument($"'{providerText}' is not a provider. Use azdo or github.");

            RepoCatalog catalog;
            var errors = new List<string>();
            if (r.GetValueForOption(cached))
            {
                catalog = manager.CachedCatalog() ?? new RepoCatalog();
            }
            else
            {
                var service = RepoCatalogService.FromConfig(config, manager.Settings(), only);
                catalog = await manager.RefreshCatalogAsync(service);
            }
            errors.AddRange(catalog.Errors);

            var registry = manager.Registry();
            var records = RepoRecord.Merge(catalog.Repos, Array.Empty<RepoRegistryEntry>())
                .Select(rec => rec with { Local = registry.Repos.GetValueOrDefault(rec.Id) })
                .Where(rec => only == null || rec.Key.Provider == only)
                .Where(rec => r.GetValueForOption(archived) || !(rec.Catalog?.IsArchived ?? false))
                .ToList();
            if (r.GetValueForOption(filter) is { Length: > 0 } text)
                records = records.Where(rec => rec.Key.DisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();

            if (r.GetValueForOption(json))
            {
                PrintJson(new
                {
                    fetchedAt = catalog.FetchedAt,
                    errors,
                    repos = records.Select(rec => new
                    {
                        id = rec.Id,
                        provider = rec.Key.Provider.Code(),
                        organization = rec.Key.Provider == RepoProvider.AzureDevOps ? rec.Key.Owner : null,
                        owner = rec.Key.Provider == RepoProvider.AzureDevOps ? rec.Key.Project ?? rec.Key.Owner : rec.Key.Owner,
                        project = rec.Key.Project,
                        name = rec.Key.Name,
                        displayName = rec.Key.DisplayName,
                        cloneUrl = rec.Catalog?.CloneUrl,
                        sshUrl = rec.Catalog?.SshUrl,
                        webUrl = rec.Catalog?.WebUrl,
                        defaultBranch = rec.DefaultBranch,
                        archived = rec.Catalog?.IsArchived ?? false,
                        fork = rec.Catalog?.IsFork ?? false,
                        local = rec.IsLocal,
                        tracked = rec.IsTracked,
                        path = rec.Local?.Path,
                    }),
                });
                return;
            }

            AnsiConsole.MarkupLine($"\n[bold]Repository catalog[/] ({records.Count})\n");
            AnsiConsole.MarkupLine("   " + E(Pad("Provider", 8) + " " + Pad("Repository", 48) + " " + Pad("Default", 10) + " Local"));
            foreach (var rec in records)
            {
                var marker = rec.IsTracked ? "[green]●[/]" : rec.IsLocal ? "[cyan]○[/]" : " ";
                AnsiConsole.MarkupLine($" {marker} {E(Pad(rec.Key.Provider.Code(), 8))} {E(Pad(rec.Key.DisplayName, 48))} " +
                                       $"{E(Pad(rec.DefaultBranch ?? "", 10))} [grey]{E(rec.Local?.Path)}[/]");
            }
            AnsiConsole.MarkupLine("\n[grey] ● tracked  ○ local, untracked[/]");
            foreach (var error in errors) AnsiConsole.MarkupLine($"[yellow]warning: {E(error)}[/]");
        }));
        return command;
    }

    // ── list ────────────────────────────────────────────────────────────

    private static Command List(RepoManager manager)
    {
        var command = new Command("list", "List local repositories with a status summary");
        var allOption = new Option<bool>(new[] { "--all", "-a" }, "Include local repositories that are not tracked");
        var jsonOption = JsonFlag();
        command.AddOption(allOption);
        command.AddOption(jsonOption);
        command.SetHandler(ctx => Run(() => ListAsync(manager,
            ctx.ParseResult.GetValueForOption(allOption), ctx.ParseResult.GetValueForOption(jsonOption))));
        return command;
    }

    private static async Task ListAsync(RepoManager manager, bool all, bool json)
    {
        var records = manager.Records().Where(r => all ? r.IsLocal : r.IsTracked).ToList();
        var statuses = await manager.StatusAsync(records);
        if (json) { PrintJson(statuses); return; }
        if (records.Count == 0)
        {
            Console.WriteLine(all
                ? "No local repositories registered. Run 'fleetmate repos discover'."
                : "No tracked repositories. Track one with 'fleetmate repos track <repo>', or list all local ones with --all.");
            return;
        }
        Console.WriteLine();
        foreach (var (record, status) in records.Zip(statuses))
        {
            var marker = record.IsTracked ? "[green]●[/]" : "[cyan]○[/]";
            AnsiConsole.MarkupLine($" {marker} {E(Pad(record.Key.DisplayName, 44))} {Summary(status)}");
        }
        Console.WriteLine();
    }

    // ── discover ────────────────────────────────────────────────────────

    private static Command Discover(RepoManager manager)
    {
        var command = new Command("discover", "Find existing clones under the scan roots and link them");
        var root = new Option<string[]>("--root", "Folder to scan instead of the configured roots (repeatable)") { AllowMultipleArgumentsPerToken = false };
        var depth = new Option<int?>("--depth", "Folder levels to descend below each root");
        var track = new Option<bool>("--track", "Also mark every linked repository as tracked");
        var json = JsonFlag();
        command.AddOption(root);
        command.AddOption(depth);
        command.AddOption(track);
        command.AddOption(json);
        command.SetHandler(ctx => Run(async () =>
        {
            var r = ctx.ParseResult;
            var settings = manager.Settings();
            if (r.GetValueForOption(root) is { Length: > 0 } roots) settings.ScanRoots = roots.ToList();
            if (r.GetValueForOption(depth) is { } d) settings.ScanDepth = Math.Max(1, d);
            var results = await manager.DiscoverAsync(r.GetValueForOption(track), settings);
            if (r.GetValueForOption(json)) { PrintJson(results); return; }

            var linked = results.Where(x => x.Action == DiscoveryAction.Linked).ToList();
            var known = results.Count(x => x.Action == DiscoveryAction.AlreadyLinked);
            var duplicates = results.Where(x => x.Action == DiscoveryAction.Duplicate).ToList();
            var noRemote = results.Count(x => x.Action == DiscoveryAction.NoRemote);
            AnsiConsole.MarkupLine($"\nScanned {E(string.Join(", ", settings.ScanRoots))} (depth {settings.ScanDepth}): {results.Count} checkouts\n");
            foreach (var x in linked)
            {
                var note = x.InCatalog ? "" : "  [yellow](not in catalog)[/]";
                AnsiConsole.MarkupLine($"  [green]+[/] {E(Pad(x.DisplayName ?? "", 44))} [grey]{E(x.Path)}[/]{note}");
            }
            foreach (var x in duplicates)
                AnsiConsole.MarkupLine($"  [yellow]=[/] {E(Pad(x.DisplayName ?? "", 44))} [grey]{E(x.Path)}[/]  (already at {E(x.RegisteredPath)})");
            Console.WriteLine($"\n{linked.Count} linked, {known} already linked, {duplicates.Count} duplicate copies, {noRemote} without a usable origin");
        }));
        return command;
    }

    // ── link / clone / unlink / track ───────────────────────────────────

    private static Command Link(RepoManager manager)
    {
        var command = new Command("link", "Link a repository to an existing checkout. With only a path, the repository is taken from its origin remote.");
        var repo = new Argument<string>("repo", "Repository (name, project/name or owner/name), or the path when it is the only argument");
        var path = new Argument<string?>("path", "Path of the checkout") { Arity = ArgumentArity.ZeroOrOne };
        var untracked = new Option<bool>("--untracked", "Link without tracking");
        var json = JsonFlag();
        command.AddArgument(repo);
        command.AddArgument(path);
        command.AddOption(untracked);
        command.AddOption(json);
        command.SetHandler(ctx => Run(async () =>
        {
            var r = ctx.ParseResult;
            var repoValue = r.GetValueForArgument(repo);
            var pathValue = r.GetValueForArgument(path);
            var entry = await manager.LinkAsync(pathValue ?? repoValue, pathValue == null ? null : repoValue, !r.GetValueForOption(untracked));
            if (r.GetValueForOption(json)) PrintJson(entry);
            else AnsiConsole.MarkupLine($"[green]Linked {E(entry.Key.DisplayName)} → {E(entry.Path)}[/]");
        }));
        return command;
    }

    private static Command Clone(RepoManager manager)
    {
        var command = new Command("clone",
            @"Clone a catalog repository into <root>\AzDevOps\<Project>\<Repo> or <root>\GitHub\<owner>\<repo> with your own git credentials, and track it");
        var repo = new Argument<string>("repo", "Repository (name, project/name or owner/name)");
        var root = new Option<string?>("--root", "Clone root (default: the configured clone root)");
        var into = new Option<string?>("--into", "Exact destination folder, overriding the layout");
        var ssh = new Option<bool>("--ssh", "Clone over ssh instead of https");
        var json = JsonFlag();
        command.AddArgument(repo);
        command.AddOption(root);
        command.AddOption(into);
        command.AddOption(ssh);
        command.AddOption(json);
        command.SetHandler(ctx => Run(async () =>
        {
            var r = ctx.ParseResult;
            var entry = await manager.CloneAsync(r.GetValueForArgument(repo), r.GetValueForOption(root), r.GetValueForOption(into), r.GetValueForOption(ssh));
            if (r.GetValueForOption(json)) PrintJson(entry);
            else AnsiConsole.MarkupLine($"[green]Cloned {E(entry.Key.DisplayName)} → {E(entry.Path)}[/]");
        }));
        return command;
    }

    private static Command Unlink(RepoManager manager)
    {
        var command = new Command("unlink", "Forget a local checkout (the folder is left untouched)");
        var repos = new Argument<string[]>("repos", "Repositories") { Arity = ArgumentArity.OneOrMore };
        command.AddArgument(repos);
        command.SetHandler(ctx => Run(() =>
        {
            foreach (var repo in ctx.ParseResult.GetValueForArgument(repos))
                Console.WriteLine($"Unlinked {manager.Unlink(repo).Key.DisplayName}");
            return Task.CompletedTask;
        }));
        return command;
    }

    private static Command Track(RepoManager manager, bool tracked)
    {
        var command = tracked
            ? new Command("track", "Mark local repositories as tracked")
            : new Command("untrack", "Stop tracking repositories (the checkout stays linked)");
        var repos = new Argument<string[]>("repos", "Repositories") { Arity = ArgumentArity.OneOrMore };
        command.AddArgument(repos);
        command.SetHandler(ctx => Run(() =>
        {
            foreach (var repo in ctx.ParseResult.GetValueForArgument(repos))
            {
                var record = manager.SetTracked(repo, tracked);
                if (tracked) AnsiConsole.MarkupLine($"[green]Tracking {E(record.Key.DisplayName)}[/]");
                else Console.WriteLine($"Stopped tracking {record.Key.DisplayName}");
            }
            return Task.CompletedTask;
        }));
        return command;
    }

    // ── status / fetch / pull ───────────────────────────────────────────

    private static Command Status(RepoManager manager)
    {
        var command = new Command("status", "Branch, sync and change counts, worktrees and AGENTS.md for repositories (default: all tracked)");
        var repos = new Argument<string[]>("repos", "Repositories (default: all tracked)") { Arity = ArgumentArity.ZeroOrMore };
        var files = new Option<bool>("--files", "List each changed file");
        var json = JsonFlag();
        command.AddArgument(repos);
        command.AddOption(files);
        command.AddOption(json);
        command.SetHandler(ctx => Run(async () =>
        {
            var r = ctx.ParseResult;
            var records = manager.ResolveLocal(r.GetValueForArgument(repos));
            var statuses = await manager.StatusAsync(records);
            if (statuses.Any(s => s.Error != null)) Environment.ExitCode = 1;
            if (r.GetValueForOption(json)) { PrintJson(statuses); return; }
            if (records.Count == 0)
            {
                Console.WriteLine("No tracked repositories. Name one, or track some with 'fleetmate repos track'.");
                return;
            }
            foreach (var status in statuses)
            {
                AnsiConsole.MarkupLine($"\n[bold]{E(status.DisplayName)}[/]  [grey]{E(status.Path)}[/]");
                AnsiConsole.MarkupLine("  " + Summary(status));
                if (status.AgentsFile != null) AnsiConsole.MarkupLine($"  [grey]read first:[/] {E(status.AgentsFile)}");
                foreach (var worktree in status.Worktrees.Skip(1))
                    AnsiConsole.MarkupLine($"  [grey]worktree[/] [cyan]{E(worktree.Branch ?? "(detached)")}[/] [grey]{E(worktree.Path)}[/]");
                if (!r.GetValueForOption(files)) continue;
                foreach (var change in status.Changes)
                {
                    var code = change.Kind == RepoChangeKind.Untracked ? "??" : change.IndexStatus + change.WorktreeStatus;
                    var from = change.OriginalPath != null ? $" ← {change.OriginalPath}" : "";
                    Console.WriteLine($"    {code} {change.Path}{from}");
                }
            }
            Console.WriteLine();
        }));
        return command;
    }

    private static Command Batch(RepoManager manager, RepoBatchOperation operation)
    {
        var command = operation == RepoBatchOperation.Fetch
            ? new Command("fetch", "git fetch --prune for repositories (default: all tracked)")
            : new Command("pull", "git pull --ff-only for repositories (default: all tracked)");
        var repos = new Argument<string[]>("repos", "Repositories (default: all tracked)") { Arity = ArgumentArity.ZeroOrMore };
        var json = JsonFlag();
        command.AddArgument(repos);
        command.AddOption(json);
        command.SetHandler(ctx => Run(async () =>
        {
            var records = manager.ResolveLocal(ctx.ParseResult.GetValueForArgument(repos));
            var results = await manager.RunAsync(operation, records);
            if (results.Any(x => !x.Succeeded)) Environment.ExitCode = 1;
            if (ctx.ParseResult.GetValueForOption(json)) { PrintJson(results); return; }
            foreach (var result in results)
            {
                var mark = result.Succeeded ? "[green]✓[/]" : "[red]✗[/]";
                var detail = result.Error ?? result.Output.Split('\n').LastOrDefault(l => l.Trim().Length > 0) ?? "";
                AnsiConsole.MarkupLine($" {mark} {E(Pad(result.DisplayName, 44))} [grey]{E(detail.Trim())}[/]");
            }
        }));
        return command;
    }

    // ── push / commit / branch ──────────────────────────────────────────

    private static Command Push(RepoManager manager)
    {
        var command = new Command("push",
            "Push the current branch, setting its upstream on first push. Refuses main, master and the repository's default branch unless --allow-main.");
        var repo = new Argument<string>("repo", "Repository");
        var allowMain = new Option<bool>("--allow-main", "Allow pushing a protected branch");
        command.AddArgument(repo);
        command.AddOption(allowMain);
        command.SetHandler(ctx => Run(async () =>
        {
            var record = manager.Resolve(ctx.ParseResult.GetValueForArgument(repo));
            var output = await manager.WorkingCopy(record).PushAsync(manager.ProtectedBranches(record), ctx.ParseResult.GetValueForOption(allowMain));
            Console.WriteLine(output.Trim());
        }));
        return command;
    }

    private static Command Commit(RepoManager manager)
    {
        var command = new Command("commit",
            "Commit all changes, or only the given paths. Refuses main, master and the repository's default branch unless --allow-main.");
        var repo = new Argument<string>("repo", "Repository");
        var paths = new Argument<string[]>("paths", "Paths to commit (default: every change)") { Arity = ArgumentArity.ZeroOrMore };
        var message = new Option<string>(new[] { "--message", "-m" }, "Commit message") { IsRequired = true };
        var allowMain = new Option<bool>("--allow-main", "Allow committing on a protected branch");
        var json = JsonFlag();
        command.AddArgument(repo);
        command.AddArgument(paths);
        command.AddOption(message);
        command.AddOption(allowMain);
        command.AddOption(json);
        command.SetHandler(ctx => Run(async () =>
        {
            var r = ctx.ParseResult;
            var record = manager.Resolve(r.GetValueForArgument(repo));
            var commit = await manager.WorkingCopy(record).CommitAsync(
                r.GetValueForOption(message) ?? "", r.GetValueForArgument(paths), manager.ProtectedBranches(record), r.GetValueForOption(allowMain));
            if (r.GetValueForOption(json)) PrintJson(commit);
            else AnsiConsole.MarkupLine($"[green]{E(commit.ShortSha)} {E(commit.Subject)}[/]");
        }));
        return command;
    }

    private static Command Branch(RepoManager manager)
    {
        var command = new Command("branch", "Switch to a branch, creating it when it does not exist");
        var repo = new Argument<string>("repo", "Repository");
        var name = new Argument<string>("name", "Branch name");
        var from = new Option<string?>("--from", "Start point for a new branch, e.g. origin/main");
        command.AddArgument(repo);
        command.AddArgument(name);
        command.AddOption(from);
        command.SetHandler(ctx => Run(async () =>
        {
            var r = ctx.ParseResult;
            var copy = manager.WorkingCopy(manager.Resolve(r.GetValueForArgument(repo)));
            var output = await copy.SwitchBranchAsync(r.GetValueForArgument(name), startPoint: r.GetValueForOption(from));
            Console.WriteLine(output.Trim());
        }));
        return command;
    }

    // ── diff / log / stats ──────────────────────────────────────────────

    private static Command Diff(RepoManager manager)
    {
        var command = new Command("diff", "Show unstaged (or staged) changes");
        var repo = new Argument<string>("repo", "Repository");
        var paths = new Argument<string[]>("paths", "Limit to these paths") { Arity = ArgumentArity.ZeroOrMore };
        var staged = new Option<bool>("--staged", "Diff the index against HEAD instead of the worktree against the index");
        var stat = new Option<bool>("--stat", "Summary only");
        command.AddArgument(repo);
        command.AddArgument(paths);
        command.AddOption(staged);
        command.AddOption(stat);
        command.SetHandler(ctx => Run(async () =>
        {
            var r = ctx.ParseResult;
            var copy = manager.WorkingCopy(manager.Resolve(r.GetValueForArgument(repo)));
            Console.Write(await copy.DiffAsync(r.GetValueForOption(staged), r.GetValueForOption(stat), r.GetValueForArgument(paths)));
        }));
        return command;
    }

    private static Command Log(RepoManager manager)
    {
        var command = new Command("log", "Recent commits");
        var repo = new Argument<string>("repo", "Repository");
        var number = new Option<int>(new[] { "--number", "-n" }, () => 20, "Number of commits");
        var reference = new Option<string?>("--ref", "Branch or ref (default: HEAD)");
        var json = JsonFlag();
        command.AddArgument(repo);
        command.AddOption(number);
        command.AddOption(reference);
        command.AddOption(json);
        command.SetHandler(ctx => Run(async () =>
        {
            var r = ctx.ParseResult;
            var copy = manager.WorkingCopy(manager.Resolve(r.GetValueForArgument(repo)));
            var commits = await copy.LogAsync(r.GetValueForOption(number), r.GetValueForOption(reference));
            if (r.GetValueForOption(json)) { PrintJson(commits); return; }
            foreach (var commit in commits)
            {
                var date = commit.Date?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";
                AnsiConsole.MarkupLine($"[yellow]{E(commit.ShortSha)}[/] [grey]{date}[/] [cyan]{E(Pad(commit.Author, 18))}[/] {E(commit.Subject)}");
            }
        }));
        return command;
    }

    private static Command Stats(RepoManager manager)
    {
        var command = new Command("stats",
            "Commit, author and churn statistics from git history. With one repository: commits and lines over time, top authors, " +
            "most-changed files and folders, and commits by weekday and hour. With none (or several): one row per repository plus " +
            "the combined statistics. Merge commits are left out; renames count as a delete plus an add.");
        var repos = new Argument<string[]>("repos", "Repositories (default: all tracked)") { Arity = ArgumentArity.ZeroOrMore };
        var since = new Option<string>("--since", () => "90d", "Start of the period: 30d, 12w, 6m, 1y, YYYY-MM-DD, or all");
        var by = new Option<string?>("--by", "Timeline bucket: day, week or month (default: chosen from the period)");
        var top = new Option<int>("--top", () => 10, "How many files, folders and authors to list");
        var json = JsonFlag();
        command.AddArgument(repos);
        command.AddOption(since);
        command.AddOption(by);
        command.AddOption(top);
        command.AddOption(json);
        command.SetHandler(ctx => Run(async () =>
        {
            var r = ctx.ParseResult;
            var sinceText = r.GetValueForOption(since) ?? "90d";
            DateTimeOffset? start = null;
            if (!sinceText.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                start = RepoStatsRange.ParseSince(sinceText)
                    ?? throw RepoException.InvalidArgument($"'{sinceText}' is not a period. Use 30d, 12w, 6m, 1y, YYYY-MM-DD or all.");
            }
            RepoStatsBucket? bucket = null;
            if (r.GetValueForOption(by) is { } byText)
            {
                bucket = RepoStatsRange.ParseBucket(byText)
                    ?? throw RepoException.InvalidArgument($"'{byText}' is not a bucket. Use day, week or month.");
            }
            var limit = Math.Max(0, r.GetValueForOption(top));
            var asJson = r.GetValueForOption(json);

            var records = manager.ResolveLocal(r.GetValueForArgument(repos));
            if (records.Count == 1)
            {
                var report = await manager.StatsAsync(records[0], start, bucket: bucket, top: limit);
                if (asJson) { PrintJson(report); return; }
                AnsiConsole.MarkupLine($"\n[bold]{E(records[0].Key.DisplayName)}[/]  [grey]{Period(start)}[/]");
                PrintReport(report, limit);
                return;
            }
            if (records.Count == 0)
            {
                Console.WriteLine("No tracked repositories. Name one, or track some with 'fleetmate repos track'.");
                return;
            }
            var summary = await manager.StatsSummaryAsync(records, start, bucket: bucket, top: limit);
            if (asJson) { PrintJson(summary); return; }
            AnsiConsole.MarkupLine($"\n[bold]Tracked repositories[/]  [grey]{Period(start)}[/]\n");
            AnsiConsole.MarkupLine("[grey]" + E(Pad("Repository", 44) + " " + Pad("Commits", 8) + " " + Pad("+lines", 9) + " " +
                                                Pad("-lines", 9) + " " + Pad("Ahead", 6) + " " + Pad("Behind", 7) + " Open") + "[/]");
            foreach (var row in summary.Rows)
            {
                if (row.Error != null && row.Commits == 0)
                {
                    AnsiConsole.MarkupLine($"{E(Pad(row.DisplayName, 44))} [yellow]{E(row.Error)}[/]");
                    continue;
                }
                Console.WriteLine(Pad(row.DisplayName, 44) + " " + Pad(row.Commits.ToString(), 8) + " " + Pad($"+{row.Added}", 9) + " " +
                                  Pad($"-{row.Removed}", 9) + " " + Pad(row.Ahead.ToString(), 6) + " " + Pad(row.Behind.ToString(), 7) + " " + row.OpenChanges);
            }
            AnsiConsole.MarkupLine("\n[bold]All repositories[/]");
            PrintReport(summary.Combined, limit);
        }));
        return command;
    }

    private static string Period(DateTimeOffset? start) =>
        start is { } s ? "since " + s.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "all history";

    private static void PrintReport(RepoStatsReport report, int top)
    {
        var t = report.Total;
        AnsiConsole.MarkupLine($"  {t.Commits} commits by {t.Authors} author{(t.Authors == 1 ? "" : "s")}, " +
                               $"[green]+{t.Added}[/] / [yellow]-{t.Removed}[/] lines, {t.FilesTouched} files");
        var format = report.Bucket == RepoStatsBucket.Month ? "yyyy-MM" : "yyyy-MM-dd";
        var peak = Math.Max(1, report.Timeline.Select(p => p.Commits).DefaultIfEmpty(0).Max());
        AnsiConsole.MarkupLine($"\n  [bold]Commits per {report.Bucket.ToString().ToLowerInvariant()}[/]");
        foreach (var point in report.Timeline.TakeLast(26))
        {
            var bar = new string('▇', (int)Math.Ceiling(point.Commits / (double)peak * 30));
            AnsiConsole.MarkupLine($"  [grey]{point.Start.ToString(format, CultureInfo.InvariantCulture)}[/] {Pad(point.Commits.ToString(), 4)} [cyan]{bar}[/]");
        }
        if (report.Contributors.Count > 0)
        {
            AnsiConsole.MarkupLine("\n  [bold]Authors[/]");
            foreach (var person in report.Contributors.Take(top))
                AnsiConsole.MarkupLine($"  {Pad(person.Commits.ToString(), 6)}{E(Pad(person.Name, 28))}[grey]+{person.Added} -{person.Removed}[/]");
        }
        if (report.Areas.Count > 0)
        {
            AnsiConsole.MarkupLine("\n  [bold]Folders[/]");
            foreach (var area in report.Areas.Take(top))
                AnsiConsole.MarkupLine($"  {Pad(area.Commits.ToString(), 6)}{E(Pad(area.Path, 40))}[grey]+{area.Added} -{area.Removed}[/]");
        }
        if (report.Files.Count > 0)
        {
            AnsiConsole.MarkupLine("\n  [bold]Files[/]");
            foreach (var file in report.Files.Take(top))
                AnsiConsole.MarkupLine($"  {Pad(file.Commits.ToString(), 6)}{E(file.Path)}  [grey]+{file.Added} -{file.Removed}[/]");
        }
        Console.WriteLine();
    }

    // ── files / grep ────────────────────────────────────────────────────

    private static Command Files(RepoManager manager)
    {
        var command = new Command("files", "List files git sees (tracked plus untracked, honouring .gitignore)");
        var repo = new Argument<string>("repo", "Repository");
        var under = new Option<string?>("--under", "Only paths under this folder");
        var json = JsonFlag();
        command.AddArgument(repo);
        command.AddOption(under);
        command.AddOption(json);
        command.SetHandler(ctx => Run(async () =>
        {
            var r = ctx.ParseResult;
            var files = await manager.WorkingCopy(manager.Resolve(r.GetValueForArgument(repo))).ListFilesAsync();
            if (r.GetValueForOption(under) is { Length: > 0 } folder)
            {
                var prefix = folder.Replace('\\', '/').TrimEnd('/') + "/";
                files = files.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (r.GetValueForOption(json)) PrintJson(files);
            else files.ForEach(Console.WriteLine);
        }));
        return command;
    }

    private static Command Grep(RepoManager manager)
    {
        var command = new Command("grep", "Search a repository with git grep");
        var repo = new Argument<string>("repo", "Repository");
        var pattern = new Argument<string>("pattern", "Pattern (a basic regular expression unless --fixed)");
        var ignoreCase = new Option<bool>(new[] { "--ignore-case", "-i" }, "Ignore case");
        var fixedStrings = new Option<bool>(new[] { "--fixed", "-F" }, "Treat the pattern as a literal string");
        var limit = new Option<int>("--limit", () => 1000, "Maximum matches");
        var json = JsonFlag();
        command.AddArgument(repo);
        command.AddArgument(pattern);
        command.AddOption(ignoreCase);
        command.AddOption(fixedStrings);
        command.AddOption(limit);
        command.AddOption(json);
        command.SetHandler(ctx => Run(async () =>
        {
            var r = ctx.ParseResult;
            var matches = await manager.WorkingCopy(manager.Resolve(r.GetValueForArgument(repo))).GrepAsync(
                r.GetValueForArgument(pattern), r.GetValueForOption(ignoreCase), r.GetValueForOption(fixedStrings), limit: r.GetValueForOption(limit));
            if (r.GetValueForOption(json)) { PrintJson(matches); return; }
            foreach (var match in matches) AnsiConsole.MarkupLine($"[cyan]{E(match.Path)}:{match.Line}:[/] {E(match.Text)}");
        }));
        return command;
    }

    // ── settings ────────────────────────────────────────────────────────

    private static Command SettingsCommand(RepoManager manager)
    {
        var command = new Command("settings", "Show or change scan roots, clone root and extra GitHub owners");
        var scanRoot = new Option<string[]>("--scan-root", "Replace the scan roots (repeatable)");
        var depth = new Option<int?>("--depth", "Scan depth below each root");
        var cloneRoot = new Option<string?>("--clone-root", "Clone root for the standard layout");
        var owner = new Option<string[]>("--github-owner", "Replace the extra GitHub owners listed in the catalog (repeatable)");
        var concurrency = new Option<int?>("--concurrency", "Concurrent git processes for batch commands");
        var json = JsonFlag();
        command.AddOption(scanRoot);
        command.AddOption(depth);
        command.AddOption(cloneRoot);
        command.AddOption(owner);
        command.AddOption(concurrency);
        command.AddOption(json);
        command.SetHandler(ctx => Run(() =>
        {
            var r = ctx.ParseResult;
            var roots = r.GetValueForOption(scanRoot) ?? Array.Empty<string>();
            var owners = r.GetValueForOption(owner) ?? Array.Empty<string>();
            var depthValue = r.GetValueForOption(depth);
            var cloneValue = r.GetValueForOption(cloneRoot);
            var concurrencyValue = r.GetValueForOption(concurrency);
            if (roots.Length > 0 || owners.Length > 0 || depthValue != null || cloneValue != null || concurrencyValue != null)
            {
                manager.UpdateSettings(s =>
                {
                    if (roots.Length > 0) s.ScanRoots = roots.ToList();
                    if (depthValue is { } d) s.ScanDepth = Math.Max(1, d);
                    if (cloneValue != null) s.CloneRoot = cloneValue;
                    if (owners.Length > 0) s.GitHubOwners = owners.ToList();
                    if (concurrencyValue is { } c) s.Concurrency = Math.Max(1, c);
                });
            }
            var settings = manager.Settings();
            if (r.GetValueForOption(json)) { PrintJson(settings); return Task.CompletedTask; }
            Console.WriteLine($"registry:      {manager.Store.RegistryPath}");
            Console.WriteLine($"scan roots:    {string.Join(", ", settings.ScanRoots)} (depth {settings.ScanDepth})");
            Console.WriteLine($"skip:          {string.Join(", ", settings.SkipDirectories)}");
            Console.WriteLine($"clone root:    {settings.CloneRoot}");
            Console.WriteLine($"GitHub owners: {(settings.GitHubOwners.Count == 0 ? "(your own and your organizations')" : string.Join(", ", settings.GitHubOwners))}");
            Console.WriteLine($"protected:     {string.Join(", ", settings.ProtectedBranches)} + each repository's default branch");
            Console.WriteLine($"concurrency:   {settings.Concurrency}");
            return Task.CompletedTask;
        }));
        return command;
    }
}
