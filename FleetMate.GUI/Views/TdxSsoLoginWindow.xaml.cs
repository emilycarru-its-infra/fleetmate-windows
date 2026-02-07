using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using ModernWpf.Controls;
using Serilog;

namespace FleetMate.GUI.Views;

/// <summary>
/// TDX SSO login window using WebView2 for SAML/Shibboleth authentication
/// </summary>
public partial class TdxSsoLoginWindow : ModernWpf.Controls.Window
{
    private readonly string _ssoLoginUrl;
    private bool _authCompleted;
    
    /// <summary>
    /// Patterns indicating successful authentication
    /// </summary>
    private static readonly string[] SuccessPatterns = 
    {
        "/SBTDClient/",
        "/TDClient/",
        "/TDNext/",
        "/Home/Desktop"
    };
    
    /// <summary>
    /// Cookie names that may contain the auth token
    /// </summary>
    private static readonly string[] TokenCookieNames = 
    {
        "TDWebApi-AuthToken",
        "authToken",
        ".AspNetCore.Cookies"
    };
    
    /// <summary>
    /// Event raised when authentication completes successfully
    /// </summary>
    public event EventHandler<TdxSsoResult>? AuthenticationCompleted;
    
    /// <summary>
    /// Event raised when authentication is cancelled
    /// </summary>
    public event EventHandler? AuthenticationCancelled;
    
    public TdxSsoLoginWindow(string baseUrl)
    {
        InitializeComponent();
        
        // Build SSO login URL - strip /TDWebApi path if present for root SSO endpoint
        var rootUrl = baseUrl.TrimEnd('/').Replace("/TDWebApi", "");
        _ssoLoginUrl = rootUrl + "/api/auth/loginsso";
        
        Loaded += async (_, _) => await InitializeWebViewAsync();
        Closing += (_, e) =>
        {
            if (!_authCompleted)
            {
                AuthenticationCancelled?.Invoke(this, EventArgs.Empty);
            }
        };
    }
    
    private async Task InitializeWebViewAsync()
    {
        try
        {
            StatusText.Text = "Initializing WebView2...";
            
            // Initialize WebView2
            await WebView.EnsureCoreWebView2Async();
            
            // Configure WebView2
            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            WebView.CoreWebView2.Settings.IsBuiltInErrorPageEnabled = true;
            
            // Wire up navigation events
            WebView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            WebView.CoreWebView2.WebResourceResponseReceived += OnWebResourceResponseReceived;
            
            // Start SSO flow
            await StartAuthenticationAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize WebView2");
            ShowError($"Failed to initialize browser: {ex.Message}");
        }
    }
    
