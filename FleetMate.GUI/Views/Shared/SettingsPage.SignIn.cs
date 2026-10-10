using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Models;
using FleetMate.Core.Services;
using Serilog;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// The sign-in buttons on the Authentication cards, matching the macOS
/// client. az login and gh auth login sit on their provider's card, with a
/// Re-check that re-reads the sign-in and every system under it. Each system
/// row keeps its own Sign Out or Retry SSO and a Re-check, because sign-ins
/// finish outside the app. Retry SSO is always the silent path; no card opens
/// a sign-in window.
/// </summary>
public partial class SettingsPage
{
    private readonly Dictionary<AuthSystemId, CliSignIn.Outcome> _signInResults = new();
    private readonly Dictionary<CredentialProvider, CliSignIn.Outcome> _cliSignInResults = new();
    private AuthSystemId? _busySystem;
    private CredentialProvider? _busyProvider;

    private static App? CurrentApp => Application.Current as App;

    private FrameworkElement RowActions(AuthSystemId id, AuthDisplayStatus status)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (_busySystem == id)
        {
            panel.Children.Add(Busy());
            return panel;
        }

        var signedIn = status.Kind == AuthDisplayKind.Valid;
        var elevated = AuthProviderGrouping.ElevationDomain(id, GraphElevated) != null;
        switch (id)
        {
            case AuthSystemId.DevOps:
                if (signedIn) panel.Children.Add(Action("Sign Out", () => { CurrentApp?.SignOutDevOpsSso(); return Task.CompletedTask; }));
                else panel.Children.Add(Action("Retry SSO", () => CurrentApp?.AttemptSilentDevOpsSsoAsync() ?? Task.CompletedTask, accent: true));
                break;
            case AuthSystemId.Tdx:
                if (signedIn) panel.Children.Add(Action("Sign Out", () => { CurrentApp?.SignOutTdxSso(); return Task.CompletedTask; }));
                else panel.Children.Add(Action("Retry SSO", () => CurrentApp?.AttemptSilentTdxSsoAsync() ?? Task.CompletedTask, accent: true));
                break;
            case AuthSystemId.Snipe:
            case AuthSystemId.Intune or AuthSystemId.Graph or AuthSystemId.Entra when !elevated:
                // These ride the shared Entra bearer: retry asks the broker again.
                if (!signedIn && !status.IsChecking)
                    panel.Children.Add(Action("Retry SSO", () =>
                    {
                        EntraTokenSource.Shared?.Invalidate();
                        return Task.CompletedTask;
                    }, accent: true));
                break;
        }
        panel.Children.Add(RecheckButton("Re-check status", () => Task.CompletedTask));
        return panel;

        Button Action(string label, Func<Task> run, bool accent = false) => ActionButton(label, accent, async () =>
        {
            _busySystem = id;
            BuildAuthCards();
            try { await run(); }
            catch (Exception ex) { Log.Warning(ex, "[settings] {System} sign-in action failed", id); }
            await RecheckAsync(id);
            _busySystem = null;
            BuildAuthCards();
        });

