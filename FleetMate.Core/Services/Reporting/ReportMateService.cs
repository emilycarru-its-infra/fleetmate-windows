using System.Net.Http.Json;
using System.Text.Json;
using FleetMate.Core.Converters;
using FleetMate.Core.Models.Reporting;
using FleetMate.Core.Services;
using Serilog;

namespace FleetMate.Core.Services.Reporting;

/// <summary>
/// Client for ReportMate API - fleet monitoring and device inventory.
///
/// Every read goes through the <c>reportmateutil</c> CLI when it is installed (see
/// <see cref="ReportMateCli"/>) and through this class's own HTTP client
/// otherwise. Both paths return the API's JSON unchanged, so the decoding is
/// shared.
/// </summary>
public class ReportMateService : IDisposable
{
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ReportMateCli? _cli;
    private readonly string _baseUrl;
    private readonly string? _passphrase;
    private readonly string? _oidcAudience;

    // Caches
    private List<Device>? _deviceCache;
    private DateTime _deviceCacheExpiry = DateTime.MinValue;
    private List<InstallRecord>? _installCache;
    private DateTime _installCacheExpiry = DateTime.MinValue;
    private Dictionary<string, DeviceNetworkInfo>? _addressCache;
    private DateTime _addressCacheExpiry = DateTime.MinValue;
    private readonly TimeSpan _cacheDuration;

    /// <summary>
    /// True when ReportMate authenticates with an Entra bearer minted off the
    /// operator's Windows sign-in rather than the shared passphrase.
    /// </summary>
    public bool UsesOidc { get; }

    /// <summary>True when reads are routed through the installed <c>reportmateutil</c> CLI.</summary>
    public bool UsesCli => _cli != null;

    /// <summary>Where the CLI lives, for status output.</summary>
    public string? CliPath => _cli?.Path;

    /// <summary>
    /// Build from config so every call site authenticates the same way — Entra
    /// SSO by default, the legacy passphrase only where no audience is configured.
    /// </summary>
    public static ReportMateService FromConfig(FleetMate.Core.Config.FleetMateConfig config)
    {
#pragma warning disable CS0618 // legacy fallback for unmigrated configs
        var passphrase = config.ReportMatePassphrase;
#pragma warning restore CS0618
        return new ReportMateService(
            config.ReportMateUrl ?? string.Empty, passphrase, config.CacheMinutes, config.ReportMateOidcAudience);
    }

    /// <param name="cli">The CLI to route reads through; when omitted, whatever is installed. Pass an explicit null via <see cref="ReportMateService(string, string?, int, string?, ReportMateCli?, HttpMessageHandler?)"/> to force HTTP.</param>
    public ReportMateService(string baseUrl, string? passphrase = null, int cacheMinutes = 5, string? oidcAudience = null)
        : this(baseUrl, passphrase, cacheMinutes, oidcAudience, ReportMateCli.Locate(), null)
    {
    }

