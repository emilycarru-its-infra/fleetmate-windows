using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Models;
using FleetMate.Core.Services;
using Serilog;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// The sign-in buttons on each Authentication card, matching the macOS
/// client: Sign Out or Retry SSO for the SSO systems, az login on the cards
/// that ride the operator's az session, gh auth login for GitHub, and a
/// Re-check on every card because sign-ins finish outside the app. Retry SSO
/// is always the silent path; no card opens a sign-in window.
/// </summary>
public partial class SettingsPage
{
    private readonly Dictionary<AuthSystemId, CliSignIn.Outcome> _signInResults = new();
    private AuthSystemId? _busySystem;

    private FrameworkElement BuildAuthActions(AuthSystemId id, AuthState state)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (_busySystem == id)
        {
            panel.Children.Add(new ModernWpf.Controls.ProgressRing { IsActive = true, Width = 16, Height = 16, Margin = new Thickness(8, 0, 0, 0) });
            return panel;
        }

        var signedIn = state == AuthState.Valid;
        switch (id)
        {
            case AuthSystemId.DevOps:
                if (signedIn) panel.Children.Add(ActionButton("Sign Out", () => { CurrentApp?.SignOutDevOpsSso(); return Task.CompletedTask; }));
                else panel.Children.Add(ActionButton("Retry SSO", () => CurrentApp?.AttemptSilentDevOpsSsoAsync() ?? Task.CompletedTask, accent: true));
                break;
            case AuthSystemId.Tdx:
                if (signedIn) panel.Children.Add(ActionButton("Sign Out", () => { CurrentApp?.SignOutTdxSso(); return Task.CompletedTask; }));
                else panel.Children.Add(ActionButton("Retry SSO", () => CurrentApp?.AttemptSilentTdxSsoAsync() ?? Task.CompletedTask, accent: true));
                break;
            case AuthSystemId.Graph:
            case AuthSystemId.Snipe:
                // Both ride the shared Entra bearer: retry asks the broker again.
                if (!signedIn)
                    panel.Children.Add(ActionButton("Retry SSO", () =>
                    {
                        EntraTokenSource.Shared?.Invalidate();
                        return Task.CompletedTask;
                    }, accent: true));
                break;
            case AuthSystemId.GitHub:
                if (!signedIn)
                {
                    var gh = ActionButton("gh auth login", () => RunGhLogin(), accent: true);
                    gh.ToolTip = $"Opens a console running {CliSignIn.GhLoginCommand} to finish the GitHub browser sign-in";
                    panel.Children.Add(gh);
                }
                break;
        }

        // az login is the trust anchor for DevOps and for every elevation
        // session Graph uses, so offer it on those cards while signed out.
        if (!signedIn && id is AuthSystemId.DevOps or AuthSystemId.Graph)
        {
            var az = ActionButton("az login", () => RunAzLogin(id));
            az.ToolTip = CliSignIn.AzLoginCommandDescription(CurrentApp?.Config.Graph?.TenantId);
            panel.Children.Add(az);
        }

        var recheck = ActionButton("", () => Task.CompletedTask);
        recheck.FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons");
        recheck.ToolTip = "Re-check status";
        System.Windows.Automation.AutomationProperties.SetName(recheck, "Re-check status");
        panel.Children.Add(recheck);
        return panel;

        Button ActionButton(string label, Func<Task> run, bool accent = false)
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
            // Every action ends with a re-probe of its own card, so the card
            // shows the state the action left rather than the one before it.
            button.Click += async (_, _) =>
            {
                _busySystem = id;
                BuildAuthCards();
                try { await run(); }
                catch (Exception ex) { Log.Warning(ex, "[settings] {System} sign-in action failed", id); }
                await RecheckAsync(id);
                _busySystem = null;
                BuildAuthCards();
            };
            return button;
        }
    }

    private static App? CurrentApp => Application.Current as App;

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

    private async Task RunAzLogin(AuthSystemId id)
    {
        var outcome = await CliSignIn.AzLoginAsync(CurrentApp?.Config.Graph?.TenantId);
        _signInResults[id] = outcome;
        if (outcome.Succeeded && CurrentApp is { } app)
        {
            // A new az session can bring every az-backed card back, not just this one.
            EntraTokenSource.Shared?.Invalidate();
            if (id == AuthSystemId.DevOps) await app.AttemptSilentDevOpsSsoAsync();
        }
    }

    /// <summary>
    /// The console hand-off means FleetMate never sees gh finish, so watch for
    /// the session to appear, as the macOS client does, for up to three minutes.
    /// </summary>
    private Task RunGhLogin()
    {
        var outcome = CliSignIn.GhLoginInConsole();
        _signInResults[AuthSystemId.GitHub] = outcome;
        if (!outcome.Succeeded) return Task.CompletedTask;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            for (var i = 0; i < 36; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (!IsLoaded) return;
                await RecheckAsync(AuthSystemId.GitHub);
                if (CurrentApp?.AuthManager.Systems.GetValueOrDefault(AuthSystemId.GitHub)?.State.Kind == AuthStateKind.Valid)
                {
                    _signInResults.Remove(AuthSystemId.GitHub);
                    BuildAuthCards();
                    return;
                }
            }
        });
        return Task.CompletedTask;
    }
}
