using System.Net;
using System.Text;
using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

public class GraphThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Answers each send with the next queued response and records what it was sent.</summary>
    private sealed class ScriptedHandler(params Func<HttpResponseMessage>[] script) : HttpMessageHandler
    {
        private int _next;
        public List<string?> Bodies { get; } = new();
        public int Calls => _next;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content == null ? null : await request.Content.ReadAsStringAsync(ct));
            var response = script[Math.Min(_next, script.Length - 1)]();
            _next++;
            response.RequestMessage = request;
            return response;
        }
    }

    private static HttpResponseMessage Status(HttpStatusCode code, Action<HttpResponseMessage>? tweak = null)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent("{}") };
        tweak?.Invoke(r);
        return r;
    }

    private static (HttpClient Client, ScriptedHandler Inner, List<TimeSpan> Waits) Build(params Func<HttpResponseMessage>[] script)
    {
        var inner = new ScriptedHandler(script);
        var waits = new List<TimeSpan>();
        var handler = new GraphThrottlingHandler(inner,
            delay: (wait, ct) => { ct.ThrowIfCancellationRequested(); waits.Add(wait); return Task.CompletedTask; },
            now: () => Now);
        return (new HttpClient(handler) { BaseAddress = new Uri("https://graph.example/v1.0/") }, inner, waits);
    }

    [Fact]
    public void ParseRetryAfter_ReadsSecondsAndHttpDate()
    {
        Assert.Equal(TimeSpan.FromSeconds(7), GraphThrottle.ParseRetryAfter("7", Now));
        Assert.Equal(TimeSpan.FromSeconds(30), GraphThrottle.ParseRetryAfter(Now.AddSeconds(30).ToString("r"), Now));
        Assert.Equal(TimeSpan.Zero, GraphThrottle.ParseRetryAfter(Now.AddSeconds(-30).ToString("r"), Now));
        Assert.Null(GraphThrottle.ParseRetryAfter("soon", Now));
        Assert.Null(GraphThrottle.ParseRetryAfter(null, Now));
    }

    [Fact]
    public void Delay_UsesServerWaitOrBackoff_CappedAtSixtySeconds()
    {
        Assert.Equal(new[] { 2.0, 4, 8 }, new[] { 1, 2, 3 }.Select(n => GraphThrottle.Delay(n, null).TotalSeconds));
        Assert.Equal(TimeSpan.FromSeconds(5), GraphThrottle.Delay(1, TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(60), GraphThrottle.Delay(1, TimeSpan.FromSeconds(600)));
    }

    [Fact]
    public async Task RetriesA429WithDeltaSecondsRetryAfter()
    {
        var (client, inner, waits) = Build(
            () => Status(HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new(TimeSpan.FromSeconds(3))),
            () => Status(HttpStatusCode.OK));

        var response = await client.GetAsync("users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);
        Assert.Equal(new[] { TimeSpan.FromSeconds(3) }, waits);
    }

    [Fact]
    public async Task RetriesA503WithHttpDateRetryAfter()
    {
        var (client, _, waits) = Build(
            () => Status(HttpStatusCode.ServiceUnavailable, r => r.Headers.RetryAfter = new(Now.AddSeconds(12))),
            () => Status(HttpStatusCode.OK));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("users")).StatusCode);
        Assert.Equal(new[] { TimeSpan.FromSeconds(12) }, waits);
    }

    [Fact]
    public async Task BacksOff248_ThenReturnsTheLastResponse()
    {
        var (client, inner, waits) = Build(() => Status(HttpStatusCode.TooManyRequests));

        var response = await client.GetAsync("users");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(4, inner.Calls);
        Assert.Equal(new[] { 2.0, 4, 8 }, waits.Select(w => w.TotalSeconds));
    }

    [Fact]
    public async Task CapsAServerWaitAtSixtySeconds()
    {
        var (client, _, waits) = Build(
            () => Status(HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new(TimeSpan.FromSeconds(900))),
            () => Status(HttpStatusCode.OK));

        await client.GetAsync("users");

        Assert.Equal(new[] { TimeSpan.FromSeconds(60) }, waits);
    }

    [Fact]
    public async Task ABare503IsAnOutage_NotRetried()
    {
        var (client, inner, waits) = Build(() => Status(HttpStatusCode.ServiceUnavailable), () => Status(HttpStatusCode.OK));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("users")).StatusCode);
        Assert.Equal(1, inner.Calls);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task ResendsAPostBodyIntact()
    {
        var (client, inner, _) = Build(() => Status(HttpStatusCode.TooManyRequests), () => Status(HttpStatusCode.OK));

        await client.PostAsync("devices/1/syncDevice", new StringContent("{\"a\":1}", Encoding.UTF8, "application/json"));

        Assert.Equal(new[] { "{\"a\":1}", "{\"a\":1}" }, inner.Bodies);
    }

    [Fact]
    public async Task CancellationInterruptsAWait()
    {
        var inner = new ScriptedHandler(() => Status(HttpStatusCode.TooManyRequests));
        using var cts = new CancellationTokenSource();
        var handler = new GraphThrottlingHandler(inner, delay: (wait, ct) =>
        {
            cts.Cancel();
            return Task.Delay(Timeout.Infinite, ct);
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://graph.example/v1.0/") };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("users", cts.Token));
        Assert.Equal(1, inner.Calls);
    }

    [Theory]
    [InlineData("ERROR: Too Many Requests({\"error\":{\"code\":\"TooManyRequests\"}})", HttpStatusCode.TooManyRequests)]
    [InlineData("ERROR: Service Unavailable({\"error\":{\"message\":\"Throttled\"}})", HttpStatusCode.ServiceUnavailable)]
    [InlineData("ERROR: Service Unavailable({\"error\":{\"message\":\"down\"}})", HttpStatusCode.BadGateway)]
    [InlineData("ERROR: Forbidden({\"error\":{}})", HttpStatusCode.BadGateway)]
    public void ElevationErrorText_MapsToTheThrottleStatus(string output, HttpStatusCode expected)
    {
        var response = ElevationHttpHandler.ToResponse(1, output, new HttpRequestMessage(HttpMethod.Get, "https://graph.example/v1.0/users"));
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public void ElevationErrorText_CarriesRetryAfterWhenPrinted()
    {
        var response = ElevationHttpHandler.ToResponse(1, "ERROR: Too Many Requests({\"Retry-After\": \"17\"})",
            new HttpRequestMessage(HttpMethod.Get, "https://graph.example/v1.0/users"));
        Assert.Equal(TimeSpan.FromSeconds(17), response.Headers.RetryAfter?.Delta);
        Assert.Equal(HttpStatusCode.OK, ElevationHttpHandler.ToResponse(0, "{}", new HttpRequestMessage()).StatusCode);
    }
}
