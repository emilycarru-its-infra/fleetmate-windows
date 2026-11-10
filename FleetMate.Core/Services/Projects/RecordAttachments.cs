using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace FleetMate.Core.Services.Projects;

public sealed record RecordAttachment(string Name, string Url, string OriginalUrl);
public sealed record StagedRecordAttachment(string Path, string ContentType, string OriginalUrl) : IDisposable
{
    public void Dispose()
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        if (TryDelete(directory)) return;
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                await Task.Delay(500);
                if (TryDelete(directory)) return;
            }
        });
    }
    private static bool TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

/// <summary>Provider URL validation and bounded, redirect-aware attachment downloads.</summary>
public static class RecordAttachments
{
    public const long MaximumBytes = 50 * 1024 * 1024;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex HtmlLinks = new("(?:href|src)\\s*=\\s*[\\\"'](?<url>[^\\\"']+)[\\\"']", RegexOptions.IgnoreCase, MatchTimeout);
    private static readonly Regex MarkdownLinks = new(@"!?\[(?<name>[^\]]*)\]\((?<url>https://[^\s)]+)(?:\s+[^)]*)?\)", RegexOptions.IgnoreCase, MatchTimeout);
    private static readonly Regex AssetId = new(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase, MatchTimeout);

    public static bool IsWeb(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);
    public static bool SameOrigin(Uri first, Uri second) => first.Scheme == second.Scheme && first.Host.Equals(second.Host, StringComparison.OrdinalIgnoreCase) && first.Port == second.Port;
    public static bool IsDevOpsAttachment(Uri uri, Uri organization) => IsWeb(uri) && SameOrigin(uri, organization)
        && uri.AbsolutePath.StartsWith(organization.AbsolutePath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)
        && Regex.IsMatch(uri.AbsolutePath, @"/_apis/wit/attachments/[0-9a-f-]{36}$", RegexOptions.IgnoreCase, MatchTimeout);
    public static bool IsGitHubAsset(Uri uri) => IsWeb(uri) && uri.IsDefaultPort && (uri.Host.ToLowerInvariant() switch
    {
        "github.com" => uri.AbsolutePath.StartsWith("/user-attachments/", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(uri.AbsolutePath, @"^/[^/]+/[^/]+/(?:assets|files)/", RegexOptions.IgnoreCase, MatchTimeout),
        "user-images.githubusercontent.com" or "private-user-images.githubusercontent.com" or "objects.githubusercontent.com"
            or "github-production-user-asset-6210df.s3.amazonaws.com" => true,
        _ => false,
    });
    public static bool IsDevOpsAsset(Uri uri) => IsWeb(uri) && uri.IsDefaultPort && uri.Host.EndsWith(".vsblob.vsassets.io", StringComparison.OrdinalIgnoreCase);

    public static List<RecordAttachment> Discover(string? content, Func<Uri, bool> allowed, string? originalFallback = null)
    {
        if (string.IsNullOrEmpty(content)) return new();
        var input = content[..Math.Min(content.Length, 1024 * 1024)];
        var result = new List<RecordAttachment>();
        try
        {
            foreach (var match in MarkdownLinks.Matches(input).Cast<Match>().Concat(HtmlLinks.Matches(input).Cast<Match>()))
            {
                var raw = WebUtility.HtmlDecode(match.Groups["url"].Value);
                if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || !IsWeb(uri) || !allowed(uri)) continue;
                var name = match.Groups["name"].Value;
                if (string.IsNullOrWhiteSpace(name)) name = Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? "Attachment");
                result.Add(new(name, raw, originalFallback ?? raw));
                if (result.Count == 100) break;
            }
        }
        catch (RegexMatchTimeoutException) { }
        return result.DistinctBy(a => a.Url).ToList();
    }

    public static List<RecordAttachment> GitHub(string? markdown, string? renderedHtml, string browserUrl)
    {
        var raw = Discover(markdown, IsGitHubAsset);
        var rendered = Discover(renderedHtml, IsGitHubAsset, browserUrl);
        if (rendered.Count == 0) return raw;
        return rendered.Select(asset =>
        {
            var id = AssetId.Match(new Uri(asset.Url).AbsolutePath).Value;
            var original = raw.FirstOrDefault(a => id.Length > 0 && a.Url.Contains(id, StringComparison.OrdinalIgnoreCase))
                ?? raw.FirstOrDefault(a => Uri.UnescapeDataString(new Uri(a.Url).AbsolutePath).EndsWith("/" + asset.Name, StringComparison.OrdinalIgnoreCase));
            return original == null ? asset : asset with { Name = original.Name, OriginalUrl = original.Url };
        }).DistinctBy(a => a.Url).ToList();
    }

    public static Task<StagedRecordAttachment> DownloadAsync(RecordAttachment attachment, Uri credentialOrigin,
        string? token, Func<Uri, bool> allowed, CancellationToken ct = default, HttpMessageHandler? handler = null) =>
        DownloadWithDeadlineAsync(attachment, credentialOrigin, token, allowed, TimeSpan.FromSeconds(60), ct, handler);

    internal static async Task<StagedRecordAttachment> DownloadWithDeadlineAsync(RecordAttachment attachment, Uri credentialOrigin,
        string? token, Func<Uri, bool> allowed, TimeSpan deadline, CancellationToken ct = default, HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate(attachment.Url, UriKind.Absolute, out var uri) || !IsWeb(uri) || !allowed(uri))
            throw new InvalidOperationException("This attachment address is not supported.");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        operation.CancelAfter(deadline);
        ct = operation.Token;
        using var client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(60) };
        var canAuthorize = SameOrigin(uri, credentialOrigin);
        for (var hop = 0; hop <= 5; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("FleetMate");
            if (canAuthorize && !string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (response.Headers.Location == null || hop == 5) throw new HttpRequestException("Attachment redirect limit reached.");
                var next = new Uri(uri, response.Headers.Location);
                if (!IsWeb(next) || !allowed(next)) throw new HttpRequestException("Attachment redirected to an unsupported address.");
                canAuthorize &= SameOrigin(uri, next);
                uri = next;
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Attachment download returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
            var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "application/octet-stream";
            if (type is "text/html" or "application/xhtml+xml") throw new InvalidDataException("The attachment returned a web page instead of a file.");
            if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("Attachment exceeds the 50 MB preview limit.");
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FleetMateAttachments", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var name = "Attachment" + SafeExtension(attachment.Name, type);
            var path = System.IO.Path.Combine(directory, name);
            try
            {
                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                await using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, useAsync: true))
                {
                    var buffer = new byte[16384];
                    long total = 0;
                    var read = await source.ReadAtLeastAsync(buffer, 512, throwOnEndOfStream: false, cancellationToken: ct);
                    while (read != 0)
                    {
                        total += read;
                        if (total > MaximumBytes) throw new InvalidDataException("Attachment exceeds the 50 MB preview limit.");
                        if (total == read && LooksLikeHtml(buffer.AsSpan(0, read))) throw new InvalidDataException("The attachment returned a sign-in page instead of a file.");
                        await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                        read = await source.ReadAsync(buffer, ct);
                    }
                }
                return new(path, type, attachment.OriginalUrl);
            }
            catch { new StagedRecordAttachment(path, type, attachment.OriginalUrl).Dispose(); throw; }
        }
        throw new HttpRequestException("Attachment redirect limit reached.");
    }

    private static bool LooksLikeHtml(ReadOnlySpan<byte> bytes)
    {
        var start = Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length, 512)]).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        return start.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || start.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    internal static string SafeExtension(string name, string contentType)
    {
        var inferred = contentType.ToLowerInvariant() switch
        {
            "application/pdf" => ".pdf", "image/png" => ".png", "image/jpeg" => ".jpg",
            "image/gif" => ".gif", "image/bmp" => ".bmp", "image/tiff" => ".tif",
            "text/plain" => ".txt", "text/csv" => ".csv", "application/json" => ".json",
            _ => null,
        };
        if (inferred != null) return inferred;
        var extension = System.IO.Path.GetExtension(SafeName(name)).ToLowerInvariant();
        return extension is ".pdf" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff"
            or ".txt" or ".md" or ".log" or ".csv" or ".json" or ".yaml" or ".yml" or ".ini" ? extension : ".bin";
    }

    public static string SafeName(string name)
    {
        var file = System.IO.Path.GetFileName(name.Replace('\\', '/'));
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        file = new string(file.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(file) ? "Attachment" : file[..Math.Min(file.Length, 160)];
    }
}
