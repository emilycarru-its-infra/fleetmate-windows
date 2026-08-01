using System.Text.Json;
using FleetMate.Core.Services.Tickets;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace FleetMate.WinUI;

public sealed partial class TdxSignInWindow : Window
{
    private readonly string _baseUrl;
    private bool _completed;
    private bool _tokenRequested;
    public event EventHandler<TdxSsoResult>? AuthenticationCompleted;

    public TdxSignInWindow(string baseUrl)
    {
        InitializeComponent();
        _baseUrl = baseUrl;
        Title = "FleetMate — TeamDynamix sign-in";
        AppWindow.Resize(new SizeInt32(920, 680));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FleetMate", "WebView2");
            Directory.CreateDirectory(profile);
            var options = new CoreWebView2EnvironmentOptions
            {
                AllowSingleSignOnUsingOSPrimaryAccount = true
            };
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile, options);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.Source = new Uri(TdxSsoService.BuildEntryUrl(_baseUrl));
        }
        catch (Exception ex)
        {
            Status.Text = $"Browser initialization failed: {ex.Message}";
        }
    }

    private async void OnNavigationCompleted(Microsoft.UI.Xaml.Controls.WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (_completed || !args.IsSuccess) return;
        var uri = Browser.Source;
        Status.Text = uri == null ? "Signing in…" : $"Signing in at {uri.Host}…";
        if (uri?.AbsolutePath.Contains("/api/auth/loginsso", StringComparison.OrdinalIgnoreCase) == true)
        {
            var result = await ExtractTokenFromPageAsync();
            if (result == null) return;
            _completed = true;
            AuthenticationCompleted?.Invoke(this, result);
            Close();
            return;
        }

        if (!_tokenRequested && uri?.Host.Equals(new Uri(TdxSsoService.BuildEntryUrl(_baseUrl)).Host, StringComparison.OrdinalIgnoreCase) == true &&
            uri.AbsolutePath.Contains("/TDWorkManagement", StringComparison.OrdinalIgnoreCase))
        {
            _tokenRequested = true;
            Status.Text = "SSO complete; requesting TeamDynamix API token…";
            Browser.Source = new Uri(TdxSsoService.BuildLoginSsoUrl(_baseUrl));
        }
    }

    private async Task<TdxSsoResult?> ExtractTokenFromPageAsync()
    {
        var encoded = await Browser.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
        string token;
        try { token = JsonSerializer.Deserialize<string>(encoded) ?? ""; }
        catch { token = encoded.Trim().Trim('"'); }
        token = token.Trim().Trim('"');
        if (!TdxSsoService.LooksLikeJwt(token))
        {
            Status.Text = "TeamDynamix returned no bearer token after SSO.";
            return null;
        }
        var (name, email) = TdxSsoService.ExtractUserInfoFromJwt(token);
        return new TdxSsoResult { Success = true, Token = token, UserName = name, UserEmail = email, Expiry = TdxSsoService.ReadExpiry(token) };
    }

}