    private async Task StartAuthenticationAsync()
    {
        try
        {
            StatusText.Text = "Loading SSO login...";
            LoadingPanel.Visibility = Visibility.Visible;
            ErrorPanel.Visibility = Visibility.Collapsed;
            WebView.Visibility = Visibility.Collapsed;
            
            WebView.CoreWebView2.Navigate(_ssoLoginUrl);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start SSO authentication");
            ShowError($"Failed to load login page: {ex.Message}");
        }
    }
    
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        LoadingIndicator.Visibility = Visibility.Visible;
        StatusText.Text = $"Navigating to {new Uri(e.Uri).Host}...";
        Log.Debug("WebView navigating to: {Uri}", e.Uri);
    }
    
    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
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
        
        // Check if we've reached a success page
        if (IsSuccessUrl(url))
        {
            StatusText.Text = "Authentication successful, extracting token...";
            await CompleteAuthenticationAsync();
        }
    }
    
    private void OnWebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        // Log cookies from responses for debugging
        try
        {
            var uri = new Uri(e.Request.Uri);
            if (e.Response.Headers != null)
            {
                // Check for set-cookie headers
                var iterator = e.Response.Headers.GetIterator();
                while (iterator.HasCurrent)
                {
                    if (iterator.Current.Key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
                    {
                        var cookieValue = iterator.Current.Value;
                        foreach (var tokenName in TokenCookieNames)
                        {
                            if (cookieValue.Contains(tokenName, StringComparison.OrdinalIgnoreCase))
                            {
                                Log.Debug("Found auth cookie in response: {CookieName}", tokenName);
                            }
                        }
                    }
                    iterator.MoveNext();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Error checking response headers");
        }
    }
    
    private bool IsSuccessUrl(string url)
    {
        return SuccessPatterns.Any(pattern => url.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }
    
    private async Task CompleteAuthenticationAsync()
    {
        try
        {
            // Wait a moment for cookies to be fully set
            await Task.Delay(500);
            
            // Extract token from cookies
            var cookieManager = WebView.CoreWebView2.CookieManager;
            var cookies = await cookieManager.GetCookiesAsync(_ssoLoginUrl);
            
            string? token = null;
            foreach (var cookieName in TokenCookieNames)
            {
                var cookie = cookies.FirstOrDefault(c => c.Name.Equals(cookieName, StringComparison.OrdinalIgnoreCase));
                if (cookie != null)
                {
                    token = cookie.Value;
                    Log.Debug("Found auth token in cookie: {CookieName}", cookieName);
                    break;
                }
            }
            
            // Also try to get token from localStorage/sessionStorage
            if (string.IsNullOrEmpty(token))
            {
                token = await TryExtractTokenFromPageAsync();
            }
            
            if (!string.IsNullOrEmpty(token))
            {
                // Get user info
                var (userName, userEmail) = await TryExtractUserInfoAsync();
                
                _authCompleted = true;
                
                var result = new TdxSsoResult
                {
                    Success = true,
                    Token = token,
                    UserName = userName,
                    UserEmail = userEmail,
                    Expiry = DateTime.UtcNow.AddHours(23)
                };
                
                Log.Information("TDX SSO authentication successful for user: {UserName}", userName ?? "(unknown)");
                
                AuthenticationCompleted?.Invoke(this, result);
                Close();
            }
            else
            {
                ShowError("Authentication succeeded but could not extract token. Please try again.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to complete SSO authentication");
            ShowError($"Failed to complete authentication: {ex.Message}");
        }
    }
    
    private async Task<string?> TryExtractTokenFromPageAsync()
    {
        try
        {
            var script = @"
                (function() {
                    var token = localStorage.getItem('authToken') || 
                               localStorage.getItem('TDWebApi-AuthToken') ||
                               sessionStorage.getItem('authToken') ||
                               sessionStorage.getItem('TDWebApi-AuthToken');
                    return token || '';
                })();
            ";
            
            var result = await WebView.CoreWebView2.ExecuteScriptAsync(script);
            
            // Result is JSON-encoded (e.g., "\"tokenvalue\"")
            if (!string.IsNullOrEmpty(result) && result != "\"\"" && result != "null")
            {
                // Remove JSON encoding
                return result.Trim('"');
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to extract token from page");
        }
        
        return null;
    }
    
    private async Task<(string? userName, string? userEmail)> TryExtractUserInfoAsync()
    {
        try
        {
            var script = @"
                (function() {
                    try {
                        if (typeof CURRENT_USER !== 'undefined') {
                            return JSON.stringify({
                                name: CURRENT_USER.FullName || CURRENT_USER.Name,
                                email: CURRENT_USER.Email || CURRENT_USER.PrimaryEmail
                            });
                        }
                        if (typeof window.__TD_USER__ !== 'undefined') {
                            return JSON.stringify({
                                name: window.__TD_USER__.fullName,
                                email: window.__TD_USER__.email
                            });
                        }
                        return 'null';
                    } catch (e) {
                        return 'null';
                    }
                })();
            ";
            
            var result = await WebView.CoreWebView2.ExecuteScriptAsync(script);
            
            if (!string.IsNullOrEmpty(result) && result != "null" && result != "\"null\"")
            {
                // Parse JSON result
                var json = System.Text.Json.JsonDocument.Parse(result.Trim('"').Replace("\\\"", "\""));
                var root = json.RootElement;
                
                var name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                var email = root.TryGetProperty("email", out var emailEl) ? emailEl.GetString() : null;
                
                return (name, email);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to extract user info from page");
        }
        
        return (null, null);
    }
    
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
/// Result of a TDX SSO authentication attempt
/// </summary>
public class TdxSsoResult
{
    public bool Success { get; set; }
    public string? Token { get; set; }
    public string? UserName { get; set; }
    public string? UserEmail { get; set; }
    public string? Error { get; set; }
    public DateTime Expiry { get; set; }
    
    public static TdxSsoResult Failed(string error) => new()
    {
        Success = false,
        Error = error
    };
}
