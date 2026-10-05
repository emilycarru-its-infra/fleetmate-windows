using System.Collections.Concurrent;
using FleetMate.Core.Models;
using FleetMate.Core.Models.Manage;
using FleetMate.Core.Services.Manage;
using Xunit;

namespace FleetMate.Tests;

public class PackageInstallTests
{
    private sealed class FakeTransport : IRemoteRunner, IRemoteFileCopier
    {
        public bool FailCopyFor { get; init; }
        public ConcurrentBag<(string Ip, string Remote)> Uploads { get; } = new();
        public ConcurrentBag<(string Ip, string Command)> Commands { get; } = new();

        public Task<SecureShellResult> UploadAsync(string ip, string localPath, string remotePath, CancellationToken ct,
            string? username = null, string? deviceName = null)
        {
            Uploads.Add((ip, remotePath));
            return Task.FromResult(new SecureShellResult
            {
                Outcome = FailCopyFor && ip == "10.0.0.2" ? SecureShellOutcome.Unreachable : SecureShellOutcome.Success
            });
        }

        public Task<SecureShellResult> RunAsync(string ip, string command, Action<string>? onChunk, CancellationToken ct,
            string? username = null, string? deviceName = null)
        {
            Commands.Add((ip, command));
            onChunk?.Invoke("Installed\n");
            return Task.FromResult(new SecureShellResult { Outcome = SecureShellOutcome.Success, ExitCode = 0 });
        }
    }

    private sealed class Recorder : IRunObserver
    {
        public ConcurrentDictionary<string, CommandRunStatus> Status { get; } = new();
        public ConcurrentDictionary<string, string> Out { get; } = new();
        public void Started(string serial) { }
        public void Output(string serial, string chunk) => Out.AddOrUpdate(serial, chunk, (_, s) => s + chunk);
        public void Finished(string serial, CommandRunStatus status, int? exitCode, string stderr, string? error) => Status[serial] = status;
    }

    private static RunTarget Target(string serial, string ip) =>
        new(new RosterComputer { Serial = serial, Hostname = "H-" + serial, Status = "Active" }, ip);

    [Theory]
    [InlineData("app.msi", true)]
    [InlineData("App.MSIX", true)]
    [InlineData("bundle.msixbundle", true)]
    [InlineData("setup.exe", false)]
    [InlineData("installer.pkg", false)]
    public void SupportsUnattendedWindowsPackageKinds(string file, bool supported) =>
        Assert.Equal(supported, PackageInstall.IsSupported(file));

    [Fact]
    public void RemotePathIsUniqueInSystemTempAndSftpSpellsItWithASlash()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var remote = PackageInstall.RemotePath(@"C:\Downloads\Tool.MSI", id);
        Assert.Equal(@"C:\Windows\Temp\FleetMate-11111111222233334444555555555555.msi", remote);
        Assert.Equal("/C:/Windows/Temp/FleetMate-11111111222233334444555555555555.msi", PackageInstall.SftpPath(remote));
    }

    [Fact]
    public void MsiScriptInstallsSilentlyTreats3010AsSuccessAndAlwaysRemovesTheCopy()
    {
        var script = PackageInstall.Script(@"C:\Windows\Temp\FleetMate-x.msi", "Tool's Setup.msi");
        Assert.Contains("msiexec.exe", script);
        Assert.Contains("'/qn'", script);
        Assert.Contains("'/norestart'", script);
        Assert.Contains("-eq 3010", script);
        Assert.Contains("finally", script);
        Assert.Contains(@"Remove-Item -LiteralPath 'C:\Windows\Temp\FleetMate-x.msi'", script);
        Assert.Contains("'Tool''s Setup.msi'", script);
    }

    [Fact]
    public void MsixScriptProvisionsForEveryUser()
    {
        var script = PackageInstall.Script(@"C:\Windows\Temp\FleetMate-x.msix", "App.msix");
        Assert.Contains("Add-AppxProvisionedPackage -Online", script);
        Assert.DoesNotContain("msiexec", script);
    }

    [Fact]
    public async Task InstallCopiesThenRunsPerMachineAndAFailedCopySkipsTheInstall()
    {
        var transport = new FakeTransport { FailCopyFor = true };
        var runner = new CommandRunner(transport);
        var recorder = new Recorder();

        await runner.InstallPackageAsync(new[] { Target("S1", "10.0.0.1"), Target("S2", "10.0.0.2") },
            @"C:\Downloads\Tool.msi", transport, recorder, CancellationToken.None);

        Assert.Equal(2, transport.Uploads.Count);
        Assert.Single(transport.Commands);
        Assert.Equal("10.0.0.1", transport.Commands.Single().Ip);
        Assert.Equal(CommandRunStatus.Success, recorder.Status["S1"]);
        Assert.Equal(CommandRunStatus.Offline, recorder.Status["S2"]);
        Assert.Contains("Copying Tool.msi", recorder.Out["S1"]);
        Assert.Contains("Installing Tool.msi", recorder.Out["S1"]);
    }
}