    /// <param name="cli">The CLI to route reads through, or null to force HTTP.</param>
    /// <param name="httpHandler">Replaces the HTTP transport, so a test can stub the API.</param>
    public ReportMateService(string baseUrl, string? passphrase, int cacheMinutes, string? oidcAudience,
        ReportMateCli? cli, HttpMessageHandler? httpHandler)
    {
        UsesOidc = !string.IsNullOrWhiteSpace(oidcAudience);
        _baseUrl = ServiceUri.Normalize(baseUrl).TrimEnd('/');
        _passphrase = passphrase;
        _oidcAudience = UsesOidc ? oidcAudience : null;
        _cli = cli;

        // Prefer-bearer: an Entra audience beats the shared passphrase wherever
        // both are set, so migration is additive rather than a flag day.
        var inner = httpHandler ?? new HttpClientHandler();
        _client = UsesOidc
            ? new HttpClient(new EntraBearerHandler(oidcAudience!) { InnerHandler = inner })
            : new HttpClient(inner);
        _client.BaseAddress = new Uri(_baseUrl + "/");
        _client.Timeout = TimeSpan.FromSeconds(120);

        if (!UsesOidc && !string.IsNullOrEmpty(passphrase))
        {
            _client.DefaultRequestHeaders.Add("X-Client-Passphrase", passphrase);
        }

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new NullableDateTimeConverter() }
        };

        _cacheDuration = TimeSpan.FromMinutes(cacheMinutes);
    }

    /// <summary>
    /// The environment the CLI needs: the API URL and the same credential the
    /// HTTP path would send, expressed the way the CLI reads it.
    /// </summary>
    private async Task<Dictionary<string, string>> CliCredentialsAsync()
    {
        var env = new Dictionary<string, string> { ["REPORTMATE_API_URL"] = _baseUrl };
        if (_oidcAudience != null)
        {
            var source = EntraTokenSource.Shared
                ?? throw new EntraTokenException(_oidcAudience, "Entra token source is not configured; call EntraTokenSource.Configure first");
            env["REPORTMATE_TOKEN"] = await source.GetTokenAsync(_oidcAudience);
        }
        else if (!string.IsNullOrEmpty(_passphrase))
        {
            env["REPORTMATE_PASSPHRASE"] = _passphrase;
        }
        return env;
    }

    /// <summary>
    /// One read, through the CLI when installed and over HTTP otherwise.
    /// </summary>
    /// <param name="cliArguments">The <c>reportmate</c> arguments that produce the same JSON as <paramref name="httpPath"/>.</param>
    /// <returns>The decoded body, or null when the API answered 404 on either path.</returns>
    /// <exception cref="ReportMateCli.CliException">The CLI ran and the API refused the request.</exception>
    /// <exception cref="HttpRequestException">The HTTP path failed with a non-success status.</exception>
    private async Task<T?> FetchAsync<T>(string[] cliArguments, string httpPath) where T : class
    {
        if (_cli != null)
        {
            var output = await _cli.RunAsync(cliArguments, await CliCredentialsAsync());
            if (output.Succeeded)
            {
                return JsonSerializer.Deserialize<T>(output.Stdout, _jsonOptions);
            }
            if (output.IsNotFound) return null;
            if (output.Launched)
            {
                throw new ReportMateCli.CliException(output.ExitCode, output.Stderr);
            }
            Log.Warning("reportmateutil at {Path} did not launch ({Reason}); using HTTP", _cli.Path, output.Stderr);
        }

        var response = await _client.GetAsync(httpPath);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"GET {httpPath} -> {(int)response.StatusCode} {response.ReasonPhrase}: {error}", null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions);
    }

    /// <summary>
    /// Get all devices from the fleet
    /// </summary>
    public async Task<List<Device>> GetDevicesAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _deviceCache != null && DateTime.UtcNow < _deviceCacheExpiry)
        {
            return _deviceCache;
        }

        Log.Debug("Fetching devices from ReportMate...");

        try
        {
            var allDevices = new List<Device>();
            var offset = 0;
            const int limit = 100;

            while (true)
            {
                var wrapper = await FetchAsync<DevicesResponse>(
                    new[] { "devices", "--limit", limit.ToString(), "--offset", offset.ToString() },
                    $"api/v1/devices?offset={offset}&limit={limit}");

                if (wrapper?.Devices == null || wrapper.Devices.Count == 0)
                    break;

                allDevices.AddRange(wrapper.Devices);

                if (wrapper.Devices.Count < limit)
                    break;

                offset += limit;
            }

            _deviceCache = allDevices;
            _deviceCacheExpiry = DateTime.UtcNow.Add(_cacheDuration);

            Log.Information("Cached {Count} devices from ReportMate", _deviceCache.Count);
            return _deviceCache;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch devices from ReportMate");
            return _deviceCache ?? new List<Device>();
        }
    }

    /// <summary>
    /// Find a device by serial, hostname, asset tag, or other identifier
    /// </summary>
    public async Task<Device?> FindDeviceAsync(string query)
    {
        var devices = await GetDevicesAsync();
        var normalized = query.Trim().ToUpperInvariant();

        return devices.FirstOrDefault(d =>
            d.SerialNumber?.ToUpperInvariant() == normalized ||
            d.DeviceName?.ToUpperInvariant() == normalized ||
            d.Name?.ToUpperInvariant() == normalized ||
            d.Hostname?.ToUpperInvariant() == normalized ||
            d.AssetTag?.ToUpperInvariant() == normalized ||
            d.Owner?.ToUpperInvariant() == normalized ||
            d.IpAddress == query ||
            NormalizeMac(d.MacAddress) == NormalizeMac(query));
    }

    /// <summary>
    /// Get all install records
    /// </summary>
    public async Task<List<InstallRecord>> GetInstallsAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _installCache != null && DateTime.UtcNow < _installCacheExpiry)
        {
            return _installCache;
        }

        Log.Debug("Fetching install records from ReportMate...");

        try
        {
            var installs = await FetchAsync<List<InstallRecord>>(new[] { "module", "installs" }, "api/v1/installs");
            _installCache = installs ?? new List<InstallRecord>();
            _installCacheExpiry = DateTime.UtcNow.Add(_cacheDuration);

            Log.Information("Cached {Count} install records from ReportMate", _installCache.Count);
            return _installCache;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch install records from ReportMate");
            return _installCache ?? new List<InstallRecord>();
        }
    }

    /// <summary>
    /// Get only install records with errors
    /// </summary>
    public async Task<List<InstallRecord>> GetErrorsAsync(bool forceRefresh = false)
    {
        var installs = await GetInstallsAsync(forceRefresh);
        return installs.Where(i => i.IsError).ToList();
    }

    /// <summary>
    /// Get errors grouped by item name
    /// </summary>
    public async Task<List<ErrorSummary>> GetErrorsByItemAsync(bool forceRefresh = false)
    {
        var errors = await GetErrorsAsync(forceRefresh);

        return errors
            .GroupBy(e => e.ItemName)
            .Select(g => new ErrorSummary
            {
                ItemName = g.Key,
                DeviceCount = g.Count(),
                Category = g.First().Category,
                SampleError = g.First().LastError ?? string.Empty,
                AffectedDevices = g.Select(e => $"{e.DeviceName} ({e.SerialNumber})").ToList()
            })
            .OrderByDescending(s => s.DeviceCount)
            .ToList();
    }

    /// <summary>
    /// Get errors grouped by device
    /// </summary>
    public async Task<List<DeviceErrorSummary>> GetErrorsByDeviceAsync(bool forceRefresh = false)
    {
        var errors = await GetErrorsAsync(forceRefresh);

        return errors
            .GroupBy(e => e.SerialNumber)
            .Select(g => new DeviceErrorSummary
            {
                DeviceName = g.First().DeviceName,
                SerialNumber = g.Key,
                Location = g.First().Location ?? string.Empty,
                ErrorCount = g.Count(),
                FailedItems = g.Select(e => e.ItemName).ToList(),
                LastSeen = g.Max(e => e.LastSeen)
            })
            .OrderByDescending(s => s.ErrorCount)
            .ToList();
    }

    /// <summary>
    /// Get install records for a specific device
    /// </summary>
    public async Task<List<InstallRecord>> GetDeviceInstallsAsync(string serialOrName)
    {
        var installs = await GetInstallsAsync();
        var normalized = serialOrName.Trim().ToUpperInvariant();

        return installs.Where(i =>
            i.SerialNumber?.ToUpperInvariant() == normalized ||
            i.DeviceName?.ToUpperInvariant() == normalized).ToList();
    }

    /// <summary>
    /// Get install records for a specific item across all devices
    /// </summary>
    public async Task<List<InstallRecord>> GetItemInstallsAsync(string itemName)
    {
        var installs = await GetInstallsAsync();
        return installs.Where(i =>
            i.ItemName.Equals(itemName, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Get device installation log (Cimian log entries)
    /// </summary>
    public async Task<DeviceLog?> GetDeviceLogAsync(string serialNumber)
    {
        Log.Debug("Fetching install log for device {Serial}", serialNumber);

        try
        {
            return await FetchAsync<DeviceLog>(
                new[] { "device", serialNumber, "installs-log" },
                $"api/v1/device/{serialNumber}/installs/log");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch device log for {Serial}", serialNumber);
            return null;
        }
    }

    /// <summary>
    /// Get device network information including IP addresses
    /// </summary>
    public async Task<NetworkInfo?> GetDeviceNetworkAsync(string serialNumber)
    {
        Log.Debug("Fetching network info for device {Serial}", serialNumber);

        try
        {
            var wrapper = await FetchAsync<ModuleDataWrapper>(
                new[] { "device", serialNumber, "module", "network" },
                $"api/v1/device/{serialNumber}/modules/network");
            return wrapper?.Data?.ToObject<NetworkInfo>(_jsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch network info for {Serial}", serialNumber);
            return null;
        }
    }

    /// <summary>
    /// Get fleet-wide network data (all devices with network info).
    ///
    /// One request for the whole fleet (<c>/api/v1/network</c>), so a scan or a
    /// group action resolves every address without a per-device round trip.
    /// </summary>
    public async Task<List<DeviceNetworkInfo>> GetFleetNetworkAsync()
    {
        Log.Debug("Fetching fleet network data...");

        try
        {
            var devices = await FetchAsync<List<DeviceNetworkInfo>>(new[] { "module", "network" }, "api/v1/network");
            return devices ?? new List<DeviceNetworkInfo>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch fleet network data");
            return new List<DeviceNetworkInfo>();
        }
    }

    /// <summary>
    /// Fleet addresses keyed by upper-cased serial number, cached like the
    /// device list. Devices with no reported IPv4 address are absent.
    /// </summary>
    public async Task<Dictionary<string, DeviceNetworkInfo>> GetFleetAddressesAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _addressCache != null && DateTime.UtcNow < _addressCacheExpiry)
        {
            return _addressCache;
        }

        var map = new Dictionary<string, DeviceNetworkInfo>();
        foreach (var row in await GetFleetNetworkAsync())
        {
            if (string.IsNullOrWhiteSpace(row.SerialNumber)) continue;
            if (string.IsNullOrWhiteSpace(row.PrimaryIp)) continue;
            map[row.SerialNumber.ToUpperInvariant()] = row;
        }
        _addressCache = map;
        _addressCacheExpiry = DateTime.UtcNow.Add(_cacheDuration);
        Log.Information("Cached {Count} fleet addresses from ReportMate", map.Count);
        return map;
    }

    /// <summary>
    /// Get full device details with all modules
    /// </summary>
    public async Task<FullDevice?> GetFullDeviceAsync(string serialNumber)
    {
        Log.Debug("Fetching full device data for {Serial}", serialNumber);

        try
        {
            return await FetchAsync<FullDevice>(new[] { "device", serialNumber }, $"api/v1/device/{serialNumber}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch full device for {Serial}", serialNumber);
            return null;
        }
    }

    private static string NormalizeMac(string? mac)
    {
        if (string.IsNullOrEmpty(mac)) return string.Empty;
        return mac.Replace(":", "").Replace("-", "").Replace(".", "").ToUpperInvariant();
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
