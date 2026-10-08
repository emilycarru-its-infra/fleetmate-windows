using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Models.Reporting;
using FleetMate.Core.Services.Projects;
using FleetMate.GUI.Views.Development;
using Serilog;
using SkiaSharp;

namespace FleetMate.GUI.Views.Shared.Widgets;

/// <summary>
/// Which widgets each tab shows, in order — the old Dashboard's cards, each
/// moved to the tab it belongs to, matching the macOS app. Everything is drawn
/// from data the app already holds; only the Projects GitHub-issues list and
/// the Devices ReportMate errors fetch anything of their own.
/// </summary>
public static class WidgetCatalog
{
    /// <summary>Filter categories a chart click hands to its tab, as the macOS module filters name them.</summary>
    public static class Category
    {
        public const string Platform = "Platform";
        public const string Compliance = "Compliance";
        public const string AssetCategory = "Category";
        public const string Status = "Status";
        public const string Priority = "Priority";
        public const string Repository = "Repository";
    }

    /// <summary>Tabs with no widgets get no section at all.</summary>
    public static bool HasWidgets(string tab) =>
        tab is "Development" or "Projects" or "Devices" or "Inventory" or "Tickets";

    /// <summary>The cache keys whose change should redraw <paramref name="tab"/>'s widgets.</summary>
    public static bool DependsOn(string tab, string cacheKey) => tab switch
    {
        "Devices" => cacheKey is "Devices",
        "Inventory" => cacheKey is "Assets",
        "Tickets" => cacheKey is "Tickets",
        "Projects" => cacheKey is "WorkItems" or "Sprints" or "Issues",
        "Development" => cacheKey is "PullRequests" or "Runs" or "Inbox",
        _ => false,
    };

    public static List<UIElement> Build(string tab, App app, Action<string, string> filter) => tab switch
    {
        "Development" => Development(app, filter),
        "Projects" => Projects(app),
        "Devices" => Devices(app, filter),
        "Inventory" => Inventory(app, filter),
        "Tickets" => Tickets(app, filter),
        _ => new(),
    };

    private static string Count(int value, bool loaded) => loaded ? value.ToString() : "--";

    /// <summary>
    /// Whether a provider failed this load (rate-limited, unreachable). Its
    /// count is unknown, so the tile shows "--": a 0 would read as nothing open.
    /// Being signed out of a provider on purpose is not a failure.
    /// </summary>
    internal static bool Failed(PullRequestQueue? queue, PullRequestSource? source = null) =>
        queue?.Errors.Any(e => (source == null || e.Source == source) && !PullRequestQueueView.IsExpectedSignedOut(e)) == true;

    // MARK: - Development

    private static List<UIElement> Development(App app, Action<string, string> filter)
    {
        var prs = app.DevelopmentPullRequests?.PullRequests ?? new List<UnifiedPullRequest>();
        var runs = app.DevelopmentRuns ?? new List<PipelineRun>();
        var queue = app.DevelopmentPullRequests;
        var prsLoaded = queue != null;
        var runsLoaded = app.DevelopmentRuns != null;

        var cards = new List<UIElement>
        {
            WidgetCards.KpiStack(new[]
            {
                new KpiTile("Unread in Inbox", app.Inbox.UnreadCount.ToString(), "", "#FF2196F3"),
                new KpiTile("Review Requested",
                    Count(prs.Count(p => p.Relations.Contains(PullRequestRelation.AssignedToMe)), prsLoaded), "", "#FF9C27B0"),
            }),
            WidgetCards.KpiStack(new[]
            {
                new KpiTile("DevOps Pull Requests", Count(prs.Count(p => p.Source == PullRequestSource.AzureDevOps), prsLoaded && !Failed(queue, PullRequestSource.AzureDevOps)), "", "#FF3F51B5"),
                new KpiTile("GitHub Pull Requests", Count(prs.Count(p => p.Source == PullRequestSource.GitHub), prsLoaded && !Failed(queue, PullRequestSource.GitHub)), "", "#FF607D8B"),
            }),
            WidgetCards.KpiStack(new[]
            {
                new KpiTile("Failing Pipelines", Count(CommitsAndPipelinesFilter.FailingCount(runs), runsLoaded), "", "#FFE07A1F"),
                new KpiTile("Running Pipelines", Count(runs.Count(r => r.Status.IsActive()), runsLoaded), "", "#FFD99E0B"),
            }),
        };

        var byRepo = prs
            .GroupBy(DevelopmentFilter.RepositoryKey)
            .Select(g => new ChartSlice(g.Key, g.Count()))
            .OrderByDescending(s => s.Value)
            .ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        if (byRepo.Count > 0)
        {
            cards.Add(WidgetCards.Card("Pull Requests by Repository",
                WidgetCards.Bars(byRepo, repo => filter(Category.Repository, repo)), units: 2));
        }

        return cards;
    }

