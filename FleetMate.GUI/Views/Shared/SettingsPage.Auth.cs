using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FleetMate.Core.Config;
using FleetMate.Core.Models;
using FleetMate.Core.Services;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// Settings › Authentication, grouped by where each credential comes from:
/// one card per provider (single sign-on, the az sign-in, the gh sign-in,
/// stored credentials), listing the systems that depend on it. One expired
/// az session reads as one problem on one card, with the systems it takes
/// down listed underneath, instead of the same failure repeated per system.
/// Every row and card uses one status model (<see cref="AuthDisplayStatus"/>),
/// nothing is red, and only the status pill is coloured.
/// </summary>
public partial class SettingsPage
{
    private static readonly bool GraphElevated = AuthProviderGrouping.GraphUsesElevation();
    private readonly HashSet<GraphDomain> _startingElevation = new();
    private AuthDisplayStatus _reportMateStatus = AuthDisplayStatus.Checking();
    private bool _authRebuildQueued;

    /// <summary>One system's row on a provider card.</summary>
    private sealed record AuthRow(
        AuthSystemId? Id,
        string Name,
        string Glyph,
        string Method,
        AuthDisplayStatus Status,
        IReadOnlyList<(string Label, string? Value)> Details,
        GraphDomain? Domain = null,
        DateTime? LastChecked = null);

    // ── Live updates ────────────────────────────────────────────────────────

    /// <summary>Repaint as each probe lands, and while elevation sessions start.</summary>
    private void AttachAuthUpdates()
    {
        if (CurrentApp is { } app)
        {
            app.AuthManager.PropertyChanged += OnAuthChanged;
            if (app.ElevationMonitor is { } monitor) monitor.Changed += QueueAuthRebuild;
        }
        ElevationSession.CreatingChanged += OnElevationCreating;
    }

    private void DetachAuthUpdates()
    {
        if (CurrentApp is { } app)
        {
            app.AuthManager.PropertyChanged -= OnAuthChanged;
            if (app.ElevationMonitor is { } monitor) monitor.Changed -= QueueAuthRebuild;
        }
        ElevationSession.CreatingChanged -= OnElevationCreating;
    }

    private void OnAuthChanged(object? sender, PropertyChangedEventArgs e) => QueueAuthRebuild();

    private void OnElevationCreating(GraphDomain domain, bool creating)
    {
        lock (_startingElevation)
        {
            if (creating) _startingElevation.Add(domain);
            else _startingElevation.Remove(domain);
        }
        QueueAuthRebuild();
    }

