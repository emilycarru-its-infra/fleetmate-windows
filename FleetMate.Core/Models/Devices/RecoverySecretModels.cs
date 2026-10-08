using System.Text;
using System.Text.Json.Serialization;

namespace FleetMate.Core.Models.Devices;

/// <summary>
/// A secret FleetMate can reveal for one device. Each is fetched only when
/// asked for, shown once, and never cached, exported or logged.
/// </summary>
public enum RecoverySecretKind
{
    FileVault,
    MacOSLaps,
    BitLocker,
    WindowsLaps,
}

public static class RecoverySecretKinds
{
    public static string DisplayName(this RecoverySecretKind kind) => kind switch
    {
        RecoverySecretKind.FileVault => "FileVault Recovery Key",
        RecoverySecretKind.BitLocker => "BitLocker Recovery Keys",
        _ => "Local Admin Password",
    };

    /// <summary>The secrets that exist for a device's platform, in display order.</summary>
    public static IReadOnlyList<RecoverySecretKind> Available(string? operatingSystem)
    {
        var os = (operatingSystem ?? "").ToLowerInvariant();
        if (os.Contains("mac")) return new[] { RecoverySecretKind.FileVault, RecoverySecretKind.MacOSLaps };
        if (os.Contains("windows")) return new[] { RecoverySecretKind.BitLocker, RecoverySecretKind.WindowsLaps };
        return Array.Empty<RecoverySecretKind>();
    }
}

/// <summary>
/// One revealed value, labelled for the sheet. <see cref="Detail"/> carries
/// context such as the account or volume, never another secret.
/// <see cref="ToString"/> is redacted, so a secret that strays into a log
/// line or an exception message prints as a placeholder.
/// </summary>
public sealed class RevealedSecret
{
    public string Id { get; }
    public string Label { get; }
    public string? Detail { get; }
    private string? _value;

    public RevealedSecret(string id, string label, string value, string? detail = null)
    {
        Id = id;
        Label = label;
        _value = value;
        Detail = detail;
    }

    /// <summary>The secret itself. Read it only to put it on screen or the clipboard.</summary>
    public string Value => _value ?? "";

    /// <summary>Drop the reference so the string can be collected once the sheet closes.</summary>
    public void Forget() => _value = null;

    public override string ToString() => $"{Label} [redacted]";
}

/// <summary>Why a secret couldn't be revealed. The message never contains a secret.</summary>
public sealed class RecoverySecretException : Exception
{
    public RecoverySecretException(string message) : base(message) { }

    public static RecoverySecretException MissingEntraDeviceId() =>
        new("This device has no Entra device ID, so its keys can't be looked up.");

    public static RecoverySecretException NotEscrowed(string what) =>
        new($"No {what} has been escrowed for this device.");

    public static RecoverySecretException Unsupported(string? os) =>
        new($"No recovery secrets are available for {(string.IsNullOrWhiteSpace(os) ? "this platform" : os)}.");
}

// ── FileVault ────────────────────────────────────────────────────────────

/// <summary><c>GET /beta/deviceManagement/managedDevices/{id}/getFileVaultKey</c></summary>
public sealed class FileVaultKeyResponse
{
    [JsonPropertyName("value")] public string? Value { get; set; }
}

// ── macOS LAPS ───────────────────────────────────────────────────────────

/// <summary><c>GET /beta/deviceManagement/managedDevices/{id}/retrieveMacOSManagedDeviceLocalAdminAccountDetail</c></summary>
public sealed class MacOSLocalAdminCredentialResponse
{
    public sealed class Detail
    {
        [JsonPropertyName("adminAccountPassword")] public string? AdminAccountPassword { get; set; }
        [JsonPropertyName("passwordLastRotatedDateTime")] public string? PasswordLastRotatedDateTime { get; set; }
    }

    [JsonPropertyName("value")] public Detail? Value { get; set; }
}

// ── BitLocker ────────────────────────────────────────────────────────────

/// <summary><c>GET /informationProtection/bitlocker/recoveryKeys?$filter=deviceId eq '…'</c></summary>
public sealed class BitLockerRecoveryKeyListResponse
{
    [JsonPropertyName("value")] public List<BitLockerRecoveryKey> Value { get; set; } = new();
}

/// <summary>A BitLocker key record. <see cref="Key"/> is present only on a single-key read with <c>$select=key</c>.</summary>
public sealed class BitLockerRecoveryKey
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("createdDateTime")] public string? CreatedDateTime { get; set; }
    [JsonPropertyName("volumeType")] public string? VolumeType { get; set; }
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
    [JsonPropertyName("key")] public string? Key { get; set; }

    /// <summary><c>operatingSystemVolume</c> → "Operating System Volume".</summary>
    public string VolumeDisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(VolumeType)) return "Volume";
            var sb = new StringBuilder();
            for (var i = 0; i < VolumeType.Length; i++)
            {
                var c = VolumeType[i];
                if (i > 0 && char.IsUpper(c) && char.IsLower(VolumeType[i - 1])) sb.Append(' ');
                sb.Append(i == 0 ? char.ToUpperInvariant(c) : c);
            }
            return sb.ToString();
        }
    }
}

// ── Windows LAPS ─────────────────────────────────────────────────────────

/// <summary><c>GET /directory/deviceLocalCredentials/{entraDeviceId}?$select=credentials</c></summary>
public sealed class DeviceLocalCredentialInfo
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("deviceName")] public string? DeviceName { get; set; }
    [JsonPropertyName("lastBackupDateTime")] public string? LastBackupDateTime { get; set; }
    [JsonPropertyName("credentials")] public List<DeviceLocalCredential>? Credentials { get; set; }

    /// <summary>The credential backed up most recently; earlier ones were already rotated away.</summary>
    public DeviceLocalCredential? LatestCredential =>
        Credentials?.OrderByDescending(c => c.BackupDateTime ?? "", StringComparer.Ordinal).FirstOrDefault();
}

public sealed class DeviceLocalCredential
{
    [JsonPropertyName("accountName")] public string? AccountName { get; set; }
    [JsonPropertyName("accountSid")] public string? AccountSid { get; set; }
    [JsonPropertyName("backupDateTime")] public string? BackupDateTime { get; set; }
    [JsonPropertyName("passwordBase64")] public string? PasswordBase64 { get; set; }

    /// <summary>Graph returns the password as base64-encoded UTF-8.</summary>
    public string? Password
    {
        get
        {
            if (string.IsNullOrEmpty(PasswordBase64)) return null;
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(PasswordBase64)); }
            catch (FormatException) { return null; }
        }
    }

    public override string ToString() => $"{AccountName} [redacted]";
}
