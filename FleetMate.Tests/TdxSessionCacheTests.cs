using System.Text;
using System.Text.Json;
using FleetMate.Core.Services.Tickets;
using Xunit;

namespace FleetMate.Tests;

public class TdxSessionCacheTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fleetmate-session-" + Guid.NewGuid().ToString("N"));
    private const string Service = "https://tickets.example.com";
    private static TdxSsoResult Result(string email = "operator@example.com", int seconds = 3600)
    {
        static string B64(object value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expiry = DateTimeOffset.UtcNow.AddSeconds(seconds);
        var token = B64(new { alg = "HS256" }) + "." + B64(new { email, exp = expiry.ToUnixTimeSeconds() }) + ".signature";
        return new() { Success = true, Token = token, UserEmail = email, Expiry = expiry.UtcDateTime };
    }

    [Fact]
    public void CacheIsEncryptedAndBoundToServiceAndIdentity()
    {
        var result = Result();
        Assert.True(TdxSessionCache.Store(Service, result, _folder));
        Assert.DoesNotContain(result.Token!, Encoding.UTF8.GetString(File.ReadAllBytes(Directory.GetFiles(_folder).Single())));
        Assert.Equal(result.Token, TdxSessionCache.Read(Service, "operator@example.com", _folder)?.Token);
        Assert.Null(TdxSessionCache.Read("https://other.example.com", "operator@example.com", _folder));
        Assert.Null(TdxSessionCache.Read(Service, "someone@example.com", _folder));
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public void ExpiredOrRefusedSessionsAreNotStored()
    {
        Assert.False(TdxSessionCache.Store(Service, Result(seconds: -5), _folder));
        Assert.False(TdxSessionCache.Store(Service, TdxSsoResult.Refused("Refused"), _folder));
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public void CorruptCacheIsRemovedAndDoesNotBlockSilentSignIn()
    {
        Assert.True(TdxSessionCache.Store(Service, Result(), _folder));
        File.WriteAllText(Directory.GetFiles(_folder).Single(), "broken");
        Assert.Null(TdxSessionCache.Read(Service, "operator@example.com", _folder));
        Assert.Empty(Directory.GetFiles(_folder));
    }

    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
}
