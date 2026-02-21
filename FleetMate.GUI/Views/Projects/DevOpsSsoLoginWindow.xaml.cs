using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace FleetMate.GUI.Views.Projects;

/// <summary>
/// Azure DevOps SSO login window using WebView2 for OAuth2 Authorization Code + PKCE.
/// Mirrors the Mac app's DevOpsSsoLoginView behavior.
/// </summary>
public partial class DevOpsSsoLoginWindow : Window
{
    private readonly string _clientId;
    private readonly string _tenantId;
    private readonly string _codeVerifier;
    private readonly string _codeChallenge;
    private bool _authCompleted;

    private const string RedirectUri = "https://login.microsoftonline.com/common/oauth2/nativeclient";
    private const string AdoScope = "499b84ac-1321-427f-aa17-267ca6975798/.default offline_access";

    /// <summary>
    /// Event raised when authentication completes successfully.
    /// </summary>
    public event EventHandler<DevOpsSsoResult>? AuthenticationCompleted;

    /// <summary>
    /// Event raised when authentication is cancelled.
    /// </summary>
    public event EventHandler? AuthenticationCancelled;

    public DevOpsSsoLoginWindow(string clientId, string tenantId)
    {
        InitializeComponent();

        _clientId = clientId;
        _tenantId = tenantId;

        // Generate PKCE code verifier + challenge
        _codeVerifier = GenerateCodeVerifier();
        _codeChallenge = GenerateCodeChallenge(_codeVerifier);

        Loaded += async (_, _) => await InitializeWebViewAsync();
        Closing += (_, _) =>
        {
            if (!_authCompleted)
            {
                AuthenticationCancelled?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    // ── PKCE Helpers ────────────────────────────────────────────────────────

    private static string GenerateCodeVerifier()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string GenerateCodeChallenge(string verifier)
    {
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(challengeBytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
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

            var authorizeUrl = $"https://login.microsoftonline.com/{_tenantId}/oauth2/v2.0/authorize"
                + $"?client_id={Uri.EscapeDataString(_clientId)}"
                + $"&response_type=code"
                + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
                + $"&response_mode=query"
                + $"&scope={Uri.EscapeDataString(AdoScope)}"
                + $"&code_challenge={Uri.EscapeDataString(_codeChallenge)}"
                + $"&code_challenge_method=S256";

            Log.Debug("DevOps SSO: Navigating to authorize URL for tenant {TenantId}", _tenantId);
            WebView.CoreWebView2.Navigate(authorizeUrl);
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
        if (e.Uri.StartsWith(RedirectUri, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true; // Don't actually navigate to the redirect
            LoadingIndicator.Visibility = Visibility.Collapsed;

            var query = HttpUtility.ParseQueryString(uri.Query);
            var code = query["code"];
            var error = query["error"];
            var errorDescription = query["error_description"];

            if (!string.IsNullOrEmpty(code))
            {
                Log.Debug("DevOps SSO: Received authorization code");
                StatusText.Text = "Exchanging authorization code for token...";
                _ = ExchangeCodeForTokenAsync(code);
            }
            else if (!string.IsNullOrEmpty(error))
            {
                Log.Warning("DevOps SSO: Authorization error: {Error} - {Description}", error, errorDescription);
                ShowError($"Authentication failed: {errorDescription ?? error}");
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
            var tokenUrl = $"https://login.microsoftonline.com/{_tenantId}/oauth2/v2.0/token";

            var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = _codeVerifier,
                ["scope"] = AdoScope
            });

            using var http = new HttpClient();
            var response = await http.PostAsync(tokenUrl, body);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Log.Error("DevOps SSO token exchange failed: {Status} {Body}", response.StatusCode, responseText);
                ShowError($"Token exchange failed: {response.StatusCode}");
                return;
            }

            var json = JsonDocument.Parse(responseText);
            var root = json.RootElement;

            var accessToken = root.GetProperty("access_token").GetString();
            var expiresIn = root.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
            var idToken = root.TryGetProperty("id_token", out var idTok) ? idTok.GetString() : null;

            if (string.IsNullOrEmpty(accessToken))
            {
                ShowError("Token exchange returned empty access token.");
                return;
            }

            // Extract user info from id_token
            string? userName = null;
            if (!string.IsNullOrEmpty(idToken))
            {
                userName = ExtractUserNameFromIdToken(idToken);
            }

            _authCompleted = true;

            var result = new DevOpsSsoResult
            {
                Success = true,
                Token = accessToken,
                UserName = userName,
                Expiry = DateTime.UtcNow.AddSeconds(expiresIn)
            };

            Log.Information("DevOps SSO authentication successful for {UserName}", userName ?? "(unknown)");

            AuthenticationCompleted?.Invoke(this, result);
            Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DevOps SSO token exchange error");
            ShowError($"Token exchange error: {ex.Message}");
        }
    }

    /// <summary>
    /// Extract display name from JWT id_token payload.
    /// </summary>
    private static string? ExtractUserNameFromIdToken(string idToken)
    {
        try
        {
            var parts = idToken.Split('.');
            if (parts.Length < 2) return null;

            var payload = parts[1]
                .Replace('-', '+')
                .Replace('_', '/');

            var remainder = payload.Length % 4;
            if (remainder > 0) payload += new string('=', 4 - remainder);

            var bytes = Convert.FromBase64String(payload);
            var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;

            // Try "name" first (display name), then "preferred_username" (UPN)
            foreach (var claim in new[] { "name", "preferred_username", "unique_name" })
            {
                if (root.TryGetProperty(claim, out var val) && val.ValueKind == JsonValueKind.String)
                {
                    var value = val.GetString();
                    if (!string.IsNullOrEmpty(value)) return value;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to extract user name from id_token");
        }

        return null;
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

/// <summary>
/// Result of a DevOps SSO authentication attempt.
/// </summary>
public class DevOpsSsoResult
{
    public bool Success { get; set; }
    public string? Token { get; set; }
    public string? UserName { get; set; }
    public string? Error { get; set; }
    public DateTime Expiry { get; set; }

    public static DevOpsSsoResult Failed(string error) => new()
    {
        Success = false,
        Error = error
    };
}
