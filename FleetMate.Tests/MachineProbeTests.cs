using FleetMate.Core.Models.Manage;
using FleetMate.Core.Services.Manage;
using Xunit;

namespace FleetMate.Tests;

public class MachineProbeTests
{
    private const string Raw =
        "user=EXAMPLE\\student01\r\n" +
        "host=LAB-01\r\n" +
        "os=Windows 11 Enterprise 10.0.26100\r\n" +
        "build=26100\r\n" +
        "uptime=3d 4h 12m\r\n" +
        "join=hybrid\r\n" +
        "rdp=enabled\r\n" +
        "nla=yes\r\n" +
        "rdp_port=listening\r\n" +
        "ssh_port=listening\r\n" +
        "sshd=Running\r\n" +
        "cimian_id=Shared/Curriculum/Studio/R101/Studio01\r\n" +
        "cimian_last=2026-09-09 03:12\r\n" +
        "free_gb=412.5\r\n" +
        "apps=Blender, Photoshop,Chrome,,\r\n";

    [Fact]
    public void Parse_ReadsEveryField()
    {
        var p = MachineProbe.Parse("LAB-01", "10.1.2.3", Raw);

        Assert.Equal("EXAMPLE\\student01", p.ConsoleUser);
        Assert.Equal("student01", p.ConsoleUserShort);
        Assert.False(p.AtLoginWindow);
        Assert.Equal("Windows 11 Enterprise 10.0.26100", p.OsVersion);
        Assert.Equal("26100", p.OsBuild);
        Assert.Equal("3d 4h 12m", p.Uptime);
        Assert.Equal("hybrid", p.JoinType);
        Assert.True(p.RdpEnabled);
        Assert.True(p.RdpNlaRequired);
        Assert.True(p.RdpPortListening);
        Assert.True(p.RdpReady);
        Assert.True(p.SshReady);
        Assert.Equal("Running", p.SshdStatus);
        Assert.Equal("Shared/Curriculum/Studio/R101/Studio01", p.CimianClientIdentifier);
        Assert.Equal("412.5", p.FreeSpaceGb);
        Assert.Equal(new[] { "Blender", "Photoshop", "Chrome" }, p.TopApps);
    }

    [Fact]
    public void Parse_MissingKeysReadAsEmptyOrFalse()
    {
        var p = MachineProbe.Parse("X", "10.0.0.1", "user=\nrdp=disabled\n");

        Assert.True(p.AtLoginWindow);
        Assert.False(p.RdpEnabled);
        Assert.False(p.RdpReady);
        Assert.False(p.SshReady);
        Assert.Equal("", p.OsVersion);
        Assert.Empty(p.TopApps);
    }

    [Fact]
    public void Parse_KeepsEqualsSignsInsideValues()
    {
        var d = MachineProbe.ParseKeyValues("a=x=y\nnoequals\n=empty\nb= spaced \n");
        Assert.Equal("x=y", d["a"]);
        Assert.Equal("spaced", d["b"]);
        Assert.False(d.ContainsKey(""));
        Assert.Equal(2, d.Count);
    }

    [Fact]
    public void ProbeScript_FitsTheRemoteCommandLine()
    {
        Assert.True(RemoteScriptEncoder.Fits(MachineProbe.ProbeScript),
            $"probe script encodes to {RemoteScriptEncoder.Prefix.Length + RemoteScriptEncoder.Encode(MachineProbe.ProbeScript).Length} characters");
    }

    [Fact]
    public void ProbeScript_EmitsEveryKeyTheParserReads()
    {
        foreach (var key in new[] { "user", "host", "os", "build", "uptime", "join", "rdp", "nla", "rdp_port", "ssh_port", "sshd", "cimian_id", "cimian_last", "free_gb", "apps" })
        {
            Assert.Contains($"\"{key}=", MachineProbe.ProbeScript);
        }
    }
}