    /// <summary>Coalesce a burst of probe updates into one repaint.</summary>
    private void QueueAuthRebuild()
    {
        if (_authRebuildQueued) return;
        _authRebuildQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _authRebuildQueued = false;
            if (IsLoaded) BuildAuthCards();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private async void OnRefreshAuthClicked(object sender, RoutedEventArgs e) => await RefreshAuthCardsAsync();

    /// <summary>
    /// Verify every row: a row is never left at Configured waiting for a
    /// Re-check. A probe already running (the one at launch) is joined, not
    /// repeated.
    /// </summary>
    private async Task RefreshAuthCardsAsync()
    {
        if (CurrentApp is not { } app) return;
        BuildAuthCards();
        try
        {
            await Task.WhenAll(
                app.AuthManager.ProbeAllAsync(app.GraphService, app.TdxService, app.SnipeService, app.DevOpsService),
                CheckReportMateAsync(app.Config));
        }
        catch
        {
            // Individual probes own their error states; keep rendering the rest.
        }
        BuildAuthCards();
    }

    /// <summary>
    /// ReportMate has no probe of its own in the auth manager: a token for its
    /// audience from the broker is what its calls ride, so that is the check.
    /// </summary>
    private async Task CheckReportMateAsync(FleetMateConfig config)
    {
        if (AppEdition.Current.IsTicketsOnly || string.IsNullOrEmpty(config.ReportMateUrl) || !config.ReportMateUsesOidc)
            return;
        _reportMateStatus = AuthDisplayStatus.Checking();
        try
        {
            var source = EntraTokenSource.Shared ?? EntraTokenSource.Configure(config.Graph?.TenantId, config.EntraClientId);
            var token = await source.GetTokenAsync(config.ReportMateOidcAudience!);
            _reportMateStatus = string.IsNullOrEmpty(token) ? AuthDisplayStatus.NeedsSignIn : AuthDisplayStatus.Valid;
        }
        catch (EntraTokenException ex)
        {
            _reportMateStatus = AuthDisplayStatus.Failed(ex.Message);
        }
        catch (Exception ex)
        {
            _reportMateStatus = AuthDisplayStatus.Failed(ex.Message);
        }
    }

    // ── Cards ───────────────────────────────────────────────────────────────

    private void BuildAuthCards()
    {
        AuthCardsPanel.Children.Clear();
        if (CurrentApp is not { } app) return;
        var config = app.Config;
        var auth = app.AuthManager;

        // Intune and Microsoft Graph share one probe and one session, so the
        // Intune row stands for both.
        var ids = auth.ConfiguredSystems.Select(s => s.SystemId)
            .Where(id => id != AuthSystemId.Graph || !auth.Systems.ContainsKey(AuthSystemId.Intune));
        var groups = AuthProviderGrouping.Group(ids, config, GraphElevated)
            .ToDictionary(g => g.Provider, g => g.Systems);

        foreach (var provider in Enum.GetValues<CredentialProvider>())
        {
            var rows = (groups.GetValueOrDefault(provider) ?? Array.Empty<AuthSystemId>())
                .Select(id => RowFor(app, id))
                .Concat(UnconfiguredRows(provider, config))
                .ToList();
            // The CLI sign-ins are cards even with nothing under them: az is
            // the trust anchor for elevation, gh is how Development reads.
            var cliCard = !AppEdition.Current.IsTicketsOnly && provider is CredentialProvider.AzureCli or CredentialProvider.GitHubCli;
            if (rows.Count == 0 && !cliCard) continue;
            AuthCardsPanel.Children.Add(ProviderCard(app, provider, rows));
        }
    }

    /// <summary>Systems that are not set up yet, listed so a missing setting is visible.</summary>
    private IEnumerable<AuthRow> UnconfiguredRows(CredentialProvider provider, FleetMateConfig config)
    {
        if (AppEdition.Current.IsTicketsOnly) yield break;

        var graphProvider = GraphElevated ? CredentialProvider.AzureCli : CredentialProvider.SingleSignOn;
        if (provider == graphProvider && string.IsNullOrEmpty(config.Graph?.TenantId))
            yield return new AuthRow(null, "Microsoft Graph", AuthSystemId.Graph.Icon(),
                AuthProviderGrouping.MethodDescription(AuthSystemId.Graph, config, GraphElevated),
                AuthDisplayStatus.NotConfigured,
                new (string, string?)[]
                {
                    ("Needs", "Tenant ID (Microsoft Graph, above), or GraphTenantId in managed settings"),
                    ("Used by", "Devices, Identity and Enrollment"),
                });

        if (provider == CredentialProvider.SingleSignOn)
        {
            var hasUrl = !string.IsNullOrEmpty(config.ReportMateUrl);
            var ready = hasUrl && config.ReportMateUsesOidc;
            yield return new AuthRow(null, "ReportMate", "\uE9D9", "Brokered bearer",
                ready ? _reportMateStatus : AuthDisplayStatus.NotConfigured,
                ready
                    ? new (string, string?)[]
                    {
                        ("API URL", config.ReportMateUrl),
                        ("Audience", ShortId(config.ReportMateOidcAudience)),
                    }
                    : new (string, string?)[]
                    {
                        ("API URL", hasUrl ? config.ReportMateUrl : null),
                        ("Needs", ReportMateNeeds(hasUrl)),
                        ("Used by", "Reporting"),
                    });
        }
    }

    /// <summary>The settings Reporting still lacks, by the names they are set under.</summary>
    internal static string ReportMateNeeds(bool hasUrl) => hasUrl
        ? "ReportMateOidcAudience in managed settings"
        : "API URL (ReportMate, above) and ReportMateOidcAudience in managed settings";

    private AuthRow RowFor(App app, AuthSystemId id)
    {
        var config = app.Config;
        var system = app.AuthManager.Systems[id];
        var status = AuthDisplayStatus.From(system.State, system.LastChecked);
        var domain = AuthProviderGrouping.ElevationDomain(id, GraphElevated);
        if (status.IsChecking && domain is { } d && IsStartingElevation(app, d))
            status = AuthDisplayStatus.Checking("Starting elevation session…");

        var details = new List<(string, string?)>();
        switch (id)
        {
            case AuthSystemId.Intune or AuthSystemId.Graph or AuthSystemId.Entra:
                details.Add(("Tenant ID", ShortId(config.Graph?.TenantId)));
                if (domain is { } elevationDomain)
                {
                    if (config.Elevation is { IsConfigured: true } elevation)
                        details.Add(("Runs as", $"{elevation.IdentityPrefix}{elevationDomain.DomainName()} (managed identity)"));
                    details.Add(("Elevation session", ElevationSessionLine(app, elevationDomain)));
                }
                else
                {
                    details.Add(("Token source", "Windows broker (your sign-in)"));
                }
                break;
            case AuthSystemId.DevOps:
                details.Add(("Organization", config.AzureDevOps?.Organization ?? "not set — required"));
                details.Add(("Project", config.AzureDevOps?.Project ?? (config.AzureDevOps?.Organization != null ? "auto-discovered" : null)));
                if (system.State.Kind == AuthStateKind.Valid) details.Add(("Signed in as", system.User ?? system.State.User));
                break;
            case AuthSystemId.Tdx:
                details.Add(("Base URL", config.Tdx?.BaseUrl));
                if (system.State.Kind == AuthStateKind.Valid) details.Add(("Acting as", system.User ?? system.State.User));
                if (string.IsNullOrEmpty(config.Tdx?.BaseUrl))
                    details.Add(("Needs", "Base URL and Ticketing App ID (TeamDynamix, above), or TdxBaseUrl in managed settings"));
                break;
            case AuthSystemId.Snipe:
                details.Add(("Instance URL", config.SnipeUrl));
                if (config.SnipeUsesOidc) details.Add(("Audience", ShortId(config.SnipeOidcAudience)));
                else details.Add(("Needs", "SnipeOidcAudience in managed settings, for SSO"));
                break;
            case AuthSystemId.GitHub:
                details.Add(("Organization", config.Tasks?.Providers?.GitHub?.Organization));
                details.Add(("Project #", config.Tasks?.Providers?.GitHub?.ProjectNumber?.ToString()));
                break;
            case AuthSystemId.Gitea:
                details.Add(("Instance URL", config.Tasks?.Providers?.Gitea?.Url));
                break;
        }

        var name = id switch
        {
            // On a card of its own the Intune row speaks for Graph as a whole.
            AuthSystemId.Intune => "Intune (Microsoft Graph)",
            _ => id.DisplayName(),
        };
        return new AuthRow(id, name, id.Icon(), AuthProviderGrouping.MethodDescription(id, config, GraphElevated),
            status, details, domain, system.LastChecked);
    }

    private bool IsStartingElevation(App app, GraphDomain domain)
    {
        lock (_startingElevation)
            if (_startingElevation.Contains(domain)) return true;
        return app.ElevationMonitor?.StateOf(domain) == ElevationSessionState.Starting;
    }

    /// <summary>The elevation session behind a row: idle, starting, ready (with its expiry), stopped.</summary>
    private string ElevationSessionLine(App app, GraphDomain domain)
    {
        if (IsStartingElevation(app, domain)) return "Starting (a cold start takes about a minute)";
        if (app.ElevationMonitor is not { } monitor) return "Unknown";
        return monitor.StateOf(domain) switch
        {
            ElevationSessionState.Ready when monitor.ExpiresOf(domain) is { } expires =>
                $"Ready until {expires.ToLocalTime():t}",
            ElevationSessionState.Ready => "Ready",
            ElevationSessionState.Starting => "Starting (a cold start takes about a minute)",
            ElevationSessionState.None => "Idle. Starts on first use.",
            ElevationSessionState.Expired => "Stopped. Restarts on next use.",
            ElevationSessionState.Off => app.Config.Elevation is { IsConfigured: false } ? "Not set up" : "Off",
            _ => monitor.ErrorOf(domain) is { } error ? $"Unknown: {error}" : "Unknown",
        };
    }

    /// <summary>The CLI account's own status, for the providers that have one.</summary>
    private static AuthDisplayStatus? AccountStatus(AuthManager auth, CredentialProvider provider)
    {
        switch (provider)
        {
            case CredentialProvider.AzureCli:
                if (!auth.CliAccountsChecked) return AuthDisplayStatus.Checking();
                if (auth.AzAccount is not { } az) return AuthDisplayStatus.NeedsSignIn;
                return az.IsServicePrincipal
                    ? AuthDisplayStatus.Failed("Signed in as a service principal, so actions will not show as you.")
                    : AuthDisplayStatus.Valid;
            case CredentialProvider.GitHubCli:
                if (!auth.CliAccountsChecked) return AuthDisplayStatus.Checking();
                return auth.GhAccount == null ? AuthDisplayStatus.NeedsSignIn : AuthDisplayStatus.Valid;
            default:
                return null;
        }
    }

    private Border ProviderCard(App app, CredentialProvider provider, IReadOnlyList<AuthRow> rows)
    {
        var auth = app.AuthManager;
        var account = AccountStatus(auth, provider);
        var members = rows.Select(r => r.Status).ToList();
        if (account != null) members.Insert(0, account);
        // The gh sign-in has no state beyond the account, so its card lists
        // what uses it instead of repeating a row.
        var showRows = provider != CredentialProvider.GitHubCli;
        var summary = provider == CredentialProvider.GitHubCli && account != null
            ? (account.Label, account.Tone)
            : AuthDisplayStatus.Summary(members);

        var card = new Border { Style = (Style)FindResource("CardStyle"), Margin = new Thickness(0, 0, 0, 12) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(card, $"AuthProvider{provider}");
        var stack = new StackPanel();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Glyph(provider.Glyph(), 20);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 0, 0);
        header.Children.Add(icon);

        var body = new StackPanel();
        Grid.SetColumn(body, 1);
        var titleRow = new DockPanel();
        var actions = ProviderActions(app, provider, rows);
        DockPanel.SetDock(actions, Dock.Right);
        titleRow.Children.Add(actions);
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = provider.Title(), FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(Pill(summary.Item1, summary.Item2, summary.Item2 == AuthDisplayTone.Neutral));
        titleRow.Children.Add(title);
        body.Children.Add(titleRow);
        body.Children.Add(Caption(provider.Summary(), top: 3));
        foreach (var (label, value) in ProviderDetails(app, provider))
            AddDetail(body, label, value);
        if (_cliSignInResults.TryGetValue(provider, out var outcome))
            AddDetail(body, outcome.Succeeded ? "Sign-in" : "Sign-in error", outcome.Message);
        header.Children.Add(body);
        stack.Children.Add(header);

        if (showRows && rows.Count > 0)
        {
            var list = new StackPanel();
            for (var i = 0; i < rows.Count; i++)
            {
                if (i > 0) list.Children.Add(new Separator { Margin = new Thickness(34, 0, 0, 0) });
                list.Children.Add(SystemRow(rows[i]));
            }
            var frame = new Border
            {
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 10, 0, 0),
                Child = list,
            };
            frame.SetResourceReference(Border.BackgroundProperty, "SystemControlBackgroundBaseLowBrush");
            stack.Children.Add(frame);
        }

        card.Child = stack;
        return card;
    }

