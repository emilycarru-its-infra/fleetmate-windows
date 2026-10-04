using System.Net.Http.Json;
using System.Reflection;
using FleetMate.Core.Models.Devices;
using Serilog;

namespace FleetMate.Core.Services;

/// <summary>
/// Recovery secrets for one device at a time: BitLocker keys and Windows LAPS
/// passwords from the directory, FileVault keys and macOS LAPS passwords from
/// Intune. Nothing here caches or logs a value. The caller gets it back once
/// and decides how long to hold it. Only the fact of a reveal is logged.
/// </summary>
public partial class GraphService
{
    /// <summary>
    /// Graph asks callers that read BitLocker keys and LAPS passwords to name
    /// themselves, for the directory audit log.
    /// </summary>
    public static IReadOnlyDictionary<string, string> AuditClientHeaders { get; } = new Dictionary<string, string>
    {
        ["ocp-client-name"] = "FleetMate",
        ["ocp-client-version"] = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0",
    };

    public async Task<List<RevealedSecret>> RevealRecoverySecretAsync(RecoverySecretKind kind, IntuneDevice device,
        CancellationToken ct = default)
    {
        if (!RecoverySecretKinds.Available(device.OperatingSystem).Contains(kind))
            throw RecoverySecretException.Unsupported(device.OperatingSystem);

        var secrets = kind switch
        {
            RecoverySecretKind.FileVault => new List<RevealedSecret> { await FileVaultKeyAsync(device, ct) },
            RecoverySecretKind.MacOSLaps => new List<RevealedSecret> { await MacOSLocalAdminAsync(device, ct) },
            RecoverySecretKind.BitLocker => await BitLockerKeysAsync(device, ct),
            _ => new List<RevealedSecret> { await WindowsLocalAdminAsync(device, ct) },
        };
        Log.Information("Revealed {Kind} for device {DeviceId} ({Count} value(s))", kind, device.Id, secrets.Count);
        return secrets;
    }

    private async Task<RevealedSecret> FileVaultKeyAsync(IntuneDevice device, CancellationToken ct)
    {
        var response = await GetSecretJsonAsync<FileVaultKeyResponse>(
            $"{GraphBeta}deviceManagement/managedDevices/{device.Id}/getFileVaultKey", false, ct);
        if (string.IsNullOrEmpty(response?.Value)) throw RecoverySecretException.NotEscrowed("FileVault recovery key");
        return new RevealedSecret("filevault", "Personal Recovery Key", response.Value);
    }

    private async Task<RevealedSecret> MacOSLocalAdminAsync(IntuneDevice device, CancellationToken ct)
    {
        var response = await GetSecretJsonAsync<MacOSLocalAdminCredentialResponse>(
            $"{GraphBeta}deviceManagement/managedDevices/{device.Id}/retrieveMacOSManagedDeviceLocalAdminAccountDetail",
            false, ct);
        var password = response?.Value?.AdminAccountPassword;
        if (string.IsNullOrEmpty(password)) throw RecoverySecretException.NotEscrowed("local administrator password");
        var rotated = response!.Value!.PasswordLastRotatedDateTime;
        return new RevealedSecret("macos-laps", "Password", password,
            rotated != null ? $"Last rotated {rotated}" : null);
    }

    private async Task<List<RevealedSecret>> BitLockerKeysAsync(IntuneDevice device, CancellationToken ct)
    {
        var entraId = EntraDeviceId(device);
        var filter = Uri.EscapeDataString($"deviceId eq '{entraId}'");
        var list = await GetSecretJsonAsync<BitLockerRecoveryKeyListResponse>(
            $"informationProtection/bitlocker/recoveryKeys?$filter={filter}", true, ct);
        if (list == null || list.Value.Count == 0) throw RecoverySecretException.NotEscrowed("BitLocker recovery key");

        // The key itself is served only one record at a time.
        var secrets = new List<RevealedSecret>();
        foreach (var record in list.Value.OrderByDescending(r => r.CreatedDateTime ?? "", StringComparer.Ordinal))
        {
            var full = await GetSecretJsonAsync<BitLockerRecoveryKey>(
                $"informationProtection/bitlocker/recoveryKeys/{record.Id}?$select=key", true, ct);
            if (string.IsNullOrEmpty(full?.Key)) continue;
            secrets.Add(new RevealedSecret(record.Id, record.VolumeDisplayName, full.Key,
                $"Key ID {record.Id}" + (record.CreatedDateTime != null ? $" · backed up {record.CreatedDateTime}" : "")));
        }
        if (secrets.Count == 0) throw RecoverySecretException.NotEscrowed("BitLocker recovery key");
        return secrets;
    }

    private async Task<RevealedSecret> WindowsLocalAdminAsync(IntuneDevice device, CancellationToken ct)
    {
        var entraId = EntraDeviceId(device);
        var info = await GetSecretJsonAsync<DeviceLocalCredentialInfo>(
            $"directory/deviceLocalCredentials/{entraId}?$select=credentials", true, ct);
        var credential = info?.LatestCredential;
        var password = credential?.Password;
        if (string.IsNullOrEmpty(password)) throw RecoverySecretException.NotEscrowed("local administrator password");

        var detail = credential!.AccountName != null ? $"Account {credential.AccountName}" : "";
        if (credential.BackupDateTime != null)
            detail += (detail.Length == 0 ? "" : " · ") + $"backed up {credential.BackupDateTime}";
        return new RevealedSecret("windows-laps", "Password", password, detail.Length == 0 ? null : detail);
    }

    private static string EntraDeviceId(IntuneDevice device)
    {
        var id = device.AzureAdDeviceId;
        if (string.IsNullOrWhiteSpace(id) || id == "00000000-0000-0000-0000-000000000000")
            throw RecoverySecretException.MissingEntraDeviceId();
        return id;
    }

    /// <summary>
    /// GET a secret-bearing response. Failures report the status and Graph's
    /// error code only; a success body is never logged.
    /// </summary>
    private async Task<T?> GetSecretJsonAsync<T>(string url, bool audit, CancellationToken ct)
    {
        if (!await SetAuthorizationAsync())
            throw new RecoverySecretException("Not signed in to Microsoft Graph.");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (audit)
            foreach (var (name, value) in AuditClientHeaders)
                request.Headers.TryAddWithoutValidation(name, value);

        using var response = await _client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var reason = response.StatusCode switch
            {
                System.Net.HttpStatusCode.NotFound => "Graph has no record of it",
                System.Net.HttpStatusCode.Forbidden => "the device management identity isn't allowed to read it",
                _ => $"Graph answered {(int)response.StatusCode}",
            };
            Log.Warning("Recovery secret request for {Url} failed: {Status}", RedactQuery(url), response.StatusCode);
            throw new RecoverySecretException($"Couldn't read it: {reason}.");
        }
        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, ct);
    }

    private static string RedactQuery(string url)
    {
        var q = url.IndexOf('?');
        return q < 0 ? url : url[..q];
    }
}
