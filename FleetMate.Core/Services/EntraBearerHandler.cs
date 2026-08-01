using System.Net;
using System.Net.Http.Headers;
using Serilog;

namespace FleetMate.Core.Services;

/// <summary>
/// Attaches a per-request Entra bearer token minted for <c>audience</c>.
///
/// Per-request rather than a header set once at construction: broker tokens live
/// ~an hour, and a long-lived service instance built at launch would otherwise
/// carry a stale token for the rest of the session. <see cref="EntraTokenSource"/>
/// caches, so the common case is a dictionary hit rather than a broker call.
///
/// On a 401 the token is dropped and the request retried once. A resource that
/// revokes or rotates early otherwise strands the caller behind a cached token
/// that will not recover until restart.
/// </summary>
public sealed class EntraBearerHandler : DelegatingHandler
{
    private readonly string _audience;
    private readonly Func<EntraTokenSource?> _source;

    public EntraBearerHandler(string audience, Func<EntraTokenSource?>? source = null)
        : base(new HttpClientHandler())
    {
        _audience = audience;
        _source = source ?? (() => EntraTokenSource.Shared);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var tokenSource = _source()
            ?? throw new EntraTokenException(_audience, "Entra sign-in is not configured");

        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await tokenSource.GetTokenAsync(_audience, cancellationToken));

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        Log.Debug("[entra] {Audience} returned 401 — refreshing the token and retrying once", _audience);
        response.Dispose();
        tokenSource.Invalidate();

        var retry = await CloneAsync(request, cancellationToken);
        retry.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await tokenSource.GetTokenAsync(_audience, cancellationToken));
        return await base.SendAsync(retry, cancellationToken);
    }

    /// <summary>
    /// A request message cannot be sent twice, so the retry needs a copy. Content
    /// is buffered because the original stream is already consumed by the first send.
    /// </summary>
    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
        };

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        foreach (var option in request.Options)
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;

        if (request.Content != null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(ct);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }
}
