using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Services.Activity;

namespace FleetMate.Core.Services.Inventory;

/// <summary>
/// The inventory system FleetMate's inventory operations run against.
/// Snipe-IT (the fork) is the system of record until Cellarum replaces it.
/// Switching is one setting, <c>inventory_backend</c>; the operation names the
/// CLI and agents use stay the same on either side. Mirrors InventoryApi.swift.
/// </summary>
public enum InventoryBackend
{
    Snipe,
    Cellarum,
}

public static class InventoryBackends
{
    public static string DisplayName(this InventoryBackend backend) =>
        backend == InventoryBackend.Snipe ? "Snipe-IT" : "Cellarum";

    public static string Key(this InventoryBackend backend) =>
        backend == InventoryBackend.Snipe ? "snipe" : "cellarum";

    public static InventoryBackend? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "snipe" => InventoryBackend.Snipe,
        "cellarum" => InventoryBackend.Cellarum,
        _ => null,
    };

    /// <summary>The configured backend; Snipe-IT when nothing is set.</summary>
    public static InventoryBackend Configured(FleetMateConfig config)
    {
        var raw = (config.InventoryBackend ?? "").Trim().ToLowerInvariant();
        if (raw is "" or "snipe-it" or "snipeit") return InventoryBackend.Snipe;
        return Parse(raw) ?? throw new InvalidOperationException($"Unknown inventory_backend '{raw}'. Use snipe or cellarum.");
    }
}

/// <summary>
/// Raw, authenticated access to the active inventory backend's REST API.
/// Snipe-IT authenticates with an Entra bearer token for <c>snipe_oidc_audience</c>
/// or the API key; Cellarum with a bearer token for <c>cellarum_oidc_audience</c>
/// or a scoped key sent as X-API-Key.
/// </summary>
public sealed class InventoryApi : IDisposable
{
    public InventoryBackend Backend { get; }
    public string Root { get; }
    private readonly HttpClient _client;

    public sealed record Response(int Status, byte[] Data, string? ContentType);

    public InventoryApi(FleetMateConfig config, InventoryBackend? backend = null)
    {
        Backend = backend ?? InventoryBackends.Configured(config);
        string? baseUrl, audience, apiKey;
#pragma warning disable CS0618 // the Snipe key stays a fallback until every machine has an audience
        if (Backend == InventoryBackend.Snipe)
        {
            (baseUrl, audience, apiKey) = (config.SnipeUrl, config.SnipeOidcAudience, config.SnipeApiKey);
        }
#pragma warning restore CS0618
        else
        {
            (baseUrl, audience, apiKey) = (config.CellarumUrl, config.CellarumOidcAudience, config.CellarumApiKey);
        }

        var url = ServiceUri.Normalize(baseUrl);
        if (url.Length == 0)
            throw new InvalidOperationException(
                $"{Backend.DisplayName()} is not configured: set {(Backend == InventoryBackend.Snipe ? "snipe_url" : "cellarum_url")}.");
        if (!url.EndsWith("/api/v1", StringComparison.Ordinal)) url += "/api/v1";
        Root = url;

        var usesOidc = !string.IsNullOrWhiteSpace(audience);
        if (!usesOidc && string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                $"{Backend.DisplayName()} is not configured: set {Backend.Key()}_oidc_audience or {Backend.Key()}_api_key.");

        // Redirects are not followed: a redirect to another host would carry
        // the API key (or bearer) with it. An inventory API never needs one.
        static HttpClientHandler NoRedirects() => new() { AllowAutoRedirect = false };
        _client = usesOidc
            ? new HttpClient(new ActivityLogHandler("Inventory", new EntraBearerHandler(audience!) { InnerHandler = NoRedirects() }))
            : new HttpClient(new ActivityLogHandler("Inventory", NoRedirects()));
        _client.Timeout = TimeSpan.FromSeconds(120);
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!usesOidc)
        {
            if (Backend == InventoryBackend.Snipe)
                _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            else
                _client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        }
    }

    /// <summary>
    /// Send one request. <paramref name="path"/> is relative to the API root and may
    /// carry a query string. A non-2xx answer throws; so does Snipe-IT's 200 answer
    /// whose body says <c>"status": "error"</c>, which is how it reports refusals.
    /// </summary>
    public async Task<Response> SendAsync(string method, string path,
        IReadOnlyList<KeyValuePair<string, string>>? query = null, HttpContent? content = null,
        CancellationToken ct = default)
    {
        if (path.StartsWith('/') || path.Contains("://")
            || path.Split('?')[0].Split('/').Any(part => part is ".." or "."))
            throw new ArgumentException($"Not a relative API path: {path}");

        var url = $"{Root}/{path}";
        if (query is { Count: > 0 })
        {
            var extra = string.Join("&", query.Select(item =>
                $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));
            url += (url.Contains('?') ? "&" : "?") + extra;
        }

        using var request = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), url) { Content = content };
        using var response = await _client.SendAsync(request, ct);
        var data = await response.Content.ReadAsByteArrayAsync(ct);
        var status = (int)response.StatusCode;
        var text = Encoding.UTF8.GetString(data);
        if (status is < 200 or >= 300)
            throw new HttpRequestException($"HTTP {status}: {Truncate(text)}");
        if (Backend == InventoryBackend.Snipe && IsSnipeRefusal(data))
            throw new InvalidOperationException($"The request was refused: {Truncate(text)}");
        return new Response(status, data, response.Content.Headers.ContentType?.ToString());
    }

    /// <summary>
    /// Attach a file to a record. Snipe-IT takes a multipart upload on the record;
    /// Cellarum takes base64 bytes on /attachments and needs the holding domain.
    /// </summary>
    public async Task<Response> UploadAsync(string file, string snipeRecord, string cellarumRecord, string id,
        string? notes, string? domain, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(file, ct);
        var name = Path.GetFileName(file);
        var mediaType = MediaType(Path.GetExtension(file));
        if (Backend == InventoryBackend.Snipe)
        {
            var form = new MultipartFormDataContent($"FleetMate-{Guid.NewGuid()}");
            if (notes is not null) form.Add(new StringContent(notes), "notes");
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            form.Add(part, "file[]", name.Replace("\"", ""));
            return await SendAsync("POST", $"{snipeRecord}/{Escape(id)}/files", content: form, ct: ct);
        }

        var fields = new Dictionary<string, object?>
        {
            ["record_kind"] = cellarumRecord,
            ["record_id"] = id,
            ["filename"] = name,
            ["media_type"] = mediaType,
            ["content_base64"] = Convert.ToBase64String(bytes),
        };
        if (domain is not null) fields["domain_id"] = domain;
        if (notes is not null) fields["notes"] = notes;
        var json = new StringContent(JsonSerializer.Serialize(fields), Encoding.UTF8, "application/json");
        return await SendAsync("POST", "attachments", content: json, ct: ct);
    }

    public static string Escape(string value) => Uri.EscapeDataString(value);

    private static bool IsSnipeRefusal(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String && status.GetString() == "error";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Truncate(string text) => text.Length > 2000 ? text[..2000] : text;

    internal static string MediaType(string extension) => extension.TrimStart('.').ToLowerInvariant() switch
    {
        "pdf" => "application/pdf",
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "txt" or "log" => "text/plain",
        "csv" => "text/csv",
        "json" => "application/json",
        "doc" => "application/msword",
        "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "xls" => "application/vnd.ms-excel",
        "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "zip" => "application/zip",
        _ => "application/octet-stream",
    };

    public void Dispose() => _client.Dispose();
}
