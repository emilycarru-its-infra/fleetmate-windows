using System;
using System.Threading.Tasks;
using System.Windows;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace FleetMate.GUI.Views.Projects;

/// <summary>
/// Azure DevOps SSO login window using WebView2 for OAuth2 Authorization Code + PKCE.
/// Delegates PKCE, token exchange, and JWT parsing to Core DevOpsSsoService.
/// </summary>
public partial class DevOpsSsoLoginWindow : Window
{
    private readonly DevOpsSsoService _ssoService;
    private bool _authCompleted;

    /// <summary>
    /// Event raised when authentication completes successfully.
    /// </summary>
    public event EventHandler<DevOpsSsoResult>? AuthenticationCompleted;

    /// <summary>
    /// Event raised when authentication is cancelled.
    /// </summary>
    public event EventHandler? AuthenticationCancelled;

    public DevOpsSsoLoginWindow(DevOpsSsoService ssoService)
    {
        InitializeComponent();
        _ssoService = ssoService;

        Loaded += async (_, _) => await InitializeWebViewAsync();
        Closing += (_, _) =>
        {
            if (!_authCompleted)
            {
                AuthenticationCancelled?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    // ── WebView2 Initialization ─────────────────────────────────────────────

    private async Task InitializeWebViewAsync()
    {
        try
        {
            StatusText.Text = "Initializing WebView2...";
            await WebView.EnsureCoreWebView2Async();

            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            WebView.CoreWebView2.Settings.IsBuiltInErrorPageEnabled = true;

            WebView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

            await StartAuthenticationAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize WebView2 for DevOps SSO");
            ShowError($"Failed to initialize browser: {ex.Message}");
        }
    }

    // ── OAuth2 Authorization Code + PKCE Flow ───────────────────────────────

    private async Task StartAuthenticationAsync()
    {
        try
        {
            StatusText.Text = "Loading Microsoft sign-in...";
            LoadingPanel.Visibility = Visibility.Visible;
            ErrorPanel.Visibility = Visibility.Collapsed;
            WebView.Visibility = Visibility.Collapsed;

            var authorizeUrl = _ssoService.BuildAuthorizeUrl();

            Log.Debug("DevOps SSO: Navigating to authorize URL");
            WebView.CoreWebView2.Navigate(authorizeUrl.AbsoluteUri);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start DevOps SSO authentication");
            ShowError($"Failed to load login page: {ex.Message}");
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        LoadingIndicator.Visibility = Visibility.Visible;

        var uri = new Uri(e.Uri);
        StatusText.Text = $"Navigating to {uri.Host}...";
        Log.Debug("DevOps SSO WebView navigating to: {Uri}", e.Uri);

        // Check if this is the redirect URI with an auth code
        if (DevOpsSsoService.IsRedirectUri(uri))
        {
            e.Cancel = true; // Don't actually navigate to the redirect
            LoadingIndicator.Visibility = Visibility.Collapsed;

            var code = DevOpsSsoService.ExtractCode(uri);
            var error = DevOpsSsoService.ExtractError(uri);

            if (!string.IsNullOrEmpty(code))
            {
                Log.Debug("DevOps SSO: Received authorization code");
                StatusText.Text = "Exchanging authorization code for token...";
                _ = ExchangeCodeForTokenAsync(code);
            }
            else if (!string.IsNullOrEmpty(error))
            {
                Log.Warning("DevOps SSO: Authorization error: {Error}", error);
                ShowError($"Authentication failed: {error}");
            }
            else
            {
                ShowError("Authentication failed: no authorization code received.");
            }
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        LoadingIndicator.Visibility = Visibility.Collapsed;

        if (!e.IsSuccess)
        {
            if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                ShowError($"Navigation failed: {e.WebErrorStatus}");
            }
            return;
        }

        // Show WebView and hide loading
        LoadingPanel.Visibility = Visibility.Collapsed;
        WebView.Visibility = Visibility.Visible;

        var url = WebView.Source?.ToString() ?? "";
        StatusText.Text = new Uri(url).Host;
    }

    // ── Token Exchange ──────────────────────────────────────────────────────

    private async Task ExchangeCodeForTokenAsync(string code)
    {
        try
        {
            var result = await _ssoService.ExchangeCodeAsync(code);

            if (!result.Success || string.IsNullOrEmpty(result.Token))
            {
                ShowError($"Token exchange failed: {result.Error ?? "unknown error"}");
                return;
            }

            _authCompleted = true;

            Log.Information("DevOps SSO authentication successful for {UserName}", result.UserName ?? "(unknown)");

            AuthenticationCompleted?.Invoke(this, result);
            Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DevOps SSO token exchange error");
            ShowError($"Token exchange error: {ex.Message}");
        }
    }

    // ── UI Helpers ──────────────────────────────────────────────────────────

    private void ShowError(string message)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        WebView.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorMessage.Text = message;
        StatusText.Text = "Error";
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        await StartAuthenticationAsync();
    }
}
