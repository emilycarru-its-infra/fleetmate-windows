using System.Text;
using System.Text.Json;
using FleetMate.Core.Services.Projects;

namespace FleetMate.Core.Services;

/// <summary>
/// One OAuth2 authorization-code + PKCE exchange for an arbitrary Entra scope,
/// for the hidden-browser path of <see cref="EntraTokenSource"/>. The browser
/// half (a WebView2 carrying the device's primary refresh token, never shown)
/// lives in the desktop app; this is the part that needs no browser: the
/// authorize address, recognising the redirect, and redeeming the code.
///
/// The redirect is the loopback address public clients such as the Azure CLI
/// register. The hidden browser cancels that navigation and takes the code from
/// it, so nothing ever listens on, or loads, the address.
/// </summary>
public sealed class EntraAuthCodeFlow
{
    public const string RedirectUri = "http://localhost";

    private readonly EntraHeadlessRequest _request;
    private readonly string _verifier;
    private readonly HttpMessageHandler? _handler;

    public EntraAuthCodeFlow(EntraHeadlessRequest request, HttpMessageHandler? handler = null)
    {
        _request = request;
        _verifier = DevOpsSsoService.GenerateCodeVerifier();
        _handler = handler;
    }

    private string Endpoint(string leaf) =>
        $"https://login.microsoftonline.com/{Uri.EscapeDataString(_request.Tenant)}/oauth2/v2.0/{leaf}";

    /// <summary>The authorize address the hidden browser opens.</summary>
    public Uri AuthorizeUrl
    {
        get
        {
            var qs = new StringBuilder("?response_type=code&response_mode=query&code_challenge_method=S256");
            qs.Append("&client_id=").Append(Uri.EscapeDataString(_request.ClientId));
            qs.Append("&redirect_uri=").Append(Uri.EscapeDataString(RedirectUri));
            qs.Append("&scope=").Append(Uri.EscapeDataString(_request.Scope));
            qs.Append("&code_challenge=").Append(Uri.EscapeDataString(DevOpsSsoService.GenerateCodeChallenge(_verifier)));
            if (!string.IsNullOrWhiteSpace(_request.LoginHint))
                qs.Append("&login_hint=").Append(Uri.EscapeDataString(_request.LoginHint));
            // A work account: Entra skips asking whether it is a personal one.
            qs.Append("&domain_hint=organizations");
            return new Uri(Endpoint("authorize") + qs);
        }
    }

    /// <summary>True for the redirect that carries the code (or the error).</summary>
    public static bool IsRedirect(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp
        && uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        && (uri.AbsolutePath is "/" or "");

    /// <summary>Redeem the code the redirect carried, or throw with Entra's reason.</summary>
    public async Task<(string Token, DateTimeOffset ExpiresOn)> RedeemAsync(Uri redirect, CancellationToken ct = default)
    {
        var code = DevOpsSsoService.ExtractCode(redirect);
        if (string.IsNullOrEmpty(code))
            throw new InvalidOperationException(DevOpsSsoService.ExtractError(redirect) ?? "Entra returned no authorization code");

        using var http = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        using var response = await http.PostAsync(Endpoint("token"), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _request.ClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = _verifier,
            ["scope"] = _request.Scope,
        }), ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var root = json.RootElement;
        if (!response.IsSuccessStatusCode)
        {
            var reason = root.TryGetProperty("error_description", out var d) ? d.GetString()
                : root.TryGetProperty("error", out var e) ? e.GetString() : null;
            throw new InvalidOperationException($"the code exchange answered {(int)response.StatusCode}: {reason?.Split('\n')[0].Trim() ?? "no reason given"}");
        }

        var token = root.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("the code exchange returned no access token");
        var seconds = root.TryGetProperty("expires_in", out var x) switch
        {
            true when x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var n) => n,
            true when x.ValueKind == JsonValueKind.String && int.TryParse(x.GetString(), out var m) => m,
            _ => 3600,
        };
        return (token, DateTimeOffset.UtcNow.AddSeconds(seconds));
    }
}
