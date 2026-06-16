using System.Net;
using System.Text;

namespace FleetMate.Core.Services;

/// <summary>
/// Routes every Graph HttpClient request through an aze <see cref="ElevationSession"/>
/// as an in-session <c>az rest</c> call. The domain identity's token never leaves
/// Azure; only the JSON result comes back. Drop-in for the GraphService HttpClient,
/// so the ~20 existing call sites are unchanged.
/// </summary>
public sealed class ElevationHttpHandler : HttpMessageHandler
{
    private readonly ElevationSession _session = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var method = request.Method.Method.ToLowerInvariant();
        var domain = RouteDomain(url);

        var sb = new StringBuilder();
        sb.Append("az rest --method ").Append(method).Append(" --url ").Append(SingleQuote(url));
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
            var status = code == 0 ? HttpStatusCode.OK : HttpStatusCode.BadGateway;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(output, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }
        catch (Exception ex)
        {
            return new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent(ex.Message),
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
}
