using FleetMate.Core.Config;
using FleetMate.Core.Services.Agent;
using Xunit;

namespace FleetMate.Tests;

public class AgentCliUpdaterTests
{
    private const string Home = @"C:\Users\someone";
    private const string AppData = @"C:\Users\someone\AppData\Roaming";
    private const string Local = @"C:\Users\someone\AppData\Local";

    private static AgentCliUpdater Updater(
        ISet<string>? files = null, AgentCliUpdater.Runner? run = null, Func<string, string>? links = null,
        Func<string, string?>? onPath = null, Func<AgentCli, bool>? inUse = null) =>
        new(Home, AppData, Local,
            run ?? (_ => Task.FromResult(new AgentCliProcessOutput(0, "", ""))),
            exists: p => files?.Contains(p) ?? false,
            resolveLinks: links ?? (p => p),
            findOnPath: onPath ?? (_ => null),
            inUse: inUse ?? (_ => false),
            log: _ => { });

    // ── Install method detection ─────────────────────────────────────────

    [Fact]
    public void NpmShim_BesideItsPackage()
    {
        var files = new HashSet<string> { $@"{AppData}\npm\node_modules\@openai\codex\package.json" };
        var install = Updater(files).Detect(AgentCli.Codex, $@"{AppData}\npm\codex.cmd", $@"{AppData}\npm\codex.cmd");
        Assert.Equal(AgentCliInstallMethod.Npm, install.Method);
        Assert.Equal("@openai/codex", install.Package);
        Assert.Equal($@"{AppData}\npm", install.Prefix);
    }

    [Fact]
    public void Npm_FromAResolvedNodeModulesPath()
    {
        var install = Updater().Detect(AgentCli.Claude, @"C:\nodejs\claude.cmd",
            @"C:\nodejs\node_modules\@anthropic-ai\claude-code\cli.js");
        Assert.Equal(AgentCliInstallMethod.Npm, install.Method);
        Assert.Equal(@"C:\nodejs", install.Prefix);
    }

    [Fact]
    public void Winget_PortablePackage()
    {
        var install = Updater().Detect(AgentCli.Codex, $@"{Local}\Microsoft\WinGet\Links\codex.exe",
            $@"{Local}\Microsoft\WinGet\Packages\OpenAI.Codex_Microsoft.Winget.Source_8wekyb3d8bbwe\codex.exe");
        Assert.Equal(AgentCliInstallMethod.Winget, install.Method);
        Assert.Equal("OpenAI.Codex", install.Package);
    }

    [Fact]
    public void ClaudeNativeInstaller()
    {
        var install = Updater().Detect(AgentCli.Claude, $@"{Home}\.local\bin\claude.exe", $@"{Home}\.local\bin\claude.exe");
        Assert.Equal(AgentCliInstallMethod.ClaudeNative, install.Method);
    }

    [Fact]
    public void UnknownLocation()
    {
        var install = Updater().Detect(AgentCli.Codex, @"C:\Tools\codex.exe", @"C:\Tools\codex.exe");
        Assert.Equal(AgentCliInstallMethod.Unknown, install.Method);
        Assert.Null(Updater().UpdateCommand(install));
    }

    [Fact]
    public void Locate_LooksInTheInstallFolders_WhenPathLacksTheCli()
    {
        var files = new HashSet<string> { $@"{Home}\.local\bin\claude.exe" };
        Assert.Equal($@"{Home}\.local\bin\claude.exe", Updater(files).Locate(AgentCli.Claude));
        Assert.Null(Updater(files).Locate(AgentCli.Codex));
    }

    // ── Commands ─────────────────────────────────────────────────────────

    [Fact]
    public void Npm_UpdatesIntoTheSamePrefix_Unattended()
    {
        var install = new AgentCliInstall(AgentCli.Codex, "", "", AgentCliInstallMethod.Npm, "@openai/codex", $@"{AppData}\npm");
        var command = Updater(onPath: n => n == "npm.cmd" ? @"C:\nodejs\npm.cmd" : null).UpdateCommand(install)!;
        Assert.Equal(@"C:\nodejs\npm.cmd", command.Executable);
        Assert.Equal(new[] { "install", "--global", "--prefix", $@"{AppData}\npm", "--no-fund", "--no-audit", "@openai/codex@latest" }, command.Arguments);
        Assert.Equal("true", command.Environment!["npm_config_yes"]);
    }

    [Fact]
    public void Winget_UpgradesSilently()
    {
        var install = new AgentCliInstall(AgentCli.Codex, "", "", AgentCliInstallMethod.Winget, "OpenAI.Codex");
        var command = Updater().UpdateCommand(install)!;
        Assert.Equal(new[] { "upgrade", "--id", "OpenAI.Codex", "--exact", "--silent", "--disable-interactivity",
            "--accept-source-agreements", "--accept-package-agreements" }, command.Arguments);
    }

    [Fact]
    public void ClaudeNative_UsesClaudeUpdate()
    {
        var install = new AgentCliInstall(AgentCli.Claude, $@"{Home}\.local\bin\claude.exe", "", AgentCliInstallMethod.ClaudeNative);
        var command = Updater().UpdateCommand(install)!;
        Assert.Equal($@"{Home}\.local\bin\claude.exe", command.Executable);
        Assert.Equal(new[] { "update" }, command.Arguments);
    }

