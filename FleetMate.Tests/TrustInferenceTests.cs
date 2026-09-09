using FleetMate.Core.Models.Manage;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Pattern-based trust for commands without metadata: the Windows idioms an
/// operator would type into the custom command box.
/// </summary>
public class TrustInferenceTests
{
    [Theory]
    [InlineData("hostname")]
    [InlineData("Get-Service sshd")]
    [InlineData("Get-ChildItem C:\\ProgramData\\ManagedInstalls\\Logs")]
    [InlineData("Get-Content 'C:\\ProgramData\\ManagedInstalls\\Logs\\ManagedSoftwareUpdate.log' -Tail 50")]
    [InlineData("managedsoftwareupdate --checkonly")]
    [InlineData("quser")]
    public void Safe(string command) =>
        Assert.Equal(CommandTrustLevel.Safe, TrustInference.Infer(command));

    [Theory]
    [InlineData("managedsoftwareupdate --auto")]
    [InlineData("managedsoftwareupdate --installonly")]
    [InlineData("Restart-Service Spooler")]
    [InlineData("gpupdate /force")]
    [InlineData("logoff 2")]
    [InlineData("Set-ItemProperty -Path HKLM:\\SOFTWARE\\X -Name Y -Value 1")]
    [InlineData("Add-LocalGroupMember -Group Administrators -Member x")]
    [InlineData("Start-ScheduledTask -TaskName Nightly")]
    public void Caution(string command) =>
        Assert.Equal(CommandTrustLevel.Caution, TrustInference.Infer(command));

    [Theory]
    [InlineData("shutdown /r /t 0")]
    [InlineData("Restart-Computer -Force")]
    [InlineData("Remove-Item -Recurse -Force C:\\ProgramData\\ManagedInstalls\\Cache")]
    [InlineData("Remove-LocalUser -Name old")]
    [InlineData("net user old /delete")]
    [InlineData("manage-bde -off C:")]
    [InlineData("reg delete HKLM\\SOFTWARE\\X /f")]
    [InlineData("msiexec /x {GUID} /qn")]
    [InlineData("dsregcmd /leave")]
    [InlineData("taskkill /f /im explorer.exe")]
    public void Destructive(string command) =>
        Assert.Equal(CommandTrustLevel.Destructive, TrustInference.Infer(command));

    [Fact]
    public void Destructive_WinsOverCaution()
    {
        // Restart-Service is caution, but restarting the spooler resets print state.
        Assert.Equal(CommandTrustLevel.Destructive, TrustInference.Infer("Restart-Service Spooler -Force; Remove-Item -Recurse C:\\Windows\\System32\\spool\\PRINTERS\\*"));
    }

    [Fact]
    public void Infer_IsCaseInsensitiveAndNullSafe()
    {
        Assert.Equal(CommandTrustLevel.Destructive, TrustInference.Infer("SHUTDOWN /S"));
        Assert.Equal(CommandTrustLevel.Safe, TrustInference.Infer(null!));
    }

    [Fact]
    public void TrustLevel_YamlNamesRoundTrip()
    {
        foreach (var level in Enum.GetValues<CommandTrustLevel>())
        {
            Assert.True(CommandTrustLevelExtensions.TryParse(level.ToYaml(), out var parsed));
            Assert.Equal(level, parsed);
        }
        Assert.False(CommandTrustLevelExtensions.TryParse("nope", out _));
        Assert.False(CommandTrustLevelExtensions.TryParse(null, out _));
    }
}
