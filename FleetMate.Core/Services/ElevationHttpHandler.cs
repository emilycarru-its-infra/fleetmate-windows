using System.Net;
using System.Text;
using FleetMate.Core.Config;
using Serilog;

namespace FleetMate.Core.Services;

/// <summary>
/// Routes every Graph HttpClient request through an aze <see cref="ElevationSession"/>
/// as an in-session <c>az rest</c> call. The domain identity's token never leaves
/// Azure; only the JSON result comes back. Drop-in for the GraphService HttpClient,
/// so the ~20 existing call sites are unchanged.
/// </summary>
public sealed class ElevationHttpHandler : HttpMessageHandler
{
    private readonly ElevationSession _session;

    public ElevationHttpHandler(ElevationConfig config)
    {
        _session = new ElevationSession(config);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var method = request.Method.Method.ToLowerInvariant();
        var domain = RouteDomain(url);

        var sb = new StringBuilder();
        sb.Append("az rest --method ").Append(method).Append(" --uri ").Append(SingleQuote(url));
        if (request.Content != null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrEmpty(body))
                sb.Append(" --headers Content-Type=application/json --body ").Append(SingleQuote(body));
        }
        sb.Append(" -o json");

        try
        {
            var (output, code) = await _session.ExecAsync(domain, sb.ToString());
            // Log the real failure once, at the transport boundary. Callers turn a
            // non-success status into null/empty (e.g. "User not found"), so without
            // this the underlying error — a Graph 4xx/5xx surfaced by the in-session
            // az rest — would be invisible.
            if (code != 0)
                Log.Warning("Elevation {Domain} call to {Method} {Url} exited {Code}: {Body}",
                    domain.Slug(), method, url, code, Truncate(output));
            var status = code == 0 ? HttpStatusCode.OK : HttpStatusCode.BadGateway;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(output, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }
        catch (Exception ex)
        {
            // Elevation infra failure (not configured, container/exec error, network).
            // The full detail is logged at debug level only — ex.Message can still carry
            // elevated output in edge cases, and GraphService re-logs the response body.
            // The body callers receive is therefore a fixed, non-sensitive string.
            Log.Debug(ex, "Elevation {Domain} call to {Method} {Url} failed", domain.Slug(), method, url);
            Log.Warning("Elevation {Domain} call to {Method} {Url} failed (detail at debug level)",
                domain.Slug(), method, url);
            return new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("Elevation request failed; see FleetMate debug log for detail."),
                RequestMessage = request,
            };
        }
    }

    /// Intune (deviceManagement / deviceAppManagement) → devices; everything else
    /// (users/groups/directory) → identity.
    private static GraphDomain RouteDomain(string url)
    {
        var lower = url.ToLowerInvariant();
        if (lower.Contains("/devicemanagement/") || lower.Contains("/deviceappmanagement/"))
            return GraphDomain.Devices;
        return GraphDomain.Identity;
    }

    // Single-quote so the container shell does not expand $top/$filter/$ref/etc.
    private static string SingleQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    // Keep log lines bounded — Graph error bodies can be large.
    private static string Truncate(string s)
    {
        s = s?.Trim() ?? "";
        return s.Length > 600 ? s[..600] + "…" : s;
    }
}