    [Theory]
    [InlineData("codex-cli 0.162.1\n", "0.162.1")]
    [InlineData("2.1.296 (Claude Code)", "2.1.296")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("no version here", null)]
    public void ParsesVersions(string output, string? expected) =>
        Assert.Equal(expected, AgentCliUpdater.ParseVersion(output));

    // ── A whole run ──────────────────────────────────────────────────────

    private static AgentCliUpdater NpmCodexOnly(List<string> ran, string latest, Func<AgentCli, bool>? inUse = null)
    {
        var shim = $@"{AppData}\npm\codex.cmd";
        var files = new HashSet<string> { shim, $@"{AppData}\npm\node_modules\@openai\codex\package.json" };
        return Updater(files, run: c =>
        {
            ran.Add(c.Display);
            if (c.Arguments.SequenceEqual(new[] { "--version" })) return Task.FromResult(new AgentCliProcessOutput(0, "codex-cli 0.162.1", ""));
            if (c.Arguments.FirstOrDefault() == "view") return Task.FromResult(new AgentCliProcessOutput(0, latest + "\n", ""));
            return Task.FromResult(new AgentCliProcessOutput(0, "", ""));
        }, inUse: inUse);
    }

    [Fact]
    public async Task Run_SkipsTheUpgradeWhenCurrent_AndAMissingCli()
    {
        var ran = new List<string>();
        var state = await NpmCodexOnly(ran, "0.162.1").RunAsync(checkOnly: false);
        Assert.DoesNotContain(ran, r => r.Contains(" install "));
        Assert.Equal("Up to date", state.Statuses.Single(s => s.Cli == AgentCli.Codex).Message);
        Assert.False(state.Statuses.Single(s => s.Cli == AgentCli.Claude).Installed);
        Assert.NotNull(state.LastChecked);
    }

    [Fact]
    public async Task Run_Upgrades_WhenNewer()
    {
        var ran = new List<string>();
        await NpmCodexOnly(ran, "0.163.0").RunAsync(checkOnly: false);
        Assert.Contains(ran, r => r.Contains("install --global") && r.EndsWith("@openai/codex@latest"));
    }

    [Fact]
    public async Task Run_LeavesAnInUseNpmInstallForLater_AndKeepsTheSchedule()
    {
        var ran = new List<string>();
        var earlier = DateTimeOffset.UtcNow.AddHours(-7);
        var state = await NpmCodexOnly(ran, "0.163.0", inUse: _ => true)
            .RunAsync(checkOnly: false, new AgentCliUpdateState { LastChecked = earlier });
        Assert.DoesNotContain(ran, r => r.Contains(" install "));
        Assert.Equal(earlier, state.LastChecked);
        Assert.Contains("In use", state.Statuses.Single(s => s.Cli == AgentCli.Codex).Message);
    }

    [Fact]
    public async Task Run_TreatsWingetsNoUpdateCodeAsCurrent()
    {
        var link = $@"{Local}\Microsoft\WinGet\Links\codex.exe";
        var updater = Updater(new HashSet<string> { link },
            run: c => Task.FromResult(c.Arguments.FirstOrDefault() == "upgrade"
                ? new AgentCliProcessOutput(AgentCliUpdater.WingetNoUpdate, "No available upgrade found.", "")
                : new AgentCliProcessOutput(0, "codex-cli 0.162.1", "")),
            links: p => p == link ? $@"{Local}\Microsoft\WinGet\Packages\OpenAI.Codex_Microsoft.Winget.Source_8wekyb3d8bbwe\codex.exe" : p);
        var state = await updater.RunAsync(checkOnly: false);
        Assert.Equal("Up to date", state.Statuses.Single(s => s.Cli == AgentCli.Codex).Message);
    }

    [Fact]
    public async Task CheckOnly_KeepsTheSchedule()
    {
        var earlier = DateTimeOffset.FromUnixTimeSeconds(1000);
        var state = await Updater().RunAsync(checkOnly: true, new AgentCliUpdateState { LastChecked = earlier });
        Assert.Equal(earlier, state.LastChecked);
    }

    [Fact]
    public void Staleness()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(new AgentCliUpdateState().IsStale(now));
        Assert.False(new AgentCliUpdateState { LastChecked = now.AddMinutes(-1) }.IsStale(now));
        Assert.True(new AgentCliUpdateState { LastChecked = now.AddHours(-7) }.IsStale(now));
    }

    [Fact]
    public void State_RoundTripsThroughItsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fm-updates-{Guid.NewGuid():N}.json");
        try
        {
            var state = new AgentCliUpdateState
            {
                LastChecked = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000),
                Statuses = new[] { new AgentCliStatus { Cli = AgentCli.Codex, Installed = true, Method = AgentCliInstallMethod.Npm, Version = "1.0.0" } },
            };
            state.Save(path);
            Assert.Contains("\"Npm\"", File.ReadAllText(path));
            var back = AgentCliUpdateState.Load(path);
            Assert.Equal(state.LastChecked, back.LastChecked);
            Assert.Equal("1.0.0", back.Statuses[0].Version);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── Settings ─────────────────────────────────────────────────────────

    [Fact]
    public void KeepClisCurrent_IsOnByDefault_AndReadFromTheRegistry()
    {
        Assert.True(new FleetMateConfig().Terminal.KeepClisCurrent);
        var config = new FleetMateConfig();
        FleetMateConfig.ApplyRegistryValues(name => name == "AgentKeepClisCurrent" ? "0" : null, config, fromPolicy: false);
        Assert.False(config.Terminal.KeepClisCurrent);
    }
}
