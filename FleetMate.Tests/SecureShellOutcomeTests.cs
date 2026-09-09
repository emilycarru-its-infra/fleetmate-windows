using System.Net.Sockets;
using FleetMate.Core.Models;
using FleetMate.Core.Services;
using Renci.SshNet.Common;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The outcome classifier separates the cases operators confuse in the field:
/// a host that is off, a host that rejected the key, and a command that ran
/// and failed. Exceptions here are constructed, never captured.
/// </summary>
public class SecureShellOutcomeTests
{
    [Fact]
    public void Authentication_IsAuthFailed()
    {
        Assert.Equal(SecureShellOutcome.AuthFailed, SecureShellService.ClassifyException(new SshAuthenticationException("Permission denied (publickey).")));
        Assert.Equal(SecureShellOutcome.AuthFailed, SecureShellService.ClassifyException(new Exception("No suitable authentication method found to complete authentication (publickey).")));
    }

    [Fact]
    public void Socket_IsUnreachable()
    {
        Assert.Equal(SecureShellOutcome.Unreachable, SecureShellService.ClassifyException(new SocketException((int)SocketError.ConnectionRefused)));
        Assert.Equal(SecureShellOutcome.Unreachable, SecureShellService.ClassifyException(new SocketException((int)SocketError.TimedOut)));
        Assert.Equal(SecureShellOutcome.Unreachable, SecureShellService.ClassifyException(new SshConnectionException("Connection lost")));
        Assert.Equal(SecureShellOutcome.Unreachable, SecureShellService.ClassifyException(new Exception("No such host is known")));
        Assert.Equal(SecureShellOutcome.Unreachable, SecureShellService.ClassifyException(new Exception("Connection timed out")));
    }

    [Fact]
    public void CommandTimeout_IsTimeout()
    {
        Assert.Equal(SecureShellOutcome.Timeout, SecureShellService.ClassifyException(new SshOperationTimeoutException("Command timed out")));
        Assert.Equal(SecureShellOutcome.Timeout, SecureShellService.ClassifyException(new Exception("Operation timed out")));
    }

    [Fact]
    public void HostKey_IsHostKeyRejected()
    {
        Assert.Equal(SecureShellOutcome.HostKeyRejected, SecureShellService.ClassifyException(new Exception("Host key verification failed; identification has changed")));
    }

    [Fact]
    public void Cancellation_IsCancelled()
    {
        Assert.Equal(SecureShellOutcome.Cancelled, SecureShellService.ClassifyException(new OperationCanceledException()));
        Assert.Equal(SecureShellOutcome.Cancelled, SecureShellService.ClassifyException(new TaskCanceledException()));
    }

    [Fact]
    public void Unknown_IsError()
    {
        Assert.Equal(SecureShellOutcome.Error, SecureShellService.ClassifyException(new InvalidOperationException("something else")));
    }

    [Fact]
    public void Result_DefaultsToErrorUntilClassified()
    {
        Assert.Equal(SecureShellOutcome.Error, new SecureShellResult().Outcome);
    }

    [Fact]
    public void DefaultUsername_IsTheFleetAdminAccount()
    {
        Assert.Equal("winadmins", new SecureShellConfig().DefaultUsername);
        Assert.Equal("winadmins", SecureShellConfig.FleetAdminUsername);
    }
}