    private IEnumerable<(string Label, string? Value)> ProviderDetails(App app, CredentialProvider provider)
    {
        var auth = app.AuthManager;
        switch (provider)
        {
            case CredentialProvider.AzureCli:
                if (auth.AzAccount is { } az)
                {
                    yield return ("Signed in as", az.User);
                    yield return ("Tenant", ShortId(az.TenantId));
                    yield return ("Subscription", az.Subscription);
                }
                else if (auth.CliAccountsChecked)
                {
                    yield return ("Status", "Not signed in. Every system below needs this.");
                }
                break;
            case CredentialProvider.GitHubCli:
                if (auth.GhAccount is { } gh) yield return ("Signed in as", gh.User);
                else if (auth.CliAccountsChecked) yield return ("Status", "Not signed in");
                var uses = new List<string> { "Development (repositories, pull requests, Actions)" };
                if (app.Config.Tasks?.Providers?.GitHub is { Enabled: true }) uses.Add("Projects (issues)");
                yield return ("Used by", string.Join(", ", uses));
                yield return ("Organization", app.Config.Tasks?.Providers?.GitHub?.Organization);
                break;
        }
    }

    private FrameworkElement SystemRow(AuthRow row)
    {
        var grid = new Grid { Margin = new Thickness(10, 9, 10, 9) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Glyph(row.Glyph, 14);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 0, 0);
        grid.Children.Add(icon);

        var body = new StackPanel();
        Grid.SetColumn(body, 1);
        var head = new DockPanel();
        if (row.Id is { } id && row.Status.Kind != AuthDisplayKind.NotConfigured)
        {
            var actions = RowActions(id, row.Status);
            DockPanel.SetDock(actions, Dock.Right);
            head.Children.Add(actions);
        }
        var title = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = row.Name, FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var method = Caption(row.Method, top: 0);
        method.Margin = new Thickness(8, 0, 0, 0);
        method.VerticalAlignment = VerticalAlignment.Center;
        title.Children.Add(method);
        title.Children.Add(Pill(row.Status.Label, row.Status.Tone, row.Status.IsChecking));
        head.Children.Add(title);
        body.Children.Add(head);

        foreach (var (label, value) in row.Details) AddDetail(body, label, value);
        if (row.Status.Kind == AuthDisplayKind.Failed && !string.IsNullOrWhiteSpace(row.Status.Text))
            body.Children.Add(ErrorDetail(row.Status.Text!));
        if (row.Id is { } resultId && _signInResults.TryGetValue(resultId, out var outcome))
            AddDetail(body, outcome.Succeeded ? "Sign-in" : "Sign-in error", outcome.Message);
        if (row.LastChecked is { } checkedAt)
            AddDetail(body, "Last checked", checkedAt.ToString("t"));

        grid.Children.Add(body);
        return grid;
    }

