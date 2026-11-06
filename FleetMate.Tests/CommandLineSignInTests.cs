using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The command line's fallback once no silent token exists: the operator's az session, which
/// signs in at the console only when az asks for it and only at a real console.
/// </summary>
public class CommandLineSignInTests
{
    private const string Tenant = "00000000-0000-0000-0000-000000000000";
    private const string Scope = "api://example-search/.default";
    private const string Token = "{\"accessToken\":\"az-token\",\"expires_on\":4102444800}";

    private static Task<(string, DateTimeOffset)> BrokerRefuses(string scope, CancellationToken _) =>
        throw new EntraTokenException(scope, "no silent credential from the Windows sign-in broker (operating-system account: failed_to_acquire_token_silently_from_broker)");

    private sealed class Az
    {
        public readonly List<(string Command, bool Interactive)> Calls = new();
        public Queue<AzCliTokenSource.AzResult> Answers { get; } = new();

        public Task<AzCliTokenSource.AzResult> Run(IReadOnlyList<string> args, bool interactive, CancellationToken _)
        {
            Calls.Add((string.Join(' ', args), interactive));
            return Task.FromResult(Answers.Dequeue());
        }
    }

    [Fact]
    public async Task AnExistingAzSessionServesWithoutASignIn()
    {
        var az = new Az();
        az.Answers.Enqueue(new(0, Token, ""));
        var source = new AzCliTokenSource(az.Run, () => true);

        var (token, expires) = await source.AcquireAsync(Scope, Tenant);
        Assert.Equal("az-token", token);
        Assert.Equal(2100, expires.Year);
        var (command, interactive) = Assert.Single(az.Calls);
        Assert.Equal($"account get-access-token --scope {Scope} --output json --tenant {Tenant}", command);
        Assert.False(interactive);
    }

    [Fact]
    public async Task AnExpiredSessionSignsInAtTheConsoleThenAsksAgain()
    {
        var az = new Az();
        az.Answers.Enqueue(new(1, "", "ERROR: " + Entra("50078") + ": Presented multi-factor authentication has expired"));
        az.Answers.Enqueue(new(0, "", ""));
        az.Answers.Enqueue(new(0, Token, ""));
        var source = new AzCliTokenSource(az.Run, () => true);

        Assert.Equal("az-token", (await source.AcquireAsync(Scope, Tenant)).Token);
        Assert.Equal(3, az.Calls.Count);
        Assert.Equal($"login --scope {Scope} --output none --tenant {Tenant}", az.Calls[1].Command);
        Assert.True(az.Calls[1].Interactive);
    }

    [Fact]
    public async Task WithoutAConsoleItSaysWhatToRunInstead()
    {
        var az = new Az();
        az.Answers.Enqueue(new(1, "", "Please run 'az login' to setup account."));
        var source = new AzCliTokenSource(az.Run, () => false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.AcquireAsync(Scope, Tenant));
        Assert.Contains($"az login --scope {Scope}", ex.Message);
        Assert.Single(az.Calls);
    }

    [Fact]
    public async Task AnUnrelatedAzFailureNeverPromptsASignIn()
    {
        var az = new Az();
        az.Answers.Enqueue(new(1, "", "ERROR: network unreachable"));
        var source = new AzCliTokenSource(az.Run, () => true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.AcquireAsync(Scope, Tenant));
        Assert.Single(az.Calls);
    }

    [Fact]
    public async Task TheTokenSourceFallsBackToTheCommandLineAfterTheBroker()
    {
        var source = new EntraTokenSource(Tenant)
        {
            AcquireOverride = BrokerRefuses,
            CommandLineOverride = (scope, tenant, _) =>
            {
                Assert.Equal(Scope, scope);
                Assert.Equal(Tenant, tenant);
                return Task.FromResult(("cli-token", DateTimeOffset.UtcNow.AddHours(1)));
            },
        };

        Assert.Equal("cli-token", await source.GetTokenAsync(Scope));
    }

    [Fact]
    public async Task ACommandLineFailureStillNamesTheBrokersReason()
    {
        var source = new EntraTokenSource(Tenant)
        {
            AcquireOverride = BrokerRefuses,
            CommandLineOverride = (_, _, _) => throw new InvalidOperationException("az needs you to sign in again"),
        };

        var ex = await Assert.ThrowsAsync<EntraTokenException>(() => source.GetTokenAsync(Scope));
        Assert.Contains("failed_to_acquire_token_silently_from_broker", ex.Message);
        Assert.Contains("az needs you to sign in again", ex.Message);
    }

    [Theory]
    [InlineData("50076", ": Due to a configuration change you must use multi-factor", true)]
    [InlineData("70043", ": The refresh token has expired due to sign-in frequency", true)]
    [InlineData("", "Please run 'az login' to setup account.", true)]
    [InlineData("", "ERROR: The command failed with an unexpected error", false)]
    public void SignInIsOfferedOnlyForSignInErrors(string code, string text, bool expected) =>
        Assert.Equal(expected, AzCliTokenSource.NeedsSignIn((code.Length > 0 ? Entra(code) : "") + text));

    /// <summary>An Entra error code as az prints it, such as the multi-factor-expired one.</summary>
    private static string Entra(string number) => "AADSTS" + number;
}
