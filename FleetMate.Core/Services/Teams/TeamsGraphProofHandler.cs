namespace FleetMate.Core.Services.Teams;

/// <summary>Attaches a silent delegated Graph proof only to the configured HTTPS Teams API, including the bearer retry.</summary>
internal sealed class TeamsGraphProofHandler : DelegatingHandler
{
    private readonly Func<EntraTokenSource?> _source;
    private readonly TeamsWorkspaceBinding? _workspace;

    internal TeamsGraphProofHandler(Func<EntraTokenSource?> source, HttpMessageHandler? transport = null, TeamsWorkspaceBinding? workspace = null)
        : base(transport ?? new HttpClientHandler { AllowAutoRedirect = false })
    {
        _source = source;
        _workspace = workspace;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Scheme != Uri.UriSchemeHttps)
            throw TeamsSearchException.Invalid("The Teams API must use HTTPS for delegated identity proof.");
        var source = _source() ?? throw new EntraTokenException(TeamsSearchService.GraphTokenScope, "Entra sign-in is not configured");
        try { _workspace?.Check(request.Headers.Authorization?.Parameter); }
        catch (TeamsSearchException) { source.Invalidate(); throw; }
        var token = await source.GetTokenAsync(TeamsSearchService.GraphTokenScope, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
            throw new EntraTokenException(TeamsSearchService.GraphTokenScope, "No silent Graph token is available for Teams access verification");
        request.Headers.Remove(TeamsSearchService.GraphTokenHeader);
        request.Headers.TryAddWithoutValidation(TeamsSearchService.GraphTokenHeader, token);
        return await base.SendAsync(request, cancellationToken);
    }
}
