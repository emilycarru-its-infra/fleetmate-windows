using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Tickets;
using Serilog;

namespace FleetMate.Core.Services.Tickets;

/// <summary>
/// The last TeamDynamix sign-in, kept so the next launch, of the window or
/// the background inbox helper, can reuse it instead of running the silent
/// SSO chain, which can take most of a minute.
/// </summary>
public sealed class TdxSession
{
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("expiry")] public DateTime ExpiryUtc { get; set; }
    [JsonPropertyName("userEmail")] public string? UserEmail { get; set; }
    [JsonPropertyName("userName")] public string? UserName { get; set; }
    [JsonPropertyName("me")] public TdxPerson? Me { get; set; }

    /// <summary>The address the live sign-in's identity check verified, lower-cased.</summary>
    [JsonPropertyName("verifiedAddress")] public string? VerifiedAddress { get; set; }

    /// <summary>The TeamDynamix base URL the session was issued by.</summary>
    [JsonPropertyName("baseUrl")] public string? BaseUrl { get; set; }

    /// <summary>Worth reusing: at least an hour left.</summary>
    public bool IsFresh(DateTime utcNow) =>
        !string.IsNullOrEmpty(Token) && ExpiryUtc.ToUniversalTime() > utcNow.AddHours(1);

    /// <summary>
    /// Whether this session belongs to the account and tenant now expected. A
    /// saved session must not skip the identity check a live sign-in enforces,
    /// so an unknown expected address never matches.
    /// </summary>
    public bool BelongsTo(string? expectedAddress, string? baseUrl) =>
        NormalizeAddress(expectedAddress) is { } expected
        && NormalizeAddress(VerifiedAddress) == expected
        && NormalizeBaseUrl(BaseUrl) is { } saved
        && saved == NormalizeBaseUrl(baseUrl);

    /// <summary>The cached person, only when their email is the verified address.</summary>
    public TdxPerson? VerifiedMe =>
        Me is { } me && NormalizeAddress(me.PrimaryEmail) is { } email && email == NormalizeAddress(VerifiedAddress) ? me : null;

    public static string? NormalizeAddress(string? value)
    {
        var trimmed = value?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(trimmed) || !trimmed.Contains('@') ? null : trimmed;
    }

    public static string? NormalizeBaseUrl(string? value)
    {
        var trimmed = value?.Trim().TrimEnd('/').ToLowerInvariant();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

/// <summary>
/// Stores <see cref="TdxSession"/> in one file encrypted with DPAPI for the
/// current Windows user, under the app's local data folder. Only the same
/// user on the same machine can read it back.
/// </summary>
public sealed class TdxSessionCache
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FleetMate.TdxSession.v1");
    private readonly string _path;

    public TdxSessionCache(string? directory = null)
    {
        _path = Path.Combine(directory ?? AppEdition.Current.LocalAppDataDirectory, "tdx-session.bin");
    }

    public TdxSession? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var json = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<TdxSession>(json);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[tdx-session] Saved session could not be read; ignoring it");
            return null;
        }
    }

    public void Save(TdxSession session)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(session), Entropy, DataProtectionScope.CurrentUser);
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tdx-session] Session could not be saved");
        }
    }

    /// <summary>Save <paramref name="me"/> into the stored session, when it is the same sign-in.</summary>
    public void SaveMe(string token, TdxPerson me)
    {
        if (Load() is { } session && session.Token == token
            && TdxSession.NormalizeAddress(me.PrimaryEmail) is { } email && email == session.VerifiedAddress)
        {
            session.Me = me;
            Save(session);
        }
    }

    public void Clear()
    {
        try { File.Delete(_path); }
        catch (Exception ex) { Log.Debug(ex, "[tdx-session] Saved session could not be removed"); }
    }
}