    // ── Pieces ──────────────────────────────────────────────────────────────

    /// <summary>Never red: green when verified, orange only where the person has to act.</summary>
    private Brush ToneBrush(AuthDisplayTone tone) => tone switch
    {
        AuthDisplayTone.Positive => new SolidColorBrush(Color.FromRgb(0x27, 0xae, 0x60)),
        AuthDisplayTone.Attention => new SolidColorBrush(Color.FromRgb(0xe6, 0x7e, 0x22)),
        AuthDisplayTone.Inactive => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
        _ => (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
    };

    /// <summary>The one coloured element on a row or card. Failures carry their detail in the row's Error line.</summary>
    private FrameworkElement Pill(string label, AuthDisplayTone tone, bool spinning)
    {
        var brush = ToneBrush(tone);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (spinning)
            content.Children.Add(new ModernWpf.Controls.ProgressRing { IsActive = true, Width = 10, Height = 10, Margin = new Thickness(0, 0, 5, 0) });
        else
            content.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = brush, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = brush, VerticalAlignment = VerticalAlignment.Center });
        var tint = brush is SolidColorBrush solid ? Color.FromArgb(0x1c, solid.Color.R, solid.Color.G, solid.Color.B) : Colors.Transparent;
        var pill = new Border
        {
            Background = new SolidColorBrush(tint),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = content,
        };
        System.Windows.Automation.AutomationProperties.SetName(pill, label);
        return pill;
    }

    private TextBlock Glyph(string glyph, double size)
    {
        var text = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size };
        text.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        return text;
    }

    private TextBlock Caption(string text, double top = 4)
    {
        var caption = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        return caption;
    }

    private void AddDetail(Panel panel, string label, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lbl = Caption(label, top: 0);
        lbl.TextWrapping = TextWrapping.NoWrap;
        var val = new TextBlock { Text = value, FontSize = 11, FontFamily = new FontFamily("Consolas"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = value };
        Grid.SetColumn(val, 1);
        row.Children.Add(lbl);
        row.Children.Add(val);
        panel.Children.Add(row);
    }

    /// <summary>A failure in full, wrapped, and selectable so it can be copied.</summary>
    private FrameworkElement ErrorDetail(string message)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(Caption("Error", top: 0));
        var text = new TextBox
        {
            Text = message,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            MinHeight = 0,
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            MaxHeight = 80,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        System.Windows.Automation.AutomationProperties.SetName(text, "Error");
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

}
