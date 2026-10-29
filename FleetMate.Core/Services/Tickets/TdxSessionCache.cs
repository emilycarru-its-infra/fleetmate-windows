using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Services.Terminal;

namespace FleetMate.Core.Services.Tickets;

/// <summary>Shares an identity-checked user session between the GUI and CLI, protected by Windows DPAPI.</summary>
public static class TdxSessionCache
{
    private sealed record Session(string Service, string Token);
    private static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FleetMate", "Authentication");

    private static string Service(string baseUrl) => new Uri(TdxSsoService.BuildLoginSsoUrl(baseUrl)).AbsoluteUri;
    private static string FilePath(string service, string? directory = null) => Path.Combine(directory ?? DirectoryPath,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(service))) + ".dat");

    public static bool Store(string baseUrl, TdxSsoResult result, string? directory = null)
    {
        if (!result.Success || string.IsNullOrEmpty(result.Token) || result.Expiry <= DateTime.UtcNow) return false;
        try
        {
            var service = Service(baseUrl);
            var data = JsonSerializer.SerializeToUtf8Bytes(new Session(service, result.Token));
            var encrypted = ProtectedData.Protect(data, Encoding.UTF8.GetBytes(service), DataProtectionScope.CurrentUser);
            PrivateFile.EnsureDirectory(directory ?? DirectoryPath);
            var path = FilePath(service, directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, encrypted);
                PrivateFile.Restrict(temporary);
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or UriFormatException)
        { return false; }
    }

    public static TdxSsoResult? Read(string baseUrl, string? expectedUpn, string? directory = null)
    {
        try
        {
            var service = Service(baseUrl);
            var path = FilePath(service, directory);
            if (!File.Exists(path)) return null;
            var decrypted = ProtectedData.Unprotect(File.ReadAllBytes(path), Encoding.UTF8.GetBytes(service), DataProtectionScope.CurrentUser);
            var session = JsonSerializer.Deserialize<Session>(decrypted);
            var result = session?.Service == service ? TdxSsoIdentity.Verify(session.Token, expectedUpn) : null;
            if (result is { Success: true } && result.Expiry > DateTime.UtcNow) return result;
            Clear(baseUrl, directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or UriFormatException)
        { Clear(baseUrl, directory); }
        return null;
    }

    public static void Clear(string baseUrl, string? directory = null)
    {
        try { File.Delete(FilePath(Service(baseUrl), directory)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException) { }
    }
}