    // MARK: - Projects

    private static List<GitHubIssueSummary>? _issues;
    private static bool _loadingIssues;

    /// <summary>GitHub issues for the Projects list, fetched once and kept; a redraw follows the fetch.</summary>
    private static void EnsureIssues(App app)
    {
        if (_issues != null || _loadingIssues) return;
        _loadingIssues = true;

        _ = Task.Run(async () =>
        {
            try
            {
                using var service = new GitHubPullRequestService(app.Config.GitHubProviderOrDefault());
                _issues = await service.GetMyIssuesAsync();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[widgets] GitHub issues unavailable");
                _issues = new();
            }
            finally
            {
                _loadingIssues = false;
                Application.Current.Dispatcher.Invoke(() => app.NotifyCacheChanged("Issues"));
            }
        });
    }

    /// <summary>
    /// States that take a work item off the open list. Resolved counts: the
    /// work is done and only awaits closing, so it is not something to do.
    /// </summary>
    private static readonly HashSet<string> FinishedStates =
        new(StringComparer.OrdinalIgnoreCase) { "Closed", "Removed", "Done", "Completed", "Resolved" };

    internal static bool IsClosed(string? state) => state is not null && FinishedStates.Contains(state);

    private static List<UIElement> Projects(App app)
    {
        EnsureIssues(app);

        var items = app.CachedWorkItems;
        var loaded = items.Count > 0;
        var active = items.Where(w => !IsClosed(w.State)).ToList();

        var cards = new List<UIElement>
        {
            WidgetCards.KpiStack(new[] { new KpiTile("Active Work Items", Count(active.Count, loaded), "", "#FF3F51B5") }),
        };

        if (loaded)
        {
            // Open work only: finished states would dwarf what is left to do.
            var states = active.GroupBy(w => string.IsNullOrEmpty(w.State) ? "Unknown" : w.State)
                .Select(g => new ChartSlice(g.Key, g.Count()))
                .OrderByDescending(s => s.Value)
                .ToList();

            var body = new StackPanel { Children = { WidgetCards.Donut(states) } };
            if (app.CachedSprints.FirstOrDefault(s => s.IsCurrent) is { } sprint)
            {
                var inSprint = items.Where(w => w.IterationPath?.EndsWith(sprint.Name) == true).ToList();
                body.Children.Add(WidgetCards.Caption(
                    $"Sprint: {sprint.Name} - {inSprint.Count(w => IsClosed(w.State))}/{inSprint.Count} done"));
            }
            cards.Add(WidgetCards.Card("Work Items", body));
        }

        var orgRoot = app.Config.AzureDevOps?.BaseUrl?.TrimEnd('/');
        var workRows = active
            .OrderByDescending(w => w.ChangedDate ?? DateTime.MinValue)
            .Select(w => new ListRow($"#{w.Id}  {w.Title}", $"{w.State} · {w.WorkItemType}",
                orgRoot == null ? null : () => OpenUrl($"{orgRoot}/_workitems/edit/{w.Id}")))
            .ToList();
        cards.Add(WidgetCards.Card("Work Items List", WidgetCards.List("Work Items", workRows), units: 2));

        var issueRows = (_issues ?? new())
            .Select(i => new ListRow($"#{i.Number}  {i.Title}", i.Repository, () => OpenUrl(i.WebUrl)))
            .ToList();
        cards.Add(WidgetCards.Card("GitHub Issues", WidgetCards.List("GitHub Issues", issueRows), units: 2));

        return cards;
    }

