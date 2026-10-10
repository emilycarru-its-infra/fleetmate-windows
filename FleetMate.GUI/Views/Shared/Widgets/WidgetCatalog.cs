using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Models.Reporting;
using FleetMate.Core.Services.Projects;
using FleetMate.GUI.Views.Development;
using Serilog;
using SkiaSharp;
using P = FleetMate.GUI.Views.Shared.Widgets.WidgetPalette;

namespace FleetMate.GUI.Views.Shared.Widgets;

/// <summary>
/// Which widgets each tab shows, in order — the old Dashboard's cards, each
/// moved to the tab it belongs to, matching the macOS app. Everything is drawn
/// from data the app already holds; only the Projects GitHub-issues list and
/// the Devices ReportMate figures fetch anything of their own.
/// </summary>
public static class WidgetCatalog
{
    /// <summary>Filter categories a widget click hands to its tab, as the macOS module filters name them.</summary>
    public static class Category
    {
        public const string Platform = "Platform";
        public const string Compliance = "Compliance";
        public const string AssetCategory = "Category";
        public const string Status = "Status";
        /// <summary>Inventory: a status type (Deployable, Deployed…), which several status names share.</summary>
        public const string StatusType = "Status Type";
        public const string Priority = "Priority";
        public const string Repository = "Repository";

        /// <summary>Development: open a segment ("Inbox").</summary>
        public const string Segment = "Segment";
        /// <summary>Development: open Pulls filtered to a source (a <see cref="DevelopmentSourceFilter"/> name).</summary>
        public const string Source = "Source";
        /// <summary>Development: open Pipelines filtered to a status (a <see cref="PipelineStatusFilter"/> name).</summary>
        public const string PipelineStatus = "PipelineStatus";
    }

    public const string InboxSegment = "Inbox";

    /// <summary>Tabs with no widgets get no section at all.</summary>
    public static bool HasWidgets(string tab) =>
        tab is "Development" or "Projects" or "Devices" or "Inventory" or "Tickets";

    /// <summary>The cache keys whose change should redraw <paramref name="tab"/>'s widgets.</summary>
    public static bool DependsOn(string tab, string cacheKey) => tab switch
    {
        "Devices" => cacheKey is "Devices",
        "Inventory" => cacheKey is "Assets",
        "Tickets" => cacheKey is "Tickets",
        "Projects" => cacheKey is "WorkItems" or "Sprints" or "Issues" or App.MyWorkItemsKey,
        "Development" => cacheKey is "PullRequests" or "Runs" or "Inbox",
        _ => false,
    };

    public static List<UIElement> Build(string tab, App app, Action<string, string> filter) => tab switch
    {
        "Development" => Development(app, filter),
        "Projects" => Projects(app),
        "Devices" => Devices(app, filter),
        "Inventory" => Inventory(app, filter),
        // The Tickets page builds its own cards (TicketWidgets), because
        // they count the list's filtered tickets and change its filters.
        "Tickets" => new(),
        _ => new(),
    };

    /// <summary>
    /// Whether a provider failed this load (rate-limited, unreachable). Its
    /// count is unknown, so the tile shows "--": a 0 would read as nothing open.
    /// Being signed out of a provider on purpose is not a failure.
    /// </summary>
    internal static bool Failed(PullRequestQueue? queue, PullRequestSource? source = null) =>
        queue?.Errors.Any(e => (source == null || e.Source == source) && !PullRequestQueueView.IsExpectedSignedOut(e)) == true;

    /// <summary>A count, or "--" when its provider failed.</summary>
    internal static string CountOrUnknown(int value, bool failed) => failed ? "--" : value.ToString("N0");

    /// <summary>
    /// The value a widget click hands its tab: a label ending in "(count)"
    /// filters on the name before it, as on the Mac.
    /// </summary>
    internal static string FilterValue(string label)
    {
        var cut = label.IndexOf(" (", StringComparison.Ordinal);
        return cut < 0 ? label : label[..cut];
    }

