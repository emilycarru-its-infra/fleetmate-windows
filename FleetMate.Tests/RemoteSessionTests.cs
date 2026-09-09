using FleetMate.Core.Config;
using FleetMate.Core.Services.Manage;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Session launch is command-line and file construction plus a process
/// start; the construction is tested here exactly, and the launcher is a
/// recorder so nothing opens.
/// </summary>
public class RemoteSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fleetmate-rdp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Recorder : IProcessLauncher
    {
        public List<(string file, string args, bool hidden)> Calls { get; } = new();
        public void Start(string fileName, string arguments, bool hidden = false) => Calls.Add((fileName, arguments, hidden));
    }

    private (RemoteSessionLauncher launcher, Recorder recorder, RdpCredentialStore store) Build(string sshUser = "ops", string rdpUser = "", string profile = "")
    {
        var config = new ManageConfig { SshUser = sshUser, RdpUser = rdpUser, SshKeyPath = @"C:\keys\fleet key.pem", TerminalProfile = profile };
        var store = new RdpCredentialStore(_root);
        var recorder = new Recorder();
        var launcher = new RemoteSessionLauncher(config, store, recorder);
        return (launcher, recorder, store);
    }

    [Fact]
    public void SshCommandLine_QuotesKeyAndTargetsUserAtHost()
    {
        var line = RemoteSessionLauncher.SshCommandLine("10.0.0.5", "ops", @"C:\keys\fleet key.pem");
        Assert.EndsWith(" -i \"C:\\keys\\fleet key.pem\" -o StrictHostKeyChecking=accept-new -o ConnectTimeout=8 -o ServerAliveInterval=15 ops@10.0.0.5", line);
        Assert.Contains("ssh.exe", line);
    }

    [Fact]
    public void WindowsTerminalArguments_OneTabPerHostWithTitlesAndProfile()
    {
        var sessions = new[] { new SshSession("10.0.0.1", "Studio 01 (LAB-01)"), new SshSession("10.0.0.2", "Odd; \"name\"") };
        var args = RemoteSessionLauncher.WindowsTerminalArguments(sessions, "ops", @"C:\k\id", "Fleet");

        Assert.StartsWith("-w new new-tab -p Fleet --title \"Studio 01 (LAB-01)\" ", args);
        Assert.Contains(" ; new-tab -p Fleet --title \"Odd, 'name'\" ", args);
        Assert.Equal(2, args.Split(" ; ").Length);
        Assert.Contains("ops@10.0.0.1", args);
        Assert.Contains("ops@10.0.0.2", args);
    }

    [Fact]
    public void WindowsTerminalArguments_NoProfileWhenUnset()
    {
        var args = RemoteSessionLauncher.WindowsTerminalArguments(new[] { new SshSession("10.0.0.1", "A") }, "ops", @"C:\k\id", "");
        Assert.DoesNotContain(" -p ", args);
    }

    [Fact]
    public void ConsoleFallback_UsesStartWithTitle()
    {
        var args = RemoteSessionLauncher.ConsoleFallbackArguments(new SshSession("10.0.0.1", "A & B"), "ops", @"C:\k\id");
        Assert.StartsWith("/c start \"A ^& B\" ", args);
        Assert.Contains("ops@10.0.0.1", args);
    }

    [Fact]
    public void RdpArguments_TargetByAddressWindowed()
    {
        Assert.Equal("/v:10.0.0.9 /w:1600 /h:1000", RemoteSessionLauncher.RdpArguments("10.0.0.9"));
        Assert.Equal("/v:10.0.0.9 /w:1280 /h:800", RemoteSessionLauncher.RdpArguments("10.0.0.9", 1280, 800));
    }

    [Fact]
    public void CmdKey_ArgumentsTargetTermsrv()
    {
        Assert.Equal("/generic:TERMSRV/10.0.0.9 /user:ops /pass:\"p w\"", RemoteSessionLauncher.CmdKeyStoreArguments("10.0.0.9", "ops", "p w"));
        Assert.Equal("/delete:TERMSRV/10.0.0.9", RemoteSessionLauncher.CmdKeyDeleteArguments("10.0.0.9"));
    }

    [Fact]
    public void OpenRdp_WithoutCredential_LaunchesMstscDirectly()
    {
        var (launcher, recorder, _) = Build();
        launcher.OpenRdp("10.0.0.9");

        Assert.Single(recorder.Calls);
        Assert.Equal("mstsc.exe", recorder.Calls[0].file);
        Assert.Equal("/v:10.0.0.9 /w:1600 /h:1000", recorder.Calls[0].args);
    }

    [Fact]
    public void OpenRdp_WithCredential_RegistersItHiddenThenLaunches()
    {
        var (launcher, recorder, store) = Build(rdpUser: "rdpops");
        store.Save("secret pw");
        launcher.OpenRdp("10.0.0.9");

        Assert.Equal(2, recorder.Calls.Count);
        Assert.Equal("cmdkey.exe", recorder.Calls[0].file);
        Assert.True(recorder.Calls[0].hidden);
        Assert.Equal("/generic:TERMSRV/10.0.0.9 /user:rdpops /pass:\"secret pw\"", recorder.Calls[0].args);
        Assert.Equal("mstsc.exe", recorder.Calls[1].file);
        Assert.Equal("/v:10.0.0.9 /w:1600 /h:1000", recorder.Calls[1].args);
    }

    [Fact]
    public void CredentialStore_RoundTripsAndDeletes()
    {
        var store = new RdpCredentialStore(_root);
        Assert.False(store.HasCredential);
        Assert.Null(store.Load());

        store.Save("hunter2");
        Assert.True(store.HasCredential);
        Assert.Equal("hunter2", store.Load());
        Assert.DoesNotContain("hunter2", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(store.Path)));

        store.Save("");
        Assert.False(store.HasCredential);
    }

    [Fact]
    public void CredentialStore_CorruptFileReadsAsUnset()
    {
        var store = new RdpCredentialStore(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(store.Path, new byte[] { 1, 2, 3 });
        Assert.Null(store.Load());
    }

    [Fact]
    public void OpenSshAndRdp_OpensBoth()
    {
        var (launcher, recorder, _) = Build();
        launcher.OpenSshAndRdp("10.0.0.1", "Studio 01");
        Assert.Contains(recorder.Calls, c => c.file is "wt.exe" or "cmd.exe");
        Assert.Contains(recorder.Calls, c => c.file == "mstsc.exe");
    }

}
