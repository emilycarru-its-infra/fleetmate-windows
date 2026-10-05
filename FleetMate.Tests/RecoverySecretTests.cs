using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using FleetMate.GUI.Views.Devices;
using Xunit;

namespace FleetMate.Tests;

public class RecoverySecretTests
{
    [Fact]
    public void WindowsLaps_DecodesBase64_AndPicksNewestCredential()
    {
        var info = JsonSerializer.Deserialize<DeviceLocalCredentialInfo>("""
            {
              "credentials": [
                { "accountName": "admin", "backupDateTime": "2026-09-01T10:00:00Z", "passwordBase64": "b2xkLXBhc3M=" },
                { "accountName": "admin", "backupDateTime": "2026-10-01T10:00:00Z", "passwordBase64": "bmV3LXBhc3M=" }
              ]
            }
            """)!;

        Assert.Equal("new-pass", info.LatestCredential!.Password);
        Assert.Equal("2026-10-01T10:00:00Z", info.LatestCredential.BackupDateTime);
    }

    [Fact]
    public void WindowsLaps_BadBase64_GivesNoPassword() =>
        Assert.Null(new DeviceLocalCredential { PasswordBase64 = "not base64!" }.Password);

    [Theory]
    [InlineData("operatingSystemVolume", "Operating System Volume")]
    [InlineData("fixedDataVolume", "Fixed Data Volume")]
    [InlineData(null, "Volume")]
    public void BitLocker_VolumeNamesReadLikeAPerson(string? volumeType, string expected) =>
        Assert.Equal(expected, new BitLockerRecoveryKey { VolumeType = volumeType }.VolumeDisplayName);

    [Theory]
    [InlineData("Windows", new[] { RecoverySecretKind.BitLocker, RecoverySecretKind.WindowsLaps })]
    [InlineData("macOS", new[] { RecoverySecretKind.FileVault, RecoverySecretKind.MacOSLaps })]
    [InlineData("iOS", new RecoverySecretKind[0])]
    [InlineData(null, new RecoverySecretKind[0])]
    public void Section_OffersOnlyThePlatformsSecrets(string? os, RecoverySecretKind[] expected) =>
        Assert.Equal(expected, RecoverySecretKinds.Available(os));

    [Fact]
    public void Secrets_NeverPrintTheirValue()
    {
        var secret = new RevealedSecret("k", "Password", "hunter2-secret", "Account admin");
        var credential = new DeviceLocalCredential { AccountName = "admin", PasswordBase64 = "aHVudGVyMi1zZWNyZXQ=" };

        Assert.DoesNotContain("hunter2-secret", secret.ToString());
        Assert.DoesNotContain("hunter2-secret", $"{secret}");
        Assert.DoesNotContain("hunter2-secret", credential.ToString());

        secret.Forget();
        Assert.Equal("", secret.Value);
    }

    [Fact]
    public void Errors_NeverCarryASecret()
    {
        foreach (var ex in new[]
                 {
                     RecoverySecretException.MissingEntraDeviceId(),
                     RecoverySecretException.NotEscrowed("BitLocker recovery key"),
                     RecoverySecretException.Unsupported("iOS"),
                 })
            Assert.DoesNotMatch("[A-Za-z0-9]{6}-[0-9]{6}", ex.Message);
    }

    [Fact]
    public void Clipboard_PayloadIsKeptOutOfHistoryAndCloud()
    {
        var data = SecretClipboard.CreateDataObject("123456-654321");

        Assert.Equal("123456-654321", data.GetData(DataFormats.UnicodeText));
        Assert.True(data.GetDataPresent(SecretClipboard.ExcludeFromMonitoring));
        Assert.Equal(0, BitConverter.ToInt32(((MemoryStream)data.GetData(SecretClipboard.CanIncludeInHistory)!).ToArray()));
        Assert.Equal(0, BitConverter.ToInt32(((MemoryStream)data.GetData(SecretClipboard.CanUploadToCloud)!).ToArray()));
    }

    [Fact]
    public void Clipboard_RemembersOnlyAHash()
    {
        var hash = SecretClipboard.Hash("123456-654321");
        Assert.DoesNotContain("123456", hash);
        Assert.Equal(64, hash.Length);
    }

    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/informationProtection/bitlocker/recoveryKeys?$filter=x")]
    [InlineData("https://graph.microsoft.com/v1.0/directory/deviceLocalCredentials/abc?$select=credentials")]
    [InlineData("https://graph.microsoft.com/beta/deviceManagement/managedDevices/abc/getFileVaultKey")]
    public void RecoveryCalls_RouteToTheDevicesIdentity(string url) =>
        Assert.Equal(GraphDomain.Devices, ElevationHttpHandler.RouteDomain(url));

    [Fact]
    public void Elevation_ForwardsAuditHeaders_NotAuthorization()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/directory/deviceLocalCredentials/x");
        foreach (var (name, value) in GraphService.AuditClientHeaders) request.Headers.TryAddWithoutValidation(name, value);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token");

        var forwarded = ElevationHttpHandler.ForwardedHeaders(request);

        Assert.Contains("ocp-client-name=FleetMate", forwarded);
        Assert.Contains(forwarded, h => h.StartsWith("ocp-client-version="));
        Assert.DoesNotContain(forwarded, h => h.StartsWith("Authorization", StringComparison.OrdinalIgnoreCase));
    }
}
