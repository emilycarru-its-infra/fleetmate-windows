using System.Net;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using FleetMate.Core.Services.Tickets;

namespace FleetMate.WinUI.Views;

/// <summary>
/// Interactive TeamDynamix SSO via WinUI WebView2. Navigates to the TDX site, lets the
/// user complete the SAML→Entra flow, then (once landed on a TDX page) exchanges the
/// session cookies for a JWT bearer at <c>/TDWebApi/api/auth/loginSSO</c>.
///
/// Essential port of the WPF TdxSsoLoginWindow — the CoreWebView2 API is identical.
/// Deferred vs the WPF version: multi-phase silent SSO, FIDO/passkey fallback scripting,
/// and localStorage token fallback. NOTE: the interactive flow can only be verified on a
/// box with TDX BrowserSSO configured and a human to authenticate.
/// </summary>
public sealed partial class TdxSsoLoginWindow : Window
{
    private static readonly string[] SuccessPatterns =
        { "/SBTDClient/", "/TDClient/", "/TDNext/", "/TDWorkManagement/", "/Home/Desktop" };
    private static readonly string[] TokenCookieNames =
        { "TDWebApi-AuthToken", "authToken", ".AspNetCore.Cookies" };

    private readonly string _rootUrl;
    private readonly TaskCompletionSource<TdxSsoResult?> _tcs = new();
    private bool _completed;

    public TdxSsoLoginWindow(string baseUrl)
    {
        InitializeComponent();
        _rootUrl = baseUrl.TrimEnd('/');
        AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 900));
        _ = InitAsync();
    }

    /// <summary>Activate the window and await the SSO result (null if cancelled/failed).</summary>
    public Task<TdxSsoResult?> ShowAndAuthenticateAsync()
    {
        Activate();
        return _tcs.Task;
    }

    private async Task InitAsync()
    {
        try
        {
            await WebView.EnsureCoreWebView2Async();
            WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            WebView.CoreWebView2.Navigate(_rootUrl);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not start WebView2: {ex.Message}";
            Busy.IsActive = false;
        }
    }

    private async void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_completed) return;
        var url = WebView.Source?.ToString() ?? "";
        if (!SuccessPatterns.Any(p => url.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = "Complete sign-in in the window above…";
            return;
        }

        _completed = true;
        StatusText.Text = "Signed in — extracting token…";

        try
        {
            var (token, name, email) = await TryGetJwtTokenAsync();
            token ??= await TryGetTokenFromCookiesAsync();

            if (!string.IsNullOrEmpty(token))
            {
                Complete(new TdxSsoResult
                {
                    Success = true,
                    Token = token,
                    UserName = name,
                    UserEmail = email,
                    Expiry = DateTime.UtcNow.AddHours(23),
                });
            }
            else
            {
                _completed = false; // let a later navigation try again
                StatusText.Text = "Signed in but no token yet — finishing…";
            }
        }
        catch (Exception ex)
        {
            Complete(new TdxSsoResult { Success = false, Error = ex.Message });
        }
    }

    /// <summary>Exchange WebView2 session cookies for a JWT bearer at the TDX loginSSO endpoint.</summary>
    private async Task<(string? Token, string? Name, string? Email)> TryGetJwtTokenAsync()
    {
        var loginSsoUrl = _rootUrl + "/TDWebApi/api/auth/loginSSO";
        var container = await BuildCookieContainerAsync();

        using var handler = new HttpClientHandler { CookieContainer = container, UseCookies = true, AllowAutoRedirect = true };
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("User-Agent", "FleetMate/1.0");

        var response = await http.GetAsync(loginSsoUrl);
        if (!response.IsSuccessStatusCode) return (null, null, null);

        var raw = (await response.Content.ReadAsStringAsync()).Trim().Trim('"');
        if (string.IsNullOrEmpty(raw) || raw.Length <= 20) return (null, null, null);

        var (name, email) = TdxJwt.ExtractUserInfo(raw);
        return (raw, name, email);
    }

    private async Task<string?> TryGetTokenFromCookiesAsync()
    {
        var cookies = await WebView.CoreWebView2.CookieManager.GetCookiesAsync(_rootUrl);
        foreach (var name in TokenCookieNames)
        {
            var match = cookies.FirstOrDefault(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (match != null && !string.IsNullOrEmpty(match.Value)) return match.Value;
        }
        return null;
    }

    private async Task<CookieContainer> BuildCookieContainerAsync()
    {
        var container = new CookieContainer();
        var uri = new Uri(_rootUrl);
        var cookies = await WebView.CoreWebView2.CookieManager.GetCookiesAsync(_rootUrl);
        foreach (var c in cookies)
        {
            try { container.Add(uri, new Cookie(c.Name, c.Value, c.Path, c.Domain)); }
            catch { /* skip malformed cookies */ }
        }
        return container;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Complete(null);

    private void Complete(TdxSsoResult? result)
    {
        _tcs.TrySetResult(result);
        Close();
    }
}