    // MARK: - Devices

    private static List<ErrorSummary>? _errors;
    private static bool _loadingErrors;

    private static List<UIElement> Devices(App app, Action<string, string> filter)
    {
        var devices = app.CachedDevices;
        var loaded = devices.Count > 0;

        var cards = new List<UIElement>
        {
            WidgetCards.KpiStack(new[]
            {
                new KpiTile("Managed Devices", Count(devices.Count, loaded), "", "#FF2196F3"),
                new KpiTile("Non-Compliant", Count(devices.Count(d => !d.IsCompliant), loaded), "", "#FFE07A1F"),
            }),
        };

        if (loaded)
        {
            var platforms = devices.GroupBy(d => d.OperatingSystem ?? "Unknown")
                .Select(g => new ChartSlice(g.Key, g.Count()))
                .OrderByDescending(s => s.Value)
                .ToList();
            cards.Add(WidgetCards.Card("Platform Distribution",
                WidgetCards.Treemap(platforms, p => filter(Category.Platform, p))));

            var compliant = devices.Count(d => d.IsCompliant);
            cards.Add(WidgetCards.Card("Compliance",
                WidgetCards.Donut(new[] { new ChartSlice("Compliant", compliant), new ChartSlice("Non-Compliant", devices.Count - compliant) },
                    c => filter(Category.Compliance, c),
                    new SKColor[] { new(76, 175, 80), new(224, 122, 31) })));
        }

        // Errors by Category only when ReportMate is configured.
        if (app.ReportMateService is { } reportMate)
        {
            if (_errors == null && !_loadingErrors)
            {
                _loadingErrors = true;
                _ = Task.Run(async () =>
                {
                    try { _errors = await reportMate.GetErrorsByItemAsync(false); }
                    catch (Exception ex) { Log.Debug(ex, "[widgets] ReportMate errors unavailable"); _errors = new(); }
                    finally
                    {
                        _loadingErrors = false;
                        Application.Current.Dispatcher.Invoke(() => app.NotifyCacheChanged("Devices"));
                    }
                });
            }

            var cats = (_errors ?? new())
                .GroupBy(e => e.Category)
                .Select(g => new ChartSlice(CategoryLabel(g.Key), g.Sum(e => e.DeviceCount)))
                .OrderByDescending(s => s.Value)
                .Take(8)
                .ToList();

            UIElement body = _errors == null
                ? WidgetCards.Caption("Loading…")
                : cats.Count == 0 ? WidgetCards.Caption("No errors found") : WidgetCards.Bars(cats);
            cards.Add(WidgetCards.Card("Errors by Category", body));
        }

        return cards;
    }

    private static string CategoryLabel(ErrorCategory category) => category switch
    {
        ErrorCategory.NotFound => "Not Found",
        ErrorCategory.HashMismatch => "Hash Mismatch",
        ErrorCategory.DownloadFailed => "Download Failed",
        ErrorCategory.MsiFailure => "MSI Failure",
        ErrorCategory.SignatureRequired => "Sig Required",
        ErrorCategory.CatalogMissing => "Catalog Missing",
        ErrorCategory.MissingChocolatey => "No Chocolatey",
        ErrorCategory.MissingSbinInstaller => "No sbin-installer",
        ErrorCategory.InstallVerificationFailed => "Verify Failed",
        ErrorCategory.MissingInstallerLocation => "No Installer Loc",
        _ => category.ToString(),
    };

    // MARK: - Inventory

