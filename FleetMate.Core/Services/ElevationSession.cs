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

    public async Task<(string Out, int Code)> ExecAsync(GraphDomain domain, string command)
    {
        await EnsureSessionAsync(domain);
        var name = SessionName(domain);

        var account = await RunAzAsync("account", "show", "--query", "id", "-o", "tsv");
        var sub = account.Out.Trim();
        if (account.Code != 0 || string.IsNullOrEmpty(sub))
            throw new ElevationException($"Not logged in to az (run az login). {account.Err.Trim()}");
        var uri = $"https://management.azure.com/subscriptions/{sub}/resourceGroups/{_config.ResourceGroup!}/providers/Microsoft.ContainerInstance/containerGroups/{name}/containers/{name}/exec?api-version={ExecApiVersion}";
        var body = "{\"command\":\"/bin/bash\",\"terminalSize\":{\"rows\":24,\"cols\":500}}";

        var execResp = await RunAzAsync("rest", "--method", "post", "--uri", uri, "--body", body);
        if (execResp.Code != 0)
            throw new ElevationException($"Exec handshake failed: {(string.IsNullOrEmpty(execResp.Err) ? execResp.Out : execResp.Err)}");

        using var doc = JsonDocument.Parse(execResp.Out);
        var wsUri = doc.RootElement.GetProperty("webSocketUri").GetString();
        var password = doc.RootElement.GetProperty("password").GetString();
        if (wsUri == null || password == null) throw new ElevationException("Exec response missing webSocketUri/password");

        return await RunWebSocketAsync(new Uri(wsUri), password, command);
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
        await SendText($"printf '\\n<<<AZE_BEGIN>>>\\n'; ( {command} ); printf '\\n<<<AZE_END:%d>>>\\n' \"$?\"; exit\n");

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
    /// Extract command output and exit code from a raw aze session stream:
    /// strip ANSI escapes, normalize newlines, then pull the payload between the
    /// AZE_BEGIN/AZE_END markers. Throws if the end marker is absent.
    /// </summary>
    internal static (string Out, int Code) ParseExecOutput(string raw)
    {
        var text = AnsiRe.Replace(raw, "").Replace("\r\n", "\n").Replace("\r", "\n");
        var m = MarkerRe.Match(text);
        if (!m.Success) throw new ElevationException("Could not find output markers in session output:\n" + text);
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
