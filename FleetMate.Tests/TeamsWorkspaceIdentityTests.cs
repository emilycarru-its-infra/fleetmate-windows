using System.Text;
using System.Text.Json;
using FleetMate.Core.Services.Teams;
using Xunit;

namespace FleetMate.Tests;

public class TeamsWorkspaceIdentityTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string ObjectId = "22222222-2222-2222-2222-222222222222";
    internal static string Token(string account, string tenant = Tenant, string objectId = ObjectId)
    {
        static string Part(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Part("{\"alg\":\"none\"}") + "." + Part(JsonSerializer.Serialize(new { preferred_username = account, tid = tenant, oid = objectId })) + ".synthetic";
    }

    [Fact]
    public void ApiIdentityMustMatchTheWorkspaceAndKeepTheSamePrincipal()
    {
        var binding = new TeamsWorkspaceBinding(new TeamsWorkspaceIdentity("first@example.org", Tenant));
        binding.Check(Token("FIRST@example.org"));
        Assert.Throws<TeamsSearchException>(() => binding.Check(Token("second@example.org")));
        Assert.Throws<TeamsSearchException>(() => binding.Check(Token("first@example.org", objectId: "33333333-3333-3333-3333-333333333333")));
        Assert.Throws<TeamsSearchException>(() => binding.Check(Token("first@example.org", tenant: "44444444-4444-4444-4444-444444444444")));
        Assert.Throws<TeamsSearchException>(() => binding.Check("not-a-token"));
    }

    [Fact]
    public void AnOldValidTokenCannotSurviveACurrentAccountSwitch()
    {
        var account = "first@example.org";
        var binding = new TeamsWorkspaceBinding(new TeamsWorkspaceIdentity(account, Tenant)) { CurrentAccount = () => account };
        var token = Token(account);
        binding.Check(token);
        account = "second@example.org";
        Assert.Throws<TeamsSearchException>(() => binding.Check(token));
    }
}