    /// <summary>
    /// The filter values a widget click selects: every value in
    /// <paramref name="available"/> whose label (through <paramref name="display"/>,
    /// when given) matches <paramref name="incoming"/>, ignoring case and
    /// punctuation — "Non-Compliant" finds "Noncompliant", and "Macintosh"
    /// finds "macOS". With no match the incoming value is used as it is.
    /// </summary>
    internal static string[] MatchFilterValues(string incoming, IEnumerable<string> available, Func<string, string>? display = null)
    {
        static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var want = Norm(incoming);
        var matches = available.Where(v => Norm(display?.Invoke(v) ?? v) == want).ToArray();
        return matches.Length > 0 ? matches : new[] { incoming };
    }

    // MARK: - Development

    private static readonly Color[] RepositoryColors = { P.Blue, P.Purple, P.Orange, P.Teal, P.Green, P.Indigo, P.Brown, P.Pink };

    /// <summary>The Unread in Inbox tile opens the Inbox when anything is unread, and Pull Requests otherwise.</summary>
    internal static (string Category, string Value) UnreadTarget(int unread) =>
        unread > 0 ? (Category.Segment, InboxSegment) : (Category.Source, nameof(DevelopmentSourceFilter.All));

    /// <summary>Open pull requests per repository, most first, top eight.</summary>
    internal static List<ChartSlice> RepositorySlices(IEnumerable<UnifiedPullRequest> prs) => prs
        .GroupBy(DevelopmentFilter.RepositoryKey)
        .Select(g => new ChartSlice(g.Key, g.Count()))
        .OrderByDescending(s => s.Value)
        .ThenBy(s => s.Label, StringComparer.Ordinal)
        .Take(8)
        .ToList();