    private static List<UIElement> Inventory(App app, Action<string, string> filter)
    {
        var assets = app.CachedAssets;
        var loaded = assets.Count > 0;

        var cards = new List<UIElement>
        {
            WidgetCards.KpiStack(new[]
            {
                new KpiTile("Assets", Count(assets.Count, loaded), "", "#FFFF9800"),
                new KpiTile("Deployed", Count(assets.Count(a =>
                    a.StatusLabel?.StatusMeta?.Equals("deployed", StringComparison.OrdinalIgnoreCase) == true), loaded), "", "#FF4CAF50"),
                new KpiTile("Unassigned", Count(assets.Count(a => a.AssignedTo == null), loaded), "", "#FF9E9E9E"),
            }),
        };

        if (loaded)
        {
            var categories = assets.GroupBy(a => a.Category?.Name ?? "Uncategorized")
                .Select(g => new ChartSlice(g.Key, g.Count()))
                .OrderByDescending(s => s.Value)
                .Take(8)
                .ToList();
            cards.Add(WidgetCards.Card("Assets by Category",
                WidgetCards.Bars(categories, c => filter(Category.AssetCategory, c))));

            var statuses = assets.GroupBy(a => string.IsNullOrEmpty(a.StatusLabel?.Name) ? "Unknown" : a.StatusLabel!.Name!)
                .Select(g => new ChartSlice(g.Key, g.Count()))
                .OrderByDescending(s => s.Value)
                .Take(6)
                .ToList();
            cards.Add(WidgetCards.Card("Asset Status",
                WidgetCards.Donut(statuses, s => filter(Category.Status, s))));
        }

        return cards;
    }

    // MARK: - Tickets

    private static readonly HashSet<string> ClosedStatuses = new(StringComparer.OrdinalIgnoreCase) { "closed", "cancelled", "canceled" };

    /// <summary>
    /// Not closed, by status name — the macOS rule. The model's own IsOpen
    /// only counts the New/None status classes, so every in-process ticket
    /// fell out of the count and the KPI read 0.
    /// </summary>
    internal static bool IsActiveTicket(string? statusName) =>
        string.IsNullOrEmpty(statusName) || !ClosedStatuses.Contains(statusName);

    private static List<UIElement> Tickets(App app, Action<string, string> filter)
    {
        var tickets = app.CachedTickets;
        var loaded = tickets.Count > 0;
        var active = tickets.Where(t => IsActiveTicket(t.StatusName)).ToList();
        var open = active.Count(t => !t.IsOnHold);
        var onHold = tickets.Count(t => t.IsOnHold);

        var cards = new List<UIElement>
        {
            WidgetCards.KpiStack(new[]
            {
                new KpiTile("Open Tickets", Count(open, loaded), "\uE8A7", "#FF9C27B0"),
                new KpiTile("SLA Violated", Count(tickets.Count(t => t.IsSlaViolated), loaded), "\uE7BA", "#FFE07A1F"),
            }),
        };

        if (loaded)
        {
            var status = new[] { new ChartSlice("Open", open), new ChartSlice("On Hold", onHold) }
                .Where(x => x.Value > 0).ToList();
            cards.Add(WidgetCards.Card("Ticket Status", WidgetCards.Donut(status, st => filter(Category.Status, st),
                new SKColor[] { new(33, 150, 243), new(255, 152, 0) })));

            // Keyed by name, not position, so a missing priority never shifts
            // the others' colours. No red: High is orange.
            var order = new Dictionary<string, int> { ["Low"] = 0, ["Medium"] = 1, ["High"] = 2 };
            var colorOf = new Dictionary<string, SKColor>
            {
                ["Low"] = new(76, 175, 80), ["Medium"] = new(33, 150, 243), ["High"] = new(255, 152, 0),
            };
            var priorities = active.GroupBy(t => t.PriorityName ?? "None")
                .OrderBy(g => order.GetValueOrDefault(g.Key, 99))
                .Select(g => new ChartSlice(g.Key, g.Count()))
                .ToList();
            cards.Add(WidgetCards.Card("Tickets by Priority",
                WidgetCards.Bars(priorities, pr => filter(Category.Priority, pr),
                    priorities.Select(x => colorOf.GetValueOrDefault(x.Label, new SKColor(158, 158, 158))).ToList())));
        }

        return cards;
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "[widgets] Could not open {Url}", url); }
    }
}
