using FleetMate.Core.Services.Manage;
using Xunit;

namespace FleetMate.Tests;

public class RemoteScriptEncoderTests
{
    [Fact]
    public void Encode_IsBase64Utf16LittleEndian()
    {
        var encoded = RemoteScriptEncoder.Encode("hostname");
        // "h\0o\0s\0..." in base64
        Assert.Equal("aABvAHMAdABuAGEAbQBlAA==", encoded);
        Assert.Equal("hostname", RemoteScriptEncoder.Decode(encoded));
    }

    [Fact]
    public void Wrap_PrefixesThePowerShellInvocation()
    {
        var line = RemoteScriptEncoder.Wrap("Get-Service sshd");
        Assert.StartsWith("powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ", line);
        Assert.Equal("Get-Service sshd", RemoteScriptEncoder.Decode(line[RemoteScriptEncoder.Prefix.Length..]));
    }

    [Fact]
    public void Wrap_NormalisesCrlfAndStripsBom()
    {
        var line = RemoteScriptEncoder.Wrap("\uFEFF$a = 1\r\n$b = 2\r$c = 3");
        Assert.Equal("$a = 1\n$b = 2\n$c = 3", RemoteScriptEncoder.Decode(line[RemoteScriptEncoder.Prefix.Length..]));
    }

    [Fact]
    public void Wrap_RefusesScriptsThatCannotFit()
    {
        var big = new string('x', 4000);
        Assert.False(RemoteScriptEncoder.Fits(big));
        var ex = Assert.Throws<ArgumentException>(() => RemoteScriptEncoder.Wrap(big));
        Assert.Contains("too long", ex.Message);
    }

    [Fact]
    public void Fits_AcceptsATypicalOneLiner()
    {
        Assert.True(RemoteScriptEncoder.Fits("Get-Content 'C:\\ProgramData\\ManagedInstalls\\Logs\\ManagedSoftwareUpdate.log' -Tail 50 | Select-String -Pattern 'ERROR|WARN'"));
    }
}
