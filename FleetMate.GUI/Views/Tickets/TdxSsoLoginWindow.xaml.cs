using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using ModernWpf.Controls;
using Serilog;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>
/// TDX SSO login window using WebView2 for SAML/Shibboleth authentication.
/// 
/// Three-phase SSO:
///   Phase 1  — Silent HttpClient: Negotiate/Kerberos follows redirect chain, gets JWT
///   Phase 1.5 — Headless WebView2: Invisible window, handles Entra SSO + Windows Hello
///   Phase 2  — Interactive WebView2: Shows browser to the user (fallback)
///
/// WebView2 handles cross-origin POSTs natively (unlike WKWebView on macOS),
/// so no SAML form interception is needed.
/// </summary>
public partial class TdxSsoLoginWindow : Window
{
    private readonly string _ssoLoginUrl;
    private readonly string _rootUrl;
    private bool _authCompleted;
    private bool _usernameAutoFilled;
    private bool _fidoFallbackInjected;
    private string? _detectedUpn;
    
    /// <summary>
    /// Patterns indicating successful authentication
    /// </summary>
    private static readonly string[] SuccessPatterns = 
    {
        "/SBTDClient/",
        "/TDClient/",
        "/TDNext/",
        "/TDWorkManagement/",
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
    
    #region JavaScript Scripts
    
    /// <summary>
    /// FIDO/passkey fallback script. Handles three scenarios:
    /// 1. "Sign in another way" — clicked on ANY Entra page where the link exists
    /// 2. Method selection page — picks a non-FIDO method (Authenticator, SMS, etc.)
    /// 3. "Back" button as last resort on error pages
    /// NEVER clicks "Try again" — that retries the failing passkey in a loop.
    /// </summary>
    private const string FidoFallbackScript = @"
(function() {
    if (window.__fleetmateFidoFallback) return;
    window.__fleetmateFidoFallback = true;
    var acted = false;
    function tryFallback() {
        if (acted) return true;
        var elems = document.querySelectorAll('a, button, [role=""link""], [role=""button""], input[type=""submit""], span[tabindex], div[tabindex], li[tabindex]');
        // Priority 1: Click ""Sign in another way"" — fires on ANY Entra page.
        for (var i = 0; i < elems.length; i++) {
            var text = (elems[i].textContent || elems[i].value || '').trim().toLowerCase();
            if (text.indexOf('sign in another way') !== -1 ||
                text.indexOf('other ways to sign in') !== -1 ||
                text.indexOf('use another method') !== -1 ||
                text.indexOf('use a different method') !== -1 ||
                text.indexOf('try another way') !== -1 ||
                text.indexOf('try a different way') !== -1 ||
                text.indexOf('choose another') !== -1 ||
                text.indexOf(""i can't use"") !== -1) {
                console.log('[FleetMate] FIDO fallback - clicking: ' + text);
                elems[i].click();
                acted = true;
                return true;
            }
        }
        // Only run Priorities 2 & 3 on error or FIDO pages
        var bodyText = (document.body && document.body.innerText) || '';
        var isErrorPage = bodyText.indexOf(""couldn\u2019t sign you in"") !== -1 ||
                          bodyText.indexOf(""couldn't sign you in"") !== -1 ||
                          bodyText.indexOf('sign-in was unsuccessful') !== -1 ||
                          bodyText.indexOf('Something went wrong') !== -1 ||
                          bodyText.indexOf(""couldn't verify"") !== -1 ||
                          bodyText.indexOf('error occurred') !== -1;
        var isFidoPage = bodyText.indexOf('passkey') !== -1 ||
                         bodyText.indexOf('security key') !== -1 ||
                         bodyText.indexOf('FIDO') !== -1;
        if (!isErrorPage && !isFidoPage) return false;
        // Priority 2: Select a non-FIDO auth method tile
        var tiles = document.querySelectorAll('[data-value]');
        if (tiles.length > 0) {
            var preferred = ['PhoneAppNotification', 'PhoneAppOTP', 'OneWaySMS',
                           'TwoWayVoiceMobile', 'Password', 'PhoneAppPassword'];
            for (var p = 0; p < preferred.length; p++) {
                for (var t = 0; t < tiles.length; t++) {
                    var val = tiles[t].getAttribute('data-value') || '';
                    if (val === preferred[p]) {
                        console.log('[FleetMate] Selecting alt method: ' + val);
                        tiles[t].click();
                        acted = true;
                        setTimeout(function() {
                            var btns = document.querySelectorAll('input[type=""submit""], button[type=""submit""], button');
                            for (var j = 0; j < btns.length; j++) {
                                var bt = (btns[j].textContent || btns[j].value || '').trim().toLowerCase();
                                if (bt === 'next' || bt === 'verify' || bt === 'sign in' ||
                                    bt === 'send code' || bt === 'yes' || bt === 'send notification') {
                                    btns[j].click();
                                    break;
                                }
                            }
                        }, 500);
                        return true;
                    }
                }
            }
        }
        // Priority 3: ""Back"" on error pages
        if (isErrorPage) {
            for (var i = 0; i < elems.length; i++) {
                var text = (elems[i].textContent || elems[i].value || '').trim().toLowerCase();
                if (text === 'back' || text === 'go back') {
                    console.log('[FleetMate] Error page - clicking: ' + text);
                    elems[i].click();
                    acted = true;
                    return true;
                }
            }
        }
        return false;
    }
    if (document.body) {
        var observer = new MutationObserver(function() { tryFallback(); });
        observer.observe(document.body, { childList: true, subtree: true });
        setTimeout(function() { observer.disconnect(); }, 45000);
    }
    var delays = [200, 500, 1000, 1500, 2000, 3000, 4000, 6000, 8000, 10000, 13000, 16000, 20000];
    delays.forEach(function(d) { setTimeout(tryFallback, d); });
})();
";
    
    /// <summary>
    /// Generate JavaScript to auto-fill the Entra sign-in page username and click Next.
    /// </summary>
    private static string EntraAutoLoginScript(string upn)
    {
        // Escape for JS string literal embedded in C# verbatim string
        var escaped = upn.Replace("\\", "\\\\").Replace("'", "\\'");
        return $@"
(function() {{
    var filled = false;
    function tryAutoLogin() {{
        if (filled) return;
        var input = document.querySelector('input[name=""loginfmt""]');
        if (!input) input = document.querySelector('input[type=""email""]');
        if (!input) return;
        var nativeSetter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
        nativeSetter.call(input, '{escaped}');
        input.dispatchEvent(new Event('input', {{ bubbles: true }}));
        input.dispatchEvent(new Event('change', {{ bubbles: true }}));
        filled = true;
        console.log('[FleetMate] Auto-filled username: {escaped}');
        setTimeout(function() {{
            var nextBtn = document.querySelector('input[type=""submit""]');
            if (!nextBtn) nextBtn = document.querySelector('button[type=""submit""]');
            if (!nextBtn) {{
                var btns = document.querySelectorAll('input, button');
                for (var i = 0; i < btns.length; i++) {{
                    var t = (btns[i].value || btns[i].textContent || '').trim().toLowerCase();
                    if (t === 'next' || t === 'sign in') {{ nextBtn = btns[i]; break; }}
                }}
            }}
            if (nextBtn) {{ console.log('[FleetMate] Clicking Next'); nextBtn.click(); }}
            window.__fleetmateFidoFallback = false;
            setTimeout(function() {{ try {{ eval(window.__fleetmateFidoScript || ''); }} catch(e) {{}} }}, 3000);
        }}, 300);
    }}
    tryAutoLogin();
    [200, 500, 1000, 2000].forEach(function(d) {{ setTimeout(tryAutoLogin, d); }});
    if (document.body) {{
        var observer = new MutationObserver(function() {{ tryAutoLogin(); }});
        observer.observe(document.body, {{ childList: true, subtree: true }});
        setTimeout(function() {{ observer.disconnect(); }}, 5000);
    }}
}})();
";
    }
    
    #endregion
    
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
        
        // Build SSO login URL - navigate to TDWorkManagement to trigger SAML redirect chain
        _rootUrl = baseUrl.TrimEnd('/').Replace("/TDWebApi", "");
        _ssoLoginUrl = _rootUrl + "/TDWorkManagement/";
        
        // Detect UPN from Windows identity (domain\user → user@domain or UPN from AD)
        _detectedUpn = DetectWindowsUpn();
        if (_detectedUpn != null)
            Log.Information("[tdx-sso] Detected Windows UPN: {Upn}", _detectedUpn);
        
        Loaded += async (_, _) => await InitializeWebViewAsync();
        Closing += (_, e) =>
        {
            if (!_authCompleted)
            {
                AuthenticationCancelled?.Invoke(this, EventArgs.Empty);
            }
        };
    }
    
    /// <summary>
    /// Detect the current Windows user's UPN for auto-fill.
    /// Uses WindowsIdentity to get the UPN claim or falls back to domain\user format.
    /// </summary>
    private static string? DetectWindowsUpn()
    {
        try
        {
            var identity = WindowsIdentity.GetCurrent();
            
            // Try UPN claim first (most reliable for Entra-joined devices)
            var upnClaim = identity.Claims
                .FirstOrDefault(c => c.Type == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn");
            if (upnClaim != null && upnClaim.Value.Contains('@'))
                return upnClaim.Value.ToLowerInvariant();
            
            // Fall back to identity name — if it's domain\user, we can't derive UPN reliably
            // but the Entra sign-in page can check domain hints
            var name = identity.Name; // e.g. "DOMAIN\user" or "user@domain.ca"
            if (name != null && name.Contains('@'))
                return name.ToLowerInvariant();
            
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[tdx-sso] Failed to detect Windows UPN");
            return null;
        }
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
        
        // Auto-fill username on Entra sign-in page
        if (!_usernameAutoFilled && _detectedUpn != null && url.Contains("login.microsoftonline.com"))
        {
            _usernameAutoFilled = true;
            Log.Information("[tdx-sso] Entra login page detected — auto-filling UPN: {Upn}", _detectedUpn);
            var script = EntraAutoLoginScript(_detectedUpn);
            try { await WebView.CoreWebView2.ExecuteScriptAsync(script); }
            catch (Exception ex) { Log.Debug(ex, "[tdx-sso] Auto-fill script error"); }
        }
        
        // Inject FIDO fallback on FIDO/passkey pages or after autologon
        if (!_fidoFallbackInjected && (
            url.Contains("/fido/") || url.Contains("/fido2/") ||
            url.Contains("passkey", StringComparison.OrdinalIgnoreCase)))
        {
            await InjectFidoFallbackAsync();
        }
        
        // Check for loginSSO returning JWT as page content
        if (url.Contains("/api/auth/loginSSO"))
        {
            Log.Debug("[tdx-sso] loginSSO page loaded — checking for JWT in page content");
            await ExtractJwtFromPageContentAsync();
            return;
        }
        
        // Check if we've reached a success page
        if (IsSuccessUrl(url))
        {
            StatusText.Text = "Authentication successful, extracting token...";
            await CompleteAuthenticationAsync();
        }
    }
    
    private async void OnWebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            var uri = new Uri(e.Request.Uri);
            var host = uri.Host;
            var status = e.Response.StatusCode;
            
            Log.Debug("[tdx-sso] [RESPONSE] {Status} {Uri}", status, e.Request.Uri);
            
            // When autologon (Seamless SSO / Kerberos) succeeds, pre-inject FIDO fallback
            // so it's ready the moment the FIDO page appears (SPA transition, no new nav event)
            if (status == 200 && (host.Contains("autologon.microsoftazuread-sso.com") || host.Contains("autologon.")))
            {
                Log.Information("[tdx-sso] Autologon succeeded — pre-injecting FIDO fallback");
                await Task.Delay(500);
                await Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await WebView.CoreWebView2.ExecuteScriptAsync("window.__fleetmateFidoFallback = false;");
                        await WebView.CoreWebView2.ExecuteScriptAsync(FidoFallbackScript);
                        _fidoFallbackInjected = true;
                    }
                    catch (Exception ex) { Log.Debug(ex, "[tdx-sso] FIDO pre-inject error"); }
                });
            }

            // Check for auth cookies in responses
            if (e.Response.Headers != null)
            {
                var iterator = e.Response.Headers.GetIterator();
                while (iterator.HasCurrentHeader)
                {
                    if (iterator.Current.Key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
                    {
                        var cookieValue = iterator.Current.Value;
                        foreach (var tokenName in TokenCookieNames)
                        {
                            if (cookieValue.Contains(tokenName, StringComparison.OrdinalIgnoreCase))
                            {
                                Log.Debug("[tdx-sso] Found auth cookie: {CookieName}", tokenName);
                            }
                        }
                    }
                    iterator.MoveNext();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[tdx-sso] Error in response handler");
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
            
            // First, try to get a JWT bearer token from TDX loginSSO endpoint
            // This mirrors the macOS approach: after SAML completes, call
            // /api/auth/loginSSO with the session cookies to get a proper JWT
            var jwtResult = await TryGetJwtTokenAsync();
            
            if (jwtResult.token != null)
            {
                _authCompleted = true;
                
                var result = new TdxSsoResult
                {
                    Success = true,
                    Token = jwtResult.token,
                    UserName = jwtResult.userName,
                    UserEmail = jwtResult.userEmail,
                    Expiry = DateTime.UtcNow.AddHours(23)
                };
                
                Log.Information("[JWT] ✓ TDX SSO authentication successful for user: {UserName} ({UserEmail})", 
                    jwtResult.userName ?? "(unknown)",
                    jwtResult.userEmail ?? "(unknown)");
                
                AuthenticationCompleted?.Invoke(this, result);
                Close();
                return;
            }
            
            // Fallback: Extract token from cookies
            Log.Debug("[JWT] JWT retrieval failed, falling back to cookie extraction");
            
            var cookieManager = WebView.CoreWebView2.CookieManager;
            var cookies = await cookieManager.GetCookiesAsync(_rootUrl);
            
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
                // Get user info from page JavaScript
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
    
    /// <summary>
    /// Call /api/auth/loginSSO with session cookies from WebView2 to get a JWT bearer token.
    /// This is the same approach used on macOS after the SAML flow completes.
    /// </summary>
    private async Task<(string? token, string? userName, string? userEmail)> TryGetJwtTokenAsync()
    {
        try
        {
            var loginSsoUrl = _rootUrl + "/TDWebApi/api/auth/loginSSO";
            Log.Debug("[JWT] Requesting bearer token from {Url}", loginSsoUrl);
            
            // Get all cookies from WebView2 for the TDX domain
            var cookieManager = WebView.CoreWebView2.CookieManager;
            var cookies = await cookieManager.GetCookiesAsync(_rootUrl);
            
            // Build a CookieContainer with the WebView2 cookies
            var cookieContainer = new CookieContainer();
            var uri = new Uri(_rootUrl);
            foreach (var cookie in cookies)
            {
                try
                {
                    cookieContainer.Add(uri, new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain));
                }
                catch (Exception ex)
                {
                    Log.Debug("Skipping cookie {Name}: {Error}", cookie.Name, ex.Message);
                }
            }
            
            using var handler = new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                UseCookies = true,
                AllowAutoRedirect = true
            };
            
            using var httpClient = new HttpClient(handler);
            httpClient.DefaultRequestHeaders.Add("User-Agent", "FleetMate/1.0");
            
            var response = await httpClient.GetAsync(loginSsoUrl);
            
            Log.Debug("[JWT] loginSSO response: {StatusCode}", response.StatusCode);
            
            if (response.IsSuccessStatusCode)
            {
                var tokenText = await response.Content.ReadAsStringAsync();
                
                // Token comes back as a quoted JSON string (e.g., "\"eyJhbG...\"")
                var cleanToken = tokenText.Trim().Trim('"');
                
                if (!string.IsNullOrEmpty(cleanToken) && cleanToken.Length > 20)
                {
                    Log.Debug("[JWT] ✓ Got bearer token ({TokenPrefix}...)", cleanToken[..20]);
                    
                    // Extract user info from JWT payload
                    var (userName, userEmail) = ExtractUserInfoFromJwt(cleanToken);
                    
                    if (userName != null)
                    {
                        Log.Debug("[JWT] User: {Name} ({Email})", userName, userEmail ?? "");
                    }
                    
                    return (cleanToken, userName, userEmail);
                }
            }
            
            Log.Debug("[JWT] loginSSO didn't return a valid token");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[JWT] loginSSO error");
        }
        
        return (null, null, null);
    }
    
    /// <summary>
    /// Extract user info (name, email) from a JWT token's payload.
    /// The JWT payload is base64url-encoded JSON in the second segment.
    /// </summary>
    private static (string? userName, string? userEmail) ExtractUserInfoFromJwt(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return (null, null);
            
            // Base64URL → Base64 conversion
            var payload = parts[1]
                .Replace('-', '+')
                .Replace('_', '/');
            
            // Add padding
            var remainder = payload.Length % 4;
            if (remainder > 0)
            {
                payload += new string('=', 4 - remainder);
            }
            
            var bytes = Convert.FromBase64String(payload);
            var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            
            string? name = null;
            string? email = null;
            
            // Try different JWT claim names for name
            foreach (var claim in new[] { "given_name", "name", "unique_name" })
            {
                if (root.TryGetProperty(claim, out var val) && val.ValueKind == JsonValueKind.String)
                {
                    name = val.GetString();
                    if (!string.IsNullOrEmpty(name)) break;
                }
            }
            
            // Try different JWT claim names for email
            foreach (var claim in new[] { "email", "upn", "unique_name" })
            {
                if (root.TryGetProperty(claim, out var val) && val.ValueKind == JsonValueKind.String)
                {
                    email = val.GetString();
                    if (!string.IsNullOrEmpty(email)) break;
                }
            }
            
            return (name, email);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to extract user info from JWT");
            return (null, null);
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
                var json = JsonDocument.Parse(result.Trim('"').Replace("\\\"", "\""));
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
        _usernameAutoFilled = false;
        _fidoFallbackInjected = false;
        await StartAuthenticationAsync();
    }
    
    /// <summary>
    /// Inject the FIDO fallback script into the current page.
    /// </summary>
    private async Task InjectFidoFallbackAsync()
    {
        if (_fidoFallbackInjected) return;
        _fidoFallbackInjected = true;
        Log.Information("[tdx-sso] Injecting FIDO fallback script");
        try
        {
            await WebView.CoreWebView2.ExecuteScriptAsync(FidoFallbackScript);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[tdx-sso] FIDO fallback injection error");
        }
    }
    
    /// <summary>
    /// Check if the loginSSO page returned a JWT as its body content.
    /// </summary>
    private async Task ExtractJwtFromPageContentAsync()
    {
        try
        {
            var script = "document.body ? document.body.innerText : ''";
            var result = await WebView.CoreWebView2.ExecuteScriptAsync(script);
            if (string.IsNullOrEmpty(result)) return;
            
            var text = result.Trim('"').Replace("\\\"", "\"").Trim();
            if (text.StartsWith("eyJ") && text.Length > 20)
            {
                Log.Information("[tdx-sso] [JWT] Got bearer token from loginSSO page content");
                var (userName, userEmail) = ExtractUserInfoFromJwt(text);
                _authCompleted = true;
                AuthenticationCompleted?.Invoke(this, new TdxSsoResult
                {
                    Success = true,
                    Token = text,
                    UserName = userName,
                    UserEmail = userEmail,
                    Expiry = DateTime.UtcNow.AddHours(23)
                });
                Close();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[tdx-sso] Error extracting JWT from page");
        }
    }
    
    #region Phase 1 — Silent HttpClient SSO
    
    /// <summary>
    /// Phase 1: Attempt fully silent SSO using HttpClient with Windows Negotiate/Kerberos auth.
    /// Follows the SAML redirect chain: TDWorkManagement → Shibboleth → Entra → autologon → JWT.
    /// Works when the machine is domain-joined and Kerberos/NTLM tickets are available.
    /// </summary>
    public static async Task<TdxSsoResult?> TryPhase1SilentAsync(string baseUrl, CancellationToken ct = default)
    {
        var rootUrl = baseUrl.TrimEnd('/').Replace("/TDWebApi", "");
        var loginSsoUrl = baseUrl.TrimEnd('/') + "/api/auth/loginSSO";
        var entryUrl = rootUrl + "/TDWorkManagement/";
        
        Log.Information("[tdx-sso] [Phase 1] Starting silent HttpClient SSO (Negotiate/Kerberos)");
        
        // Use a handler that sends Windows negotiate credentials automatically
        using var handler = new HttpClientHandler
        {
            UseDefaultCredentials = true,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 20,
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 FleetMate/1.0");
        
        try
        {
            // Step 1: Quick check — does loginSSO already have a valid session?
            Log.Debug("[tdx-sso] [Phase 1] GET {Url}", loginSsoUrl);
            var resp = await client.GetAsync(loginSsoUrl, ct);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                var token = body.Trim().Trim('"');
                if (token.StartsWith("eyJ") && token.Length > 20)
                {
                    Log.Information("[tdx-sso] [Phase 1] ✓ JWT from existing session");
                    var (name, email) = ExtractUserInfoFromJwt(token);
                    return new TdxSsoResult
                    {
                        Success = true, Token = token,
                        UserName = name, UserEmail = email,
                        Expiry = DateTime.UtcNow.AddHours(23)
                    };
                }
            }
            
            // Step 2: Follow the full SAML redirect chain from TDWorkManagement
            Log.Debug("[tdx-sso] [Phase 1] GET {Url} (SAML redirect chain)", entryUrl);
            var entryResp = await client.GetAsync(entryUrl, ct);
            
            Log.Debug("[tdx-sso] [Phase 1] Entry response: {Status} → {Url}",
                entryResp.StatusCode, entryResp.RequestMessage?.RequestUri);
            
            // Step 3: Now try loginSSO with the cookies from the SAML chain
            Log.Debug("[tdx-sso] [Phase 1] GET {Url} (with SAML cookies)", loginSsoUrl);
            var jwtResp = await client.GetAsync(loginSsoUrl, ct);
            if (jwtResp.IsSuccessStatusCode)
            {
                var body = await jwtResp.Content.ReadAsStringAsync(ct);
                var token = body.Trim().Trim('"');
                if (token.StartsWith("eyJ") && token.Length > 20)
                {
                    Log.Information("[tdx-sso] [Phase 1] ✓ JWT from SAML redirect chain");
                    var (name, email) = ExtractUserInfoFromJwt(token);
                    return new TdxSsoResult
                    {
                        Success = true, Token = token,
                        UserName = name, UserEmail = email,
                        Expiry = DateTime.UtcNow.AddHours(23)
                    };
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Debug("[tdx-sso] [Phase 1] Cancelled");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tdx-sso] [Phase 1] Silent HttpClient SSO failed");
        }
        
        Log.Information("[tdx-sso] [Phase 1] Silent SSO did not produce a JWT");
        return null;
    }
    
    #endregion
    
    #region Phase 1.5 — Headless WebView2 SSO
    
    /// <summary>
    /// Phase 1.5: Attempt SSO with a headless (invisible) WebView2 window.
    /// WebView2 has native Entra SSO and Windows Hello/FIDO support, so it may
    /// complete fully silently. FIDO fallback is injected after a timeout.
    /// Must be called on the UI thread.
    /// </summary>
    public static async Task<TdxSsoResult?> TryPhase15HeadlessAsync(string baseUrl, CancellationToken ct = default)
    {
        Log.Information("[tdx-sso] [Phase 1.5] Starting headless WebView2 SSO");
        
        var rootUrl = baseUrl.TrimEnd('/').Replace("/TDWebApi", "");
        var ssoLoginUrl = rootUrl + "/TDWorkManagement/";
        
        TdxSsoResult? result = null;
        var tcs = new TaskCompletionSource<TdxSsoResult?>();
        
        // Create a hidden window to host the WebView2
        var hiddenWindow = new Window
        {
            Width = 1, Height = 1,
            Left = -9999, Top = -9999,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Visibility = Visibility.Hidden
        };
        
        var webView = new Microsoft.Web.WebView2.Wpf.WebView2();
        hiddenWindow.Content = webView;
        hiddenWindow.Show(); // Required for WebView2 to initialize
        
        bool completed = false;
        bool fidoFallbackInjected = false;
        var detectedUpn = DetectWindowsUpn();
        bool usernameAutoFilled = false;
        
        try
        {
            await webView.EnsureCoreWebView2Async();
            
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            
            // Monitor navigation for success URL
            webView.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (completed || !e.IsSuccess) return;
                
                var url = webView.Source?.ToString() ?? "";
                Log.Debug("[tdx-sso] [Phase 1.5] [DONE] {Url}", url);
                
                // Auto-fill on Entra login page
                if (!usernameAutoFilled && detectedUpn != null && url.Contains("login.microsoftonline.com"))
                {
                    usernameAutoFilled = true;
                    Log.Information("[tdx-sso] [Phase 1.5] Auto-filling UPN: {Upn}", detectedUpn);
                    try { await webView.CoreWebView2.ExecuteScriptAsync(EntraAutoLoginScript(detectedUpn)); }
                    catch (Exception ex) { Log.Debug(ex, "[tdx-sso] [Phase 1.5] Auto-fill error"); }
                    
                    // Schedule FIDO fallback after auto-fill
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000, ct);
                        if (!completed && !fidoFallbackInjected)
                        {
                            hiddenWindow.Dispatcher.Invoke(async () =>
                            {
                                try
                                {
                                    fidoFallbackInjected = true;
                                    Log.Debug("[tdx-sso] [Phase 1.5] Injecting FIDO fallback after auto-fill");
                                    await webView.CoreWebView2.ExecuteScriptAsync(FidoFallbackScript);
                                }
                                catch { /* disposed */ }
                            });
                        }
                    });
                }
                
                // Check loginSSO for JWT
                if (url.Contains("/api/auth/loginSSO"))
                {
                    try
                    {
                        var scriptResult = await webView.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
                        var text = scriptResult?.Trim('"').Replace("\\\"", "\"").Trim() ?? "";
                        if (text.StartsWith("eyJ") && text.Length > 20)
                        {
                            completed = true;
                            var (name, email) = ExtractUserInfoFromJwt(text);
                            Log.Information("[tdx-sso] [Phase 1.5] ✓ JWT from loginSSO page");
                            tcs.TrySetResult(new TdxSsoResult
                            {
                                Success = true, Token = text,
                                UserName = name, UserEmail = email,
                                Expiry = DateTime.UtcNow.AddHours(23)
                            });
                        }
                    }
                    catch { /* page may be gone */ }
                    return;
                }
                
                // Check for success URL → call loginSSO with cookies
                if (SuccessPatterns.Any(p => url.Contains(p, StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Information("[tdx-sso] [Phase 1.5] Success URL reached, extracting JWT...");
                    try
                    {
                        await Task.Delay(500, ct);
                        var jwtResult = await TryGetJwtFromWebViewAsync(webView, rootUrl);
                        if (jwtResult != null)
                        {
                            completed = true;
                            Log.Information("[tdx-sso] [Phase 1.5] ✓ JWT obtained");
                            tcs.TrySetResult(jwtResult);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "[tdx-sso] [Phase 1.5] JWT extraction error");
                    }
                }
            };
            
            // Pre-inject FIDO fallback on autologon success (SPA transition)
            webView.CoreWebView2.WebResourceResponseReceived += async (_, e) =>
            {
                try
                {
                    var uri = new Uri(e.Request.Uri);
                    if (e.Response.StatusCode == 200 &&
                        (uri.Host.Contains("autologon.microsoftazuread-sso.com") || uri.Host.Contains("autologon.")))
                    {
                        Log.Information("[tdx-sso] [Phase 1.5] Autologon succeeded — pre-injecting FIDO fallback");
                        await Task.Delay(500);
                        hiddenWindow.Dispatcher.Invoke(async () =>
                        {
                            try
                            {
                                await webView.CoreWebView2.ExecuteScriptAsync("window.__fleetmateFidoFallback = false;");
                                await webView.CoreWebView2.ExecuteScriptAsync(FidoFallbackScript);
                                fidoFallbackInjected = true;
                            }
                            catch { /* disposed */ }
                        });
                    }
                }
                catch { /* ignore */ }
            };
            
            // Start navigation
            webView.CoreWebView2.Navigate(ssoLoginUrl);
            
            // Wait for completion or timeout (40s)
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(40));
            
            try
            {
                result = await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Information("[tdx-sso] [Phase 1.5] Timed out after 40s");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tdx-sso] [Phase 1.5] Headless WebView2 SSO error");
        }
        finally
        {
            try
            {
                webView.Dispose();
                hiddenWindow.Close();
            }
            catch { /* cleanup */ }
        }
        
        if (result == null)
            Log.Information("[tdx-sso] [Phase 1.5] Headless SSO did not produce a JWT");
        
        return result;
    }
    
    /// <summary>
    /// Extract JWT from WebView2 cookies via loginSSO endpoint.
    /// </summary>
    private static async Task<TdxSsoResult?> TryGetJwtFromWebViewAsync(
        Microsoft.Web.WebView2.Wpf.WebView2 webView, string rootUrl)
    {
        var loginSsoUrl = rootUrl.Replace("/TDWebApi", "").TrimEnd('/') + "/TDWebApi/api/auth/loginSSO";
        var cookieManager = webView.CoreWebView2.CookieManager;
        var cookies = await cookieManager.GetCookiesAsync(rootUrl);
        
        var cookieContainer = new CookieContainer();
        var uri = new Uri(rootUrl);
        foreach (var cookie in cookies)
        {
            try { cookieContainer.Add(uri, new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain)); }
            catch { /* skip invalid cookies */ }
        }
        
        using var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            UseCookies = true,
            AllowAutoRedirect = true
        };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("User-Agent", "FleetMate/1.0");
        
        var resp = await client.GetAsync(loginSsoUrl);
        if (resp.IsSuccessStatusCode)
        {
            var token = (await resp.Content.ReadAsStringAsync()).Trim().Trim('"');
            if (token.StartsWith("eyJ") && token.Length > 20)
            {
                var (name, email) = ExtractUserInfoFromJwt(token);
                return new TdxSsoResult
                {
                    Success = true, Token = token,
                    UserName = name, UserEmail = email,
                    Expiry = DateTime.UtcNow.AddHours(23)
                };
            }
        }
        
        return null;
    }
    
    #endregion
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
