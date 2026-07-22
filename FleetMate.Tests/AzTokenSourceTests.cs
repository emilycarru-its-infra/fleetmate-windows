using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Verifies AzTokenSource's caching + failure handling using an injected fake
/// command runner (no real az invocation).
/// </summary>
public class AzTokenSourceTests
{
    private static AzTokenSource.CommandRunner Runner(Func<(int, string, string)> body, Action? onCall = null)
        => (_, _) => { onCall?.Invoke(); return Task.FromResult(body()); };

    [Fact]
    public async Task ReturnsToken_AndCachesIt()
    {
        var calls = 0;
        var src = new AzTokenSource(Runner(() => (0, "tok-123\n", ""), () => calls++));

        var a = await src.GetTokenAsync("api://snipe");
        var b = await src.GetTokenAsync("api://snipe");

        Assert.Equal("tok-123", a);
        Assert.Equal("tok-123", b);
        Assert.Equal(1, calls); // second call served from cache
    }

    [Fact]
    public async Task DifferentResources_AreCachedSeparately()
    {
        var calls = 0;
        var src = new AzTokenSource(Runner(() => (0, $"tok\n", ""), () => calls++));

        await src.GetTokenAsync("api://a");
        await src.GetTokenAsync("api://b");

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task EmptyResource_ReturnsNull_WithoutRunning()
    {
        var calls = 0;
        var src = new AzTokenSource(Runner(() => (0, "tok\n", ""), () => calls++));

        Assert.Null(await src.GetTokenAsync(""));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task NonZeroExit_ReturnsNull()
    {
        var src = new AzTokenSource(Runner(() => (1, "", "not logged in")));
        Assert.Null(await src.GetTokenAsync("api://snipe"));
    }

    [Fact]
    public async Task EmptyToken_ReturnsNull()
    {
        var src = new AzTokenSource(Runner(() => (0, "   \n", "")));
        Assert.Null(await src.GetTokenAsync("api://snipe"));
    }

    [Fact]
    public async Task Clear_ForcesRefetch()
    {
        var calls = 0;
        var src = new AzTokenSource(Runner(() => (0, "tok\n", ""), () => calls++));

        await src.GetTokenAsync("api://snipe");
        src.Clear();
        await src.GetTokenAsync("api://snipe");

        Assert.Equal(2, calls);
    }
}
