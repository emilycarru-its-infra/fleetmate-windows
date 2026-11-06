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

    /// <summary>
    /// Whether a command-line run may use this session in place of its own
    /// sign-in: it is this account's and tenant's, and has more than five
    /// minutes left. Shorter than the window's hour, since a command is brief.
    /// </summary>
    public bool ReusableBy(string? expectedAddress, string? baseUrl, DateTime utcNow) =>
        !string.IsNullOrEmpty(Token) && ExpiryUtc.ToUniversalTime() > utcNow.AddMinutes(5)
        && BelongsTo(expectedAddress, baseUrl);

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

    /// <summary>
    /// The other edition's saved session: TicketsMate's for FleetMate, and
    /// FleetMate's for TicketsMate. Read, never written or cleared from here;
    /// the same user's DPAPI key opens it.
    /// </summary>
    public static TdxSessionCache OtherEdition() => new(
        (AppEdition.Current.IsTicketsOnly ? AppEdition.FleetMate : AppEdition.TicketsMate).LocalAppDataDirectory);

    /// <summary>When the file was last written, or null when there is none. Cheap enough to poll.</summary>
    public DateTime? LastWriteUtc
    {
        get
        {
            try { return File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : null; }
            catch (Exception) { return null; }
        }
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

    /// <summary>
    /// Save <paramref name="session"/>, replacing the file in one step, so a
    /// reader in another process sees the old session or the new one, never
    /// half of either. A session for the same account and tenant that lasts
    /// longer is never replaced: the window and the inbox helper both save
    /// here, and a late save must not undo a newer renewal.
    /// </summary>
    /// <returns>False when the saved session was kept, or the save failed.</returns>
    public bool Save(TdxSession session)
    {
        string? temp = null;
        try
        {
            if (Load() is { } existing && Outlasts(existing, session))
            {
                Log.Information("[tdx-session] Kept the saved session; it lasts longer than the one offered");
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(session), Entropy, DataProtectionScope.CurrentUser);
            // A name of its own, so two processes saving at once never share a temp file.
            temp = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, _path, overwrite: true);
            temp = null;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tdx-session] Session could not be saved");
            return false;
        }
        finally
        {
            if (temp != null)
            {
                try { File.Delete(temp); } catch (Exception) { }
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="saved"/> should stay in place of
    /// <paramref name="offered"/>: another token for the same account and
    /// tenant, expiring later. A different account or tenant is always
    /// replaced, and the same token can always be saved again (its person added).
    /// </summary>
    public static bool Outlasts(TdxSession saved, TdxSession offered) =>
        !string.IsNullOrEmpty(saved.Token)
        && !string.Equals(saved.Token, offered.Token, StringComparison.Ordinal)
        && TdxSession.NormalizeAddress(saved.VerifiedAddress) is { } address
        && address == TdxSession.NormalizeAddress(offered.VerifiedAddress)
        && TdxSession.NormalizeBaseUrl(saved.BaseUrl) == TdxSession.NormalizeBaseUrl(offered.BaseUrl)
        && saved.ExpiryUtc.ToUniversalTime() > offered.ExpiryUtc.ToUniversalTime();

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