public class LabPickerTests
{
    private static RosterRoom Lab(string number, string area, int machines) => new()
    {
        Number = number,
        Computers = Enumerable.Range(1, machines)
            .Select(i => new RosterComputer { Serial = $"{number}-{i}", Area = area, Status = "Active" }).ToList()
    };

    private static readonly RosterRoom[] Labs =
    {
        Lab("B10", "North", 4), Lab("B2", "North", 3), Lab("C1", "South", 5), Lab("Z9", "", 1)
    };

    [Fact]
    public void GroupsByAreaInNaturalOrderWithOtherLast()
    {
        var areas = LabPicker.Group(Labs);
        Assert.Equal(new[] { "North", "South", LabPicker.OtherArea }, areas.Select(a => a.Name));
        Assert.Equal(new[] { "B2", "B10" }, areas[0].Rooms.Select(r => r.Number));
        Assert.Equal(7, areas[0].ComputerCount);
    }

    [Fact]
    public void FilterKeepsWholeAreaOnAreaMatchAndOnlyMatchingLabsOtherwise()
    {
        var areas = LabPicker.Group(Labs);
        Assert.Equal(2, LabPicker.Filter(areas, "north").Single().Rooms.Count);
        var byLab = LabPicker.Filter(areas, "b1");
        Assert.Equal("B10", byLab.Single().Rooms.Single().Number);
        Assert.Empty(LabPicker.Filter(areas, "nothing"));
    }

    [Fact]
    public void DroppingAnAreaAddsEveryLabInItAndARoomAddsOne()
    {
        var areas = LabPicker.Group(Labs);
        var selected = new HashSet<string>();

        Assert.True(LabPicker.ApplyDrop(selected, LabPicker.AreaPayload(areas[0]), areas));
        Assert.Equal(2, selected.Count);

        var south = areas[1].Rooms[0];
        Assert.True(LabPicker.ApplyDrop(selected, LabPicker.RoomPayload(south), areas));
        Assert.Contains(south.Id, selected);
        Assert.Equal("3 labs · 12 machines", LabPicker.Summary(LabPicker.Selected(Labs, selected)));
    }

    [Theory]
    [InlineData("C1")]
    [InlineData("room:C1|")]
    [InlineData("fleetmate-lab-picker:room:missing")]
    [InlineData("fleetmate-lab-picker:area:Nowhere")]
    [InlineData(null)]
    public void StrayOrUnknownDropsSelectNothing(string? payload)
    {
        var selected = new HashSet<string>();
        Assert.False(LabPicker.ApplyDrop(selected, payload, LabPicker.Group(Labs)));
        Assert.Empty(selected);
    }

    [Fact]
    public void ComputersAreUniqueBySerialAcrossLabs()
    {
        var shared = new RosterComputer { Serial = "SHARED", Area = "North", Status = "Active" };
        var a = new RosterRoom { Number = "A", Computers = { shared } };
        var b = new RosterRoom { Number = "B", Computers = { shared } };
        Assert.Single(LabPicker.Computers(new[] { a, b }));
    }
}
