using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FleetMate.Core.Config;

namespace FleetMate.Core.Services;

/// <summary>Elevation domain → backing managed identity. Mirrors the aze tool.</summary>
public enum GraphDomain { Terraform, Devices, Identity, Systems, Cloud }

public static class GraphDomainExtensions
{
    public static string Slug(this GraphDomain d) => d switch
    {
        GraphDomain.Terraform => "terraform",
        GraphDomain.Devices => "devices",
        GraphDomain.Identity => "identity",
        GraphDomain.Systems => "systems",
        GraphDomain.Cloud => "cloud",
        _ => "devices"
    };

    /// <summary>Structural aze domain name (PascalCase). The org-specific managed-identity
    /// name is built as {ElevationConfig.IdentityPrefix}{DomainName} — see ElevationSession.</summary>
    public static string DomainName(this GraphDomain d) => d switch
    {
        GraphDomain.Terraform => "Terraform",
        GraphDomain.Devices => "Devices",
        GraphDomain.Identity => "Identity",
        GraphDomain.Systems => "Systems",
        GraphDomain.Cloud => "Cloud",
        _ => "Devices"
    };
}

public sealed class ElevationException : Exception
{
    public ElevationException(string message) : base(message) { }
}

/// <summary>
/// Native reimplementation of the aze elevation protocol (no external aze
/// script). Container lifecycle and the exec handshake go through the az CLI
/// (kept deliberately); only the raw exec websocket is driven natively.
///
/// Security model unchanged: rides the operator's own az login on a compliant
/// device plus elevation operators-group membership. The app is not a privilege.
/// </summary>
public sealed class ElevationSession
{
    private const string ExecApiVersion = "2023-05-01";  // Azure ARM containers exec API version (global)

    // All aze infrastructure (resource group, container image, transcript account,
    // identity prefix, TTL) is env/org-specific and comes from app settings — no
    // hardcoded defaults. See ElevationConfig.
    private readonly ElevationConfig _config;