    private static List<UIElement> Development(App app, Action<string, string> filter)
    {
        var queue = app.DevelopmentPullRequests;
        var prs = queue?.PullRequests ?? new List<UnifiedPullRequest>();
        var runs = app.DevelopmentRuns ?? new List<PipelineRun>();
        var prLoading = app.IsCacheLoading("PullRequests") && prs.Count == 0;
        var runsLoading = app.IsCacheLoading("Runs") && runs.Count == 0;
        var inboxLoading = app.Inbox.IsRefreshing && app.Inbox.Notifications.Count == 0;
        var unread = app.Inbox.UnreadCount;

        void Pulls(DevelopmentSourceFilter source) => filter(Category.Source, source.ToString());
        void Pipelines(PipelineStatusFilter status) => filter(Category.PipelineStatus, status.ToString());

        var cards = new List<UIElement>
        {
            WidgetCards.KpiStack(new[]
            {
                new KpiTile("Unread in Inbox", unread.ToString("N0"), "", P.Hex(P.Blue), inboxLoading,
                    () => { var (c, v) = UnreadTarget(app.Inbox.UnreadCount); filter(c, v); }),
                new KpiTile("Review Requested",
                    CountOrUnknown(prs.Count(p => p.Relations.Contains(PullRequestRelation.AssignedToMe)), Failed(queue)),
                    "", P.Hex(P.Purple), prLoading, () => Pulls(DevelopmentSourceFilter.All)),
            }),
            WidgetCards.KpiStack(new[]
            {
                new KpiTile($"{PullRequestSource.AzureDevOps.ShortName()} Pull Requests",
                    CountOrUnknown(prs.Count(p => p.Source == PullRequestSource.AzureDevOps), Failed(queue, PullRequestSource.AzureDevOps)),
                    "", P.Hex(P.Blue), prLoading, () => Pulls(DevelopmentSourceFilter.DevOps)),
                new KpiTile($"{PullRequestSource.GitHub.ShortName()} Pull Requests",
                    CountOrUnknown(prs.Count(p => p.Source == PullRequestSource.GitHub), Failed(queue, PullRequestSource.GitHub)),
                    "", P.Hex(P.Indigo), prLoading, () => Pulls(DevelopmentSourceFilter.GitHub)),
            }),
            WidgetCards.KpiStack(new[]
            {
                new KpiTile("Failing Pipelines", CommitsAndPipelinesFilter.FailingCount(runs).ToString("N0"), "",
                    P.Hex(P.Orange), runsLoading, () => Pipelines(PipelineStatusFilter.Failed)),
                new KpiTile("Running Pipelines", runs.Count(r => r.Status.IsActive()).ToString("N0"), "",
                    P.Hex(P.Teal), runsLoading, () => Pipelines(PipelineStatusFilter.Running)),
            }),
        };

        var byRepo = RepositorySlices(prs);
        cards.Add(WidgetCards.Card("Pull Requests by Repository",
            WidgetCards.ChartOr(byRepo.Count > 0, app.IsCacheLoading("PullRequests"), "No open pull requests",
                () => WidgetCards.HorizontalBars(byRepo, WidgetCards.ByIndex(byRepo, RepositoryColors),
                    repo => filter(Category.Repository, repo))),
            loading: app.IsCacheLoading("PullRequests")));

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
        new(AzureDevOpsService.FinishedStates, StringComparer.OrdinalIgnoreCase);

    internal static bool IsClosed(string? state) => state is not null && FinishedStates.Contains(state);

    private static readonly Color[] WorkItemColors = { P.Blue, P.Green, P.Orange, P.Purple, P.Gray, P.Brown };

    /// <summary>The user's open items by state, most first, each labelled "State (count)".</summary>
    internal static List<ChartSlice> WorkItemSlices(IEnumerable<WorkItem> items) => items
        .GroupBy(w => string.IsNullOrEmpty(w.State) ? "Unknown" : w.State)
        .Select(g => (State: g.Key, Count: g.Count()))
        .OrderByDescending(s => s.Count)
        .Select(s => new ChartSlice($"{s.State} ({s.Count})", s.Count))
        .ToList();

    /// <summary>"Sprint: Name · N open" — the user's open items in the current sprint.</summary>
    internal static string SprintCaption(string sprintName, IEnumerable<WorkItem> items) =>
        $"Sprint: {sprintName} · {items.Count(w => w.IterationPath?.EndsWith(sprintName, StringComparison.Ordinal) == true)} open";

    private static List<UIElement> Projects(App app)
    {
        EnsureIssues(app);
        _ = app.LoadMyWorkItemsAsync();

        // The signed-in user's open items across the organization, as on the Mac.
        var mine = app.CachedMyWorkItems ?? new List<WorkItem>();
        var loading = app.IsCacheLoading(App.MyWorkItemsKey);

        var cards = new List<UIElement>
        {
            WidgetCards.KpiStack(new[]
            {
                new KpiTile("Active Work Items", mine.Count.ToString("N0"), "", P.Hex(P.Indigo), loading && mine.Count == 0),
            }),
        };

        var slices = WorkItemSlices(mine);
        cards.Add(WidgetCards.Card("Work Items", WidgetCards.ChartOr(slices.Count > 0, loading, "No work item data", () =>
        {
            var body = new System.Windows.Controls.StackPanel
            {
                Children = { WidgetCards.Donut(slices, colors: slices.Select((_, i) => P.Sk(WorkItemColors[i % WorkItemColors.Length])).ToList()) },
            };
            if (app.CachedSprints.FirstOrDefault(s => s.IsCurrent) is { } sprint)
                body.Children.Add(WidgetCards.Caption(SprintCaption(string.IsNullOrEmpty(sprint.Name) ? "Current" : sprint.Name, mine)));
            return body;
        }), loading: loading));

        var orgRoot = app.Config.AzureDevOps?.BaseUrl?.TrimEnd('/');
        var workRows = mine
            .Select(w => new ListRow($"#{w.Id}  {w.Title}", $"{w.State} · {w.WorkItemType}",
                orgRoot == null ? null : () => OpenUrl($"{orgRoot}/_workitems/edit/{w.Id}")))
            .ToList();
        cards.Add(WidgetCards.Card("Work Items List", WidgetCards.List("Work Items", workRows), units: 2));

        var issueRows = (_issues ?? new())
            .Select(i => new ListRow($"#{i.Number}  {i.Title}", i.Repository, () => OpenUrl(i.WebUrl)))
            .ToList();
        cards.Add(WidgetCards.Card("GitHub Issues", WidgetCards.List("GitHub Issues", issueRows), units: 2, loading: _loadingIssues));

        return cards;
    }

    // MARK: - Devices

    /// <summary>Each platform's own colour, so a platform keeps it whatever its rank.</summary>
    private static readonly Dictionary<string, Color> PlatformColors = new()
    {
        ["Macintosh"] = P.Orange,
        ["Windows"] = P.Blue,
        ["iOS/iPadOS"] = P.Purple,
        ["Android"] = P.Green,
        ["Linux"] = P.Teal,
        ["ChromeOS"] = P.Brown,
    };

    private static readonly Color[] PlatformFallbackColors = { P.Blue, P.Purple, P.Orange, P.Teal, P.Brown, P.Gray };

    /// <summary>The platform name the widgets show for an Intune operating system.</summary>
    internal static string PlatformLabel(string? operatingSystem) => operatingSystem switch
    {
        "macOS" => "Macintosh",
        "iOS" or "iPadOS" => "iOS/iPadOS",
        _ => operatingSystem ?? "",
    };

    /// <summary>Devices per platform, most first, top six, each with its fixed colour.</summary>
    internal static List<(ChartSlice Slice, Color Color)> PlatformBreakdown(IEnumerable<IntuneDevice> devices) => devices
        .GroupBy(d => PlatformLabel(d.OperatingSystem))
        .Where(g => g.Key.Length > 0)
        .Select(g => new ChartSlice(g.Key, g.Count()))
        .OrderByDescending(s => s.Value)
        .Take(6)
        .Select((s, i) => (s, PlatformColors.TryGetValue(s.Label, out var c) ? c : PlatformFallbackColors[i % PlatformFallbackColors.Length]))
        .ToList();

    /// <summary>Non-compliant is Intune's own "noncompliant"; every other state counts as compliant, as on the Mac.</summary>
    internal static bool IsNonCompliant(IntuneDevice device) =>
        string.Equals(device.ComplianceState, "noncompliant", StringComparison.OrdinalIgnoreCase);

    /// <summary>Compliant and Non-Compliant, leaving out a side with no devices.</summary>
    internal static List<(ChartSlice Slice, Color Color)> ComplianceBreakdown(IReadOnlyCollection<IntuneDevice> devices)
    {
        var nonCompliant = devices.Count(IsNonCompliant);
        return new List<(ChartSlice, Color)>
        {
            (new ChartSlice("Compliant", devices.Count - nonCompliant), P.Green),
            (new ChartSlice("Non-Compliant", nonCompliant), P.Orange),
        }.Where(x => x.Item1.Value > 0).ToList();
    }

    private static List<ErrorSummary>? _errors;
    private static int? _reportMateDevices;
    private static bool _loadingErrors;

    /// <summary>Errors per category, summed over the devices each error hits, top eight.</summary>
    internal static List<ChartSlice> ErrorCategorySlices(IEnumerable<ErrorSummary> errors) => errors
        .GroupBy(e => e.Category)
        .Select(g => new ChartSlice(CategoryLabel(g.Key), g.Sum(e => e.DeviceCount)))
        .OrderByDescending(s => s.Value)
        .Take(8)
        .ToList();

    private static List<UIElement> Devices(App app, Action<string, string> filter)
    {
        var devices = app.CachedDevices;
        var loading = app.IsCacheLoading("Devices");
        var waiting = loading && devices.Count == 0;

        var cards = new List<UIElement>
        {
            WidgetCards.KpiStack(new[]
            {
                new KpiTile("Managed Devices", devices.Count.ToString("N0"), "", P.Hex(P.Blue), waiting),
                new KpiTile("Non-Compliant", devices.Count(IsNonCompliant).ToString("N0"), "", P.Hex(P.Orange), waiting,
                    () => filter(Category.Compliance, "Non-Compliant")),
            }),
        };

        var platforms = PlatformBreakdown(devices);
        cards.Add(WidgetCards.Card("Platform Distribution",
            WidgetCards.ChartOr(platforms.Count > 0, loading, "No device data",
                () => WidgetCards.Treemap(platforms.Select(p => p.Slice).ToList(), p => filter(Category.Platform, p),
                    platforms.Select(p => p.Color).ToList())),
            loading: loading));

        var compliance = ComplianceBreakdown(devices);
        cards.Add(WidgetCards.Card("Compliance",
            WidgetCards.ChartOr(compliance.Count > 0, loading, "No device data",
                () => WidgetCards.Donut(compliance.Select(c => c.Slice).ToList(), c => filter(Category.Compliance, c),
                    compliance.Select(c => P.Sk(c.Color)).ToList())),
            loading: loading));

        // Errors by Category only when ReportMate is configured.
        if (app.ReportMateService is { } reportMate)
        {
            if (_errors == null && !_loadingErrors)
            {
                _loadingErrors = true;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var devicesTask = reportMate.GetDevicesAsync();
                        var errorsTask = reportMate.GetErrorsByItemAsync(false);
                        await Task.WhenAll(devicesTask, errorsTask);
                        _reportMateDevices = devicesTask.Result.Count;
                        _errors = errorsTask.Result;
                    }
                    catch (Exception ex) { Log.Debug(ex, "[widgets] ReportMate errors unavailable"); _errors = new(); }
                    finally
                    {
                        _loadingErrors = false;
                        Application.Current.Dispatcher.Invoke(() => app.NotifyCacheChanged("Devices"));
                    }
                });
            }

