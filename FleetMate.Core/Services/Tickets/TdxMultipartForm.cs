using System.Net;
using System.Text;

namespace FleetMate.Core.Services.Tickets;

/// <summary>
/// A <c>multipart/form-data</c> body carrying one file, the shape TDX's
/// attachment upload expects: a single part named <c>file</c> with the
/// original file name.
///
/// Built by hand rather than with <see cref="System.Net.Http.MultipartFormDataContent"/>
/// so the exact bytes on the wire can be pinned by a unit test, and so a file
/// name can never break out of its header line.
/// </summary>
public sealed class TdxMultipartForm
{
    public string Boundary { get; }
    public byte[] Body { get; }

    /// <summary>The <c>Content-Type</c> header value that names this body's boundary.</summary>
    public string ContentType => $"multipart/form-data; boundary={Boundary}";

    public TdxMultipartForm(string fileName, byte[] data, string? mimeType = null,
        string fieldName = "file", string? boundary = null)
    {
        Boundary = boundary ?? "FleetMate-" + Guid.NewGuid().ToString("D");
        var name = SanitizedFileName(fileName);
        var type = mimeType ?? MimeType(name);

        var header = new StringBuilder();
        header.Append("--").Append(Boundary).Append("\r\n");
        // RFC 7578: a name outside ASCII travels percent-encoded in filename*,
        // with a plain ASCII fallback for parsers that only read filename.
        if (name.All(c => c < 128))
        {
            header.Append($"Content-Disposition: form-data; name=\"{fieldName}\"; filename=\"{Quoted(name)}\"\r\n");
        }
        else
        {
            var fallback = new string(name.Select(c => c < 128 ? c : '_').ToArray());
            header.Append($"Content-Disposition: form-data; name=\"{fieldName}\"; filename=\"{Quoted(fallback)}\"; filename*=UTF-8''{Rfc5987Encode(name)}\r\n");
        }
        header.Append("Content-Type: ").Append(type).Append("\r\n\r\n");

        var head = Encoding.UTF8.GetBytes(header.ToString());
        var tail = Encoding.UTF8.GetBytes($"\r\n--{Boundary}--\r\n");
        Body = new byte[head.Length + data.Length + tail.Length];
        head.CopyTo(Body, 0);
        data.CopyTo(Body, head.Length);
        tail.CopyTo(Body, head.Length + data.Length);
    }

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain", [".log"] = "text/plain", [".csv"] = "text/csv",
        [".htm"] = "text/html", [".html"] = "text/html", [".xml"] = "application/xml",
        [".json"] = "application/json", [".pdf"] = "application/pdf", [".rtf"] = "application/rtf",
        [".zip"] = "application/zip",
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".bmp"] = "image/bmp", [".webp"] = "image/webp", [".heic"] = "image/heic",
        [".tif"] = "image/tiff", [".tiff"] = "image/tiff", [".svg"] = "image/svg+xml",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".mp4"] = "video/mp4", [".mov"] = "video/quicktime",
        [".mp3"] = "audio/mpeg", [".m4a"] = "audio/mp4", [".wav"] = "audio/wav",
    };

    /// <summary>The MIME type for a file name's extension, or a generic binary type.</summary>
    public static string MimeType(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(ext) && MimeTypes.TryGetValue(ext, out var mime)
            ? mime
            : "application/octet-stream";
    }

    /// <summary>
    /// Only the leaf name, on one line: a header value must stay on its own
    /// line, and TDX should never see a local folder.
    /// </summary>
    internal static string SanitizedFileName(string fileName)
    {
        var leaf = fileName ?? "";
        var slash = leaf.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0) leaf = leaf[(slash + 1)..];
        leaf = new string(leaf.Where(c => c is not ('\r' or '\n' or '\u0085' or (char)0x2028 or (char)0x2029)).ToArray()).Trim();
        return leaf.Length == 0 ? "attachment" : leaf;
    }

    private static string Quoted(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>Percent-encode everything but RFC 5987's <c>attr-char</c>.</summary>
    internal static string Rfc5987Encode(string value)
    {
        const string extra = "!#$&+-.^_`|~";
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (b < 128 && (char.IsAsciiLetterOrDigit(c) || extra.Contains(c))) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}

/// <summary>Why one file did not reach the ticket, phrased for the person who chose it.</summary>
public sealed class TdxUploadException : Exception
{
    public string FileName { get; }
    /// <summary>The HTTP status, or 0 when the request never got an answer.</summary>
    public int StatusCode { get; }

    public TdxUploadException(string fileName, int statusCode, string detail = "", Exception? inner = null)
        : base(Describe(fileName, statusCode, detail, inner), inner)
    {
        FileName = fileName;
        StatusCode = statusCode;
    }

    private static string Describe(string fileName, int status, string detail, Exception? inner)
    {
        var trimmed = (detail ?? "").Trim();
        var reason = status switch
        {
            0 => inner?.Message ?? "the connection failed",
            (int)HttpStatusCode.Unauthorized => "the TeamDynamix session has expired",
            (int)HttpStatusCode.Forbidden => "you don't have permission to attach files to this ticket",
            (int)HttpStatusCode.RequestEntityTooLarge => "the file is larger than TeamDynamix accepts",
            (int)HttpStatusCode.UnsupportedMediaType => "TeamDynamix doesn't accept this file type",
            (int)HttpStatusCode.TooManyRequests => "TeamDynamix is rate-limiting uploads; try again in a minute",
            _ => trimmed.Length == 0 || trimmed.Length > 200 ? $"TeamDynamix answered {status}" : trimmed,
        };
        return $"{fileName}: {reason}";
    }
}
