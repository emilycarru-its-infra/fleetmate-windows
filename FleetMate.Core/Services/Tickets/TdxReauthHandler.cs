using System.Net;
using System.Net.Http.Headers;

namespace FleetMate.Core.Services.Tickets;

/// <summary>
/// Handles a 401 from TeamDynamix in one place. The bearer token the request
/// carried is reported as refused, and the recovery (a fresher saved session,
/// or one silent sign-in) runs once. The request is then sent once more with
/// the new token. A request marked <see cref="NoRetry"/> still reports the
/// refusal but is never re-sent, and neither is one whose body cannot be sent
/// twice.
/// </summary>
internal sealed class TdxReauthHandler : DelegatingHandler
{
    /// <summary>Set on a request that must only ever carry the token it was built with.</summary>
    public static readonly HttpRequestOptionsKey<bool> NoRetry = new("fleetmate.tdx.no-reauth-retry");

    private readonly Func<string, bool, CancellationToken, Task<string?>> _recover;

    /// <param name="recover">
    /// Called with the refused token and whether the request may be re-sent;
    /// returns the token to re-send it with, or null.
    /// </param>
    public TdxReauthHandler(Func<string, bool, CancellationToken, Task<string?>> recover) => _recover = recover;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized
            || request.Headers.Authorization is not { Scheme: "Bearer", Parameter: { Length: > 0 } refused })
            return response;

        var mayRetry = !(request.Options.TryGetValue(NoRetry, out var noRetry) && noRetry) && CanResend(request.Content);
        var fresh = await _recover(refused, mayRetry, ct);
        if (!mayRetry || string.IsNullOrEmpty(fresh) || fresh == refused) return response;

        response.Dispose();
        // Not disposed here: it shares the caller's body, which the caller disposes.
        var again = Copy(request, fresh);
        var retried = await base.SendAsync(again, ct);
        if (retried.StatusCode == HttpStatusCode.Unauthorized) await _recover(fresh, false, ct);
        return retried;
    }

    /// <summary>Bodies held in memory can be sent again; a stream may already be spent.</summary>
    internal static bool CanResend(HttpContent? content) => content switch
    {
        null => true,
        ByteArrayContent => true,
        MultipartContent multipart => multipart.All(CanResend),
        _ => false,
    };

    private static HttpRequestMessage Copy(HttpRequestMessage request, string token)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Content = request.Content,
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };
        foreach (var header in request.Headers)
        {
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        foreach (var option in request.Options)
            ((IDictionary<string, object?>)copy.Options)[option.Key] = option.Value;
        copy.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return copy;
    }
}
