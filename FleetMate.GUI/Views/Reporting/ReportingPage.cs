using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Config;
using FleetMate.Core.Services;
using RmConfig = ReportMate.App.Services.ConfigManager;
using RmApi = ReportMate.App.Services.FleetApiClient;
using RmSetup = ReportMate.App.Services.FleetSetupHints;
using RmDashboard = ReportMate.App.Views.Shared.DashboardView;
using RmChrome = ReportMate.App.Views.Shared.DashboardChrome;
using RmLink = ReportMate.App.Services.DeepLink;

namespace FleetMate.GUI.Views.Reporting;

/// <summary>
/// The Reporting tab: the ReportMate dashboard itself, every page of it, built
/// from the ReportMate app's own source. FleetMate only supplies what it already
/// has -- the API address from its settings and the signed-in user's token -- so
/// the tab is whatever the standalone app is, release for release.
/// </summary>
public sealed partial class ReportingPage : Page
{
    private readonly RmDashboard _dashboard;

    public ReportingPage()
    {
        Title = "Reporting";
        if (Application.Current is App app) Connect(app.Config);

        // Scoped: the dashboard keeps its own palette and styles, which share keys
        // such as CardStyle and NavigationTabStyle with FleetMate's. Host chrome:
        // the toolbar already has the one search field and the tab names the view,
        // so the dashboard draws neither and gives its row to the section tabs.
        _dashboard = new RmDashboard(scopedResources: true, RmChrome.HostProvided);
        Content = _dashboard;
        _searchBox.TextChanged += (_, _) => _dashboard.DeviceSearch = _searchBox.Text;
    }

    /// <summary>Open the view a reportmate:// link names, such as one device's report.</summary>
    public void OpenLink(string url)
    {
        if (RmLink.Parse(url) is { } link) _dashboard.OpenDeepLink(link);
    }

    /// <summary>
    /// Feed the dashboard FleetMate's ReportMate connection. The device's own
    /// ReportMate configuration, the dashboard's settings and policy still win:
    /// these only fill what the device has not set.
    /// </summary>
    internal static void Connect(FleetMateConfig config)
    {
        var url = config.ReportMateUrl;
        // A placeholder URL means ReportMate is not really configured here.
        var usable = !string.IsNullOrWhiteSpace(url)
            && !url.Contains("example", StringComparison.OrdinalIgnoreCase);
        RmConfig.HostDefaults = usable ? c => c.ApiUrl = url! : null;

        var audience = config.ReportMateUsesOidc ? config.ReportMateOidcAudience : null;
        RmApi.BearerTokenProvider = audience is null ? null : async ct =>
        {
            if (EntraTokenSource.Shared is not { } source) return null;
            try
            {
                var token = await source.GetTokenAsync(audience, ct);
                LastSignInError = null;
                return token;
            }
            // No silent token: fall back to the device's own read credential,
            // and keep the reason for the page to show if that cannot read either.
            // The token source has already logged it.
            catch (EntraTokenException ex)
            {
                LastSignInError = ex.Message;
                return null;
            }
        };

        // FleetMate supplies the sign-in, so the setting a fleet page is missing
        // is one of FleetMate's, not the dashboard's read passphrase.
        RmSetup.HostHint = hasUrl => SetupHint(hasUrl, audience, LastSignInError);

        RmConfig.Instance.ReloadSettings();

        // One address for ReportMate: whatever the dashboard resolved, from the
        // device's own configuration or from FleetMate's, is what FleetMate's
        // settings show and its other ReportMate calls use.
        var resolved = RmConfig.Instance.Config.ApiUrl;
        if (!string.IsNullOrWhiteSpace(resolved)) config.ReportMateUrl = resolved;
    }

    /// <summary>Why the last ReportMate token could not be had, or null once one was.</summary>
    internal static string? LastSignInError { get; private set; }

    /// <summary>
    /// The FleetMate setting a fleet page needs, in the words Settings uses, or,
    /// when everything is set but no token could be had, the sign-in failure
    /// itself rather than an empty page.
    /// </summary>
    internal static string SetupHint(bool hasUrl, string? audience, string? signInError = null)
    {
        if (!hasUrl)
            return "Set the ReportMate API URL in FleetMate Settings, or ReportMateUrl in managed settings, "
                 + "and ReportMateOidcAudience in managed settings so FleetMate can sign in to it.";
        if (audience is null)
            return "Set ReportMateOidcAudience in managed settings so FleetMate can sign in to ReportMate.";
        return signInError is not null
            ? $"Sign-in failed: {signInError}. Check the ReportMate card under Settings › Authentication Status."
            : "FleetMate could not get a ReportMate sign-in token. Check the ReportMate card under Settings › Authentication Status.";
    }
}