    public ElevationSession(ElevationConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    private void EnsureConfigured()
    {
        if (!_config.IsConfigured)
            throw new ElevationException(
                "aze elevation is not configured. Set elevation.resourceGroup, elevation.acrImage, " +
                "elevation.transcriptAccount, and elevation.identityPrefix in your FleetMate config.");
    }

    private string IdentityName(GraphDomain domain) => _config.IdentityPrefix + domain.DomainName();

    // Serializes EnsureSessionAsync so concurrent callers (bulk Task.WhenAll,
    // parallel HttpClient requests) don't race to create the same container.
    private readonly SemaphoreSlim _ensureGate = new(1, 1);

    private static string SessionName(GraphDomain domain)
    {
        var user = new string(Environment.UserName.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (user.Length > 20) user = user[..20];
        return $"aze-{domain.Slug()}-{user}";
    }

    // MARK: container lifecycle (via az)

    public async Task EnsureSessionAsync(GraphDomain domain, int? ttlHours = null)
    {
        EnsureConfigured();
        await _ensureGate.WaitAsync();
        try
        {
            await EnsureSessionCoreAsync(domain, ttlHours ?? _config.DefaultTtlHours);
        }
        finally
        {
            _ensureGate.Release();
        }
    }

    private async Task EnsureSessionCoreAsync(GraphDomain domain, int ttlHours)
    {
        var name = SessionName(domain);

        var show = await RunAzAsync("container", "show", "--resource-group", _config.ResourceGroup!, "--name", name, "--query", "instanceView.state", "-o", "tsv");
        var state = show.Out.Trim();
        if (state == "Running") return;

        if (!string.IsNullOrEmpty(state))
            await RunAzAsync("container", "delete", "--resource-group", _config.ResourceGroup!, "--name", name, "--yes", "-o", "none");

        var idShow = await RunAzAsync("identity", "show", "--resource-group", _config.ResourceGroup!, "--name", IdentityName(domain), "--query", "[id,clientId]", "-o", "tsv");
        var parts = idShow.Out.Split(new[] { '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ElevationException($"Could not resolve managed identity for domain {domain.Slug()}");
        var identityId = parts[0];
        var clientId = parts[1];

        var sleepSeconds = ttlHours * 3600;
        var commandLine = $"/bin/bash -c 'az login --identity --client-id {clientId} --allow-no-subscriptions -o none; sleep {sleepSeconds}'";

        var create = await RunAzAsync(
            "container", "create",
            "--resource-group", _config.ResourceGroup!,
            "--name", name,
            "--image", _config.AcrImage!,
            "--assign-identity", identityId,
            "--acr-identity", identityId,
            "--os-type", "Linux",
            "--cpu", "1",
            "--memory", "1.5",
            "--restart-policy", "Never",
            "--command-line", commandLine,
            "--environment-variables", $"ELEVATION_CLIENT_ID={clientId}", $"ELEVATION_TRANSCRIPT_ACCOUNT={_config.TranscriptAccount}",
            "--output", "none");
        if (create.Code != 0)
            throw new ElevationException($"Failed to create elevation session: {(string.IsNullOrEmpty(create.Err) ? create.Out : create.Err)}");

        var idLookup = await RunAzAsync("container", "show", "--resource-group", _config.ResourceGroup!, "--name", name, "--query", "id", "-o", "tsv");
        var containerId = idLookup.Out.Trim();
        if (!string.IsNullOrEmpty(containerId))
        {
            var expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + sleepSeconds;
            await RunAzAsync("resource", "tag", "--ids", containerId, "--tags", "elevation=true", $"domain={domain.Slug()}", $"expires={expires}", "--output", "none");
        }
    }

    // MARK: exec (handshake via az, raw websocket native)

    /// <summary>
    /// Runs a single bash <paramref name="command"/> inside the elevated container and
    /// returns its stdout and exit code. This is a general-purpose primitive that will
    /// execute ANY bash string with the domain managed identity's privileges.
    ///
    /// INVARIANT — callers MUST pass only sanctioned <c>az rest</c> commands built by
    /// <see cref="ElevationHttpHandler"/> (the sole intended caller). Never pass
    /// free-form strings, user input, or interpolated identifiers that were not
    /// single-quote-escaped. Never use this to extract a raw token
    /// (e.g. <c>az account get-access-token</c>) — the elevation model deliberately
    /// keeps the identity's token inside Azure; only Graph JSON results come back.
    ///
    /// A lightweight defensive guard enforces the shape (leading <c>az rest</c>) and
    /// rejects obvious token-extraction; it is a backstop, not a substitute for the
    /// invariant above.
    /// </summary>
    public async Task<(string Out, int Code)> ExecAsync(GraphDomain domain, string command)
    {
        GuardSanctionedCommand(command);
        await EnsureSessionAsync(domain);
        var name = SessionName(domain);

        var account = await RunAzAsync("account", "show", "--query", "id", "-o", "tsv");
        var sub = account.Out.Trim();
        if (account.Code != 0 || string.IsNullOrEmpty(sub))
            throw new ElevationException($"Not logged in to az (run az login). {account.Err.Trim()}");
        var uri = $"https://management.azure.com/subscriptions/{sub}/resourceGroups/{_config.ResourceGroup!}/providers/Microsoft.ContainerInstance/containerGroups/{name}/containers/{name}/exec?api-version={ExecApiVersion}";
        var body = "{\"command\":\"/bin/bash\",\"terminalSize\":{\"rows\":24,\"cols\":500}}";

        // The PTY stream occasionally drops bytes on large payloads; the base64
        // envelope detects that as a decode failure, and a fresh exec is cheap
        // compared to failing the whole call.
        ElevationException? lastCorruption = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            if (attempt > 1) await Task.Delay(250 * attempt);
            var execResp = await RunAzAsync("rest", "--method", "post", "--uri", uri, "--body", body);
            if (execResp.Code != 0)
                throw new ElevationException($"Exec handshake failed: {(string.IsNullOrEmpty(execResp.Err) ? execResp.Out : execResp.Err)}");

            using var doc = JsonDocument.Parse(execResp.Out);
            var wsUri = doc.RootElement.GetProperty("webSocketUri").GetString();
            var password = doc.RootElement.GetProperty("password").GetString();
            if (wsUri == null || password == null) throw new ElevationException("Exec response missing webSocketUri/password");

            var (payload, code) = await RunWebSocketAsync(new Uri(wsUri), password, command);
            try
            {
                return (DecodeBase64Payload(payload), code);
            }
            catch (ElevationException ex)
            {
                lastCorruption = ex;
                Serilog.Log.Warning("Elevation output corrupted (attempt {Attempt}/5); retrying", attempt);
            }
        }
        throw lastCorruption!;
    }

    // Backstop for the ExecAsync invariant: only sanctioned `az rest` calls are allowed
    // through, and never a token-extraction command. See the ExecAsync doc comment.
    private static void GuardSanctionedCommand(string command)
    {
        var trimmed = command?.TrimStart() ?? "";
        if (!trimmed.StartsWith("az rest ", StringComparison.Ordinal))
            throw new ElevationException("Elevation exec is restricted to sanctioned 'az rest' commands.");
        if (trimmed.Contains("get-access-token", StringComparison.OrdinalIgnoreCase))
            throw new ElevationException("Elevation exec must not extract tokens.");
    }

    private static readonly Regex AnsiRe = new(@"\x1b\[[0-9;?]*[a-zA-Z]", RegexOptions.Compiled);
    private static readonly Regex EndRe = new(@"<<<AZE_END:\d+>>>", RegexOptions.Compiled);
    private static readonly Regex MarkerRe = new(@"<<<AZE_BEGIN>>>\n(.*)\n?<<<AZE_END:(\d+)>>>", RegexOptions.Singleline | RegexOptions.Compiled);

    private static async Task<(string Out, int Code)> RunWebSocketAsync(Uri uri, string password, string command)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(uri, CancellationToken.None);

        async Task SendText(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        await SendText(password);
        await SendText("stty -echo\n");
        await Task.Delay(2000);
        // The exec channel is a PTY: any output line longer than the terminal
        // width is hard-wrapped with injected newlines, which silently corrupts
        // large JSON payloads (a wrap mid-token breaks parsing ~100 records in).
        // Shipping the output as a single base64 stream makes it wrap-proof —
        // injected newlines are stripped before decoding on our side.
        // gzip before base64: Graph JSON compresses ~10x, and every byte saved
        // is a byte the flaky PTY stream cannot drop.
        await SendText($"printf '\\n<<<AZE_BEGIN>>>\\n'; ( {command} ) | gzip -c | base64 -w 400; printf '\\n<<<AZE_END:%d>>>\\n' \"${{PIPESTATUS[0]}}\"; exit\n");

        var sb = new StringBuilder();
        var buffer = new byte[8192];
        var deadline = DateTime.UtcNow.AddHours(1);
        while (DateTime.UtcNow < deadline)
        {
            WebSocketReceiveResult result;
            try { result = await ws.ReceiveAsync(buffer, CancellationToken.None); }
            catch { break; }
            if (result.MessageType == WebSocketMessageType.Close) break;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (EndRe.IsMatch(AnsiRe.Replace(sb.ToString(), ""))) break;
        }
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }

        return ParseExecOutput(sb.ToString());
    }

    /// <summary>
    /// Decode the base64 stream produced in-session. PTY wrapping may have
    /// injected newlines (or other whitespace) anywhere in the stream, so
    /// everything outside the base64 alphabet is dropped before decoding.
    /// </summary>
    private static string DecodeBase64Payload(string payload)
    {
        var clean = Regex.Replace(payload, @"[^A-Za-z0-9+/=]", "");
        if (clean.Length == 0) return "";
        try
        {
            var bytes = Convert.FromBase64String(clean);
            if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
            {
                using var input = new System.IO.MemoryStream(bytes);
                using var gz = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
                using var output = new System.IO.MemoryStream();
                gz.CopyTo(output);
                return Encoding.UTF8.GetString(output.ToArray());
            }
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            throw new ElevationException(
                $"Elevation output was not valid base64 ({clean.Length} chars) — the session stream may be corrupted.");
        }
        catch (System.IO.InvalidDataException)
        {
            throw new ElevationException(
                $"Elevation output failed gzip decompression ({clean.Length} chars) — the session stream may be corrupted.");
        }
    }

    /// <summary>
    /// Extract command output and exit code from a raw aze session stream:
    /// strip ANSI escapes, normalize newlines, then pull the payload between the
    /// AZE_BEGIN/AZE_END markers. Throws if the end marker is absent.
    /// </summary>
    internal static (string Out, int Code) ParseExecOutput(string raw)
    {
        var text = AnsiRe.Replace(raw, "").Replace("\r\n", "\n").Replace("\r", "\n");
        var m = MarkerRe.Match(text);
        if (!m.Success)
        {
            // Never embed the raw session body — it can carry privileged Graph JSON.
            // Report only a non-sensitive summary; the full run is in the transcript.
            // A bounded tail is available for local troubleshooting behind an opt-in
            // debug flag (FLEETMATE_ELEVATION_DEBUG=1); withheld by default.
            var summary = $"Could not find output markers in session output ({text.Length} chars, no end marker/exit code — output withheld, see the elevation transcript).";
            if (string.Equals(Environment.GetEnvironmentVariable("FLEETMATE_ELEVATION_DEBUG"), "1", StringComparison.Ordinal))
            {
                var tail = text.Length > 200 ? text[^200..] : text;
                summary += " Tail: " + tail;
            }
            throw new ElevationException(summary);
        }
        var output = m.Groups[1].Value.Trim('\n');
        var code = int.TryParse(m.Groups[2].Value, out var c) ? c : 0;
        return (output, code);
    }

    // MARK: az subprocess

    private static async Task<(string Out, string Err, int Code)> RunAzAsync(params string[] args)
    {
        var azPath = FindAzureCli() ?? throw new ElevationException("Azure CLI (az) not found. Please install Azure CLI.");
        var psi = new ProcessStartInfo
        {
            FileName = azPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (await outTask, await errTask, process.ExitCode);
    }

    private static string? FindAzureCli()
    {
        // Windows: az.cmd under the CLI2 install or on PATH.
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            foreach (var name in new[] { "az.cmd", "az" })
            {
                var full = Path.Combine(dir, name);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }
}