        Button RecheckButton(string name, Func<Task> run)
        {
            var button = Action("\uE72C", run);
            button.FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            button.ToolTip = name;
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            return button;
        }
    }

    private FrameworkElement ProviderActions(App app, CredentialProvider provider, IReadOnlyList<AuthRow> rows)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (_busyProvider == provider)
        {
            panel.Children.Add(Busy());
            return panel;
        }

        var auth = app.AuthManager;
        switch (provider)
        {
            case CredentialProvider.AzureCli:
            {
                var signedOut = auth.CliAccountsChecked && auth.AzAccount == null;
                var az = Action(signedOut ? "az login" : "Switch Account…", RunAzLogin, accent: signedOut);
                az.ToolTip = CliSignIn.AzLoginCommandDescription(app.Config.Graph?.TenantId);
                panel.Children.Add(az);
                break;
            }
            case CredentialProvider.GitHubCli when auth.CliAccountsChecked && auth.GhAccount == null:
            {
                var gh = Action("gh auth login", RunGhLogin, accent: true);
                gh.ToolTip = $"Opens a console running {CliSignIn.GhLoginCommand} to finish the GitHub browser sign-in";
                panel.Children.Add(gh);
                break;
            }
        }

        var recheck = Action("\uE72C", () =>
        {
            _cliSignInResults.Remove(provider);
            return Task.CompletedTask;
        });
        recheck.FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        recheck.ToolTip = "Re-check the sign-in and every system that uses it";
        System.Windows.Automation.AutomationProperties.SetName(recheck, "Re-check the sign-in and every system that uses it");
        panel.Children.Add(recheck);
        return panel;

        // Every provider action ends by re-reading the sign-in and re-probing
        // each system under it, so the card shows the state the action left.
        Button Action(string label, Func<Task> run, bool accent = false) => ActionButton(label, accent, async () =>
        {
            _busyProvider = provider;
            BuildAuthCards();
            try { await run(); }
            catch (Exception ex) { Log.Warning(ex, "[settings] {Provider} sign-in action failed", provider); }
            await RecheckProviderAsync(rows);
            _busyProvider = null;
            BuildAuthCards();
        });
    }

    private Button ActionButton(string label, bool accent, Func<Task> onClick)
    {
        var button = new Button
        {
            Content = label,
            FontSize = 11,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (accent) button.Style = (Style)FindResource("AccentButtonStyle");
        button.Click += async (_, _) => await onClick();
        return button;
    }

    private static FrameworkElement Busy() =>
        new ModernWpf.Controls.ProgressRing { IsActive = true, Width = 16, Height = 16, Margin = new Thickness(8, 0, 0, 0) };

    private async Task RecheckAsync(AuthSystemId id)
    {
        if (CurrentApp is not { } app) return;
        try
        {
            await app.AuthManager.RecheckAsync(id, app.GraphService, app.TdxService, app.SnipeService, app.DevOpsService);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[settings] Re-check of {System} failed", id);
        }
    }

    /// <summary>Re-read the CLI accounts, then re-probe each system on the card.</summary>
    private async Task RecheckProviderAsync(IReadOnlyList<AuthRow> rows)
    {
        if (CurrentApp is not { } app) return;
        await app.AuthManager.ProbeCliAccountsAsync();
        var ids = rows.Select(r => r.Id).OfType<AuthSystemId>().ToList();
        await Task.WhenAll(ids.Select(RecheckAsync));
        if (rows.Any(r => r.Id == null && r.Name == "ReportMate")) await CheckReportMateAsync(app.Config);
    }

    private async Task RunAzLogin()
    {
        var outcome = await CliSignIn.AzLoginAsync(CurrentApp?.Config.Graph?.TenantId);
        _cliSignInResults[CredentialProvider.AzureCli] = outcome;
        if (outcome.Succeeded && CurrentApp is { } app)
        {
            // A new az session can bring every az-backed system back, and the
            // Azure DevOps fallback with it.
            EntraTokenSource.Shared?.Invalidate();
            await app.AttemptSilentDevOpsSsoAsync();
        }
    }

    /// <summary>
    /// The console hand-off means FleetMate never sees gh finish, so watch for
    /// the session to appear, as the macOS client does, for up to three minutes.
    /// </summary>
    private Task RunGhLogin()
    {
        var outcome = CliSignIn.GhLoginInConsole();
        _cliSignInResults[CredentialProvider.GitHubCli] = outcome;
        if (!outcome.Succeeded) return Task.CompletedTask;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            for (var i = 0; i < 36; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (!IsLoaded || CurrentApp is not { } app) return;
                await app.AuthManager.ProbeCliAccountsAsync();
                if (app.AuthManager.GhAccount != null)
                {
                    _cliSignInResults.Remove(CredentialProvider.GitHubCli);
                    await RecheckAsync(AuthSystemId.GitHub);
                    BuildAuthCards();
                    return;
                }
            }
        });
        return Task.CompletedTask;
    }
}