            var errors = _errors ?? new();
            var cats = ErrorCategorySlices(errors);
            var errorCount = errors.Count;
            var stats = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 6),
                Children =
                {
                    MiniStat("Managed", (_reportMateDevices ?? 0).ToString("N0"), P.Blue),
                    MiniStat("Errors", errorCount.ToString("N0"), errorCount > 0 ? P.Orange : P.Green),
                },
            };
            var body = new System.Windows.Controls.StackPanel
            {
                Children =
                {
                    stats,
                    WidgetCards.ChartOr(cats.Count > 0, _loadingErrors, "No errors found",
                        () => WidgetCards.HorizontalBars(cats, _ => P.Orange)),
                },
            };
            cards.Add(WidgetCards.Card("Errors by Category", body, loading: _loadingErrors));
        }

        return cards;
    }

    /// <summary>A small coloured figure over its label.</summary>
    private static UIElement MiniStat(string label, string value, Color color) => new System.Windows.Controls.StackPanel
    {
        Margin = new Thickness(0, 0, 12, 0),
        Children =
        {
            new System.Windows.Controls.TextBlock
            {
                Text = value, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(color),
            },
            new System.Windows.Controls.TextBlock
            {
                Text = label, FontSize = 10,
                Foreground = (Brush)Application.Current.FindResource("SystemControlForegroundBaseMediumBrush"),
            },
        },
    };

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

    private static readonly Color[] AssetCategoryColors = { P.Orange, P.Blue, P.Purple, P.Teal, P.Green, P.Pink, P.Brown, P.Indigo };
    private static readonly Color[] AssetStatusColors = { P.Green, P.Blue, P.Orange, P.Purple, P.Gray };

    /// <summary>Assets per category, most first, top eight.</summary>
    internal static List<ChartSlice> AssetCategorySlices(IEnumerable<SnipeAsset> assets) => assets
        .GroupBy(a => a.Category?.Name ?? "Uncategorized")
        .Select(g => new ChartSlice(g.Key, g.Count()))
        .OrderByDescending(s => s.Value)
        .Take(8)
        .ToList();

    /// <summary>
    /// The status type an asset is counted under by the Asset Status widget
    /// and filtered by in the Status Type filter: Snipe-IT's status meta
    /// (deployed, deployable, pending, archived…), else the status name, else
    /// "Unknown". Both read this, so a wedge always finds its assets — the
    /// Mac's rule.
    /// </summary>
    internal static string AssetStatusType(SnipeAsset asset) =>
        !string.IsNullOrEmpty(asset.StatusLabel?.StatusMeta) ? asset.StatusLabel.StatusMeta
        : !string.IsNullOrEmpty(asset.StatusLabel?.Name) ? asset.StatusLabel.Name
        : "Unknown";

    /// <summary>The Status Type filter's value for an asset: its status type, capitalised for the list.</summary>
    internal static string AssetStatusTypeFilterValue(SnipeAsset asset) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(AssetStatusType(asset).ToLowerInvariant());

    /// <summary>
    /// The Status filter's value for an asset: its own status name ("Ready to
    /// Deploy", "In Repair") exactly as Snipe-IT names it, or null when it has
    /// none. Several names can share one status type, so this is the finer of
    /// the two filters.
    /// </summary>
    internal static string? AssetStatusNameFilterValue(SnipeAsset asset) =>
        string.IsNullOrWhiteSpace(asset.StatusLabel?.Name) ? null : asset.StatusLabel.Name.Trim();

    /// <summary>
    /// Assets per status type (see <see cref="AssetStatusType"/>), top five,
    /// each labelled "Status (count)" — the Mac's grouping.
    /// </summary>
    internal static List<ChartSlice> AssetStatusSlices(IEnumerable<SnipeAsset> assets) => assets
        .GroupBy(AssetStatusType)
        .Select(g => (Status: g.Key, Count: g.Count()))
        .OrderByDescending(s => s.Count)
        .Take(5)
        .Select(s => new ChartSlice($"{s.Status} ({s.Count})", s.Count))
        .ToList();

    /// <summary>
    /// The few words a widget or the asset count shows when the assets could
    /// not be loaded: a token that could not be had is a failed sign-in, which
    /// the operator fixes differently from Snipe-IT itself failing.
    /// </summary>
    internal static string AssetsFailureHeadline(string? reason) =>
        reason is null ? "No asset data"
        : reason.StartsWith("Sign-in failed", StringComparison.OrdinalIgnoreCase)
          || reason.Contains("refused the sign-in", StringComparison.OrdinalIgnoreCase) ? "Sign-in failed"
        : "Could not load assets";

    private static List<UIElement> Inventory(App app, Action<string, string> filter)
    {
        var assets = app.CachedAssets;
        var empty = AssetsFailureHeadline(app.AssetsLoadError);
        var loading = app.IsCacheLoading("Assets");
        var waiting = loading && assets.Count == 0;

        var cards = new List<UIElement>
        {
            WidgetCards.KpiStack(new[]
            {
                new KpiTile("Assets", assets.Count.ToString("N0"), "", P.Hex(P.Orange), waiting),
                new KpiTile("Deployed", assets.Count(a =>
                    a.StatusLabel?.StatusMeta?.Equals("deployed", StringComparison.OrdinalIgnoreCase) == true).ToString("N0"), "", P.Hex(P.Green), waiting),
                new KpiTile("Unassigned", assets.Count(a => a.AssignedTo == null).ToString("N0"), "", P.Hex(P.Blue), waiting),
            }),
        };

        var categories = AssetCategorySlices(assets);
        cards.Add(WidgetCards.Card("Assets by Category",
            WidgetCards.ChartOr(categories.Count > 0, loading, empty,
                () => WidgetCards.HorizontalBars(categories, WidgetCards.ByIndex(categories, AssetCategoryColors),
                    c => filter(Category.AssetCategory, c))),
            loading: loading));

        var statuses = AssetStatusSlices(assets);
        cards.Add(WidgetCards.Card("Asset Status",
            WidgetCards.ChartOr(statuses.Count > 0, loading, empty,
                () => WidgetCards.Donut(statuses, s => filter(Category.StatusType, s),
                    statuses.Select((_, i) => P.Sk(AssetStatusColors[i % AssetStatusColors.Length])).ToList())),
            loading: loading));

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

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "[widgets] Could not open {Url}", url); }
    }
}
