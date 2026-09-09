using FleetMate.Core.Models;
using FleetMate.Core.Models.Manage;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Manage;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Opt-in checks against a real fleet host. Skipped unless
/// <c>FLEETMATE_LIVE_SSH_HOST</c> names a reachable address; the key comes from
/// <c>FLEETMATE_LIVE_SSH_KEY</c> or the default winadmins key path. Nothing
/// from the host is recorded: assertions are on shape, never on values.
/// </summary>
public class SecureShellLiveTests
{
    private static string? Host => Environment.GetEnvironmentVariable("FLEETMATE_LIVE_SSH_HOST");

    private static SecureShellService? Create()
    {
        if (string.IsNullOrWhiteSpace(Host)) return null;
        var config = new SecureShellConfig
        {
            PrivateKeyPath = Environment.GetEnvironmentVariable("FLEETMATE_LIVE_SSH_KEY") ?? "~/.ssh/id_rsa.winadmins",
            PrivateKeyEnvVar = null,
            AcceptAllHostKeys = true,
            ConnectionTimeoutSeconds = 10,
            CommandTimeoutSeconds = 60
        };
        return new SecureShellService(config);
    }

    [Fact]
    public async Task Streaming_DeliversChunksBeforeCompletion()
    {
        using var ssh = Create();
        if (ssh == null) return;

        var chunks = new List<(TimeSpan at, string text)>();
        var started = DateTime.UtcNow;
        var script = "1..3 | ForEach-Object { \"tick $_\"; Start-Sleep -Seconds 1 }";

        var result = await ssh.ExecuteStreamingAsync(Host!, RemoteScriptEncoder.Wrap(script),
            chunk => { lock (chunks) chunks.Add((DateTime.UtcNow - started, chunk)); }, CancellationToken.None);

        Assert.Equal(SecureShellOutcome.Success, result.Outcome);
        Assert.Contains("tick 1", result.Stdout);
        Assert.Contains("tick 3", result.Stdout);
        Assert.True(chunks.Count >= 2, $"expected streamed chunks, got {chunks.Count}");
        // Streaming means the first line arrived well before the command ended,
        // not after it; the script itself takes about three seconds.
        var total = DateTime.UtcNow - started;
        Assert.True(chunks[0].at < total - TimeSpan.FromSeconds(1.5),
            $"first chunk arrived at {chunks[0].at} of {total}; output was not streamed");
    }

    [Fact]
    public async Task Streaming_CancellationEndsTheRun()
    {
        using var ssh = Create();
        if (ssh == null) return;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = await ssh.ExecuteStreamingAsync(Host!, RemoteScriptEncoder.Wrap("Start-Sleep -Seconds 30; 'done'"), null, cts.Token);

        Assert.Equal(SecureShellOutcome.Cancelled, result.Outcome);
        Assert.True(result.Duration < TimeSpan.FromSeconds(15), $"took {result.Duration}");
    }

    [Fact]
    public async Task Probe_ParsesOnARealHost()
    {
        using var ssh = Create();
        if (ssh == null) return;

        var result = await ssh.ExecuteStreamingAsync(Host!, RemoteScriptEncoder.Wrap(MachineProbe.ProbeScript), null, CancellationToken.None);
        Assert.Equal(SecureShellOutcome.Success, result.Outcome);

        var probe = MachineProbe.Parse("live", Host!, result.Stdout);
        Assert.NotEqual("", probe.ComputerName);
        Assert.NotEqual("", probe.OsVersion);
        Assert.NotEqual("", probe.Uptime);
        Assert.Contains(probe.JoinType, new[] { "entra", "domain", "hybrid", "none" });
        Assert.True(probe.SshPortListening, "we are connected over SSH, so port 22 must report listening");
    }

    [Fact]
    public async Task WrongUser_IsAuthFailedNotUnreachable()
    {
        using var ssh = Create();
        if (ssh == null) return;

        var result = await ssh.ExecuteStreamingAsync(Host!, "hostname", null, CancellationToken.None, username: "nosuchuser");
        Assert.Equal(SecureShellOutcome.AuthFailed, result.Outcome);
    }
}
