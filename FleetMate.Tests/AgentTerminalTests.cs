using System.Collections;
using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Services.Terminal;
using Xunit;

namespace FleetMate.Tests;

public class AgentTerminalTests
{
    private static Func<string, string?> OnPath(params string[] found) =>
        name => found.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("shell")]
    [InlineData("SHELL")]
    public void Shell_PrefersPowerShell7(string? setting)
    {
        var command = AgentCommands.Resolve(setting, OnPath(@"C:\pwsh\pwsh.exe", @"C:\Windows\powershell.exe"));
        Assert.Equal(@"C:\pwsh\pwsh.exe -NoLogo", command.CommandLine);
    }

    [Fact]
    public void Shell_FallsBackToWindowsPowerShell() =>
        Assert.Equal(@"C:\Windows\powershell.exe -NoLogo",
            AgentCommands.Resolve("shell", OnPath(@"C:\Windows\powershell.exe")).CommandLine);

    [Theory]
    [InlineData("claude", @"C:\bin\claude.exe")]
    [InlineData("codex", @"C:\bin\codex.exe")]
    [InlineData("claude-remote", @"C:\bin\claude.exe --remote")]
    [InlineData("codex-remote", @"C:\bin\codex.exe --remote")]
    public void Presets_MapToTheirCli(string setting, string expected) =>
        Assert.Equal(expected, AgentCommands.Resolve(setting, OnPath(@"C:\bin\claude.exe", @"C:\bin\codex.exe")).CommandLine);

    [Fact]
    public void NpmShim_RunsThroughCmd() =>
        Assert.Equal(@"cmd.exe /d /c C:\npm\claude.cmd --remote",
            AgentCommands.Resolve("claude-remote", OnPath(@"C:\npm\claude.cmd")).CommandLine);

    [Fact]
    public void CustomCommand_KeepsQuotedArguments()
    {
        var command = AgentCommands.Resolve("\"C:\\Tools\\My Agent\\agent.exe\" --model fast \"a b\"", _ => null);
        Assert.Equal(@"C:\Tools\My Agent\agent.exe", command.FileName);
        Assert.Equal(new[] { "--model", "fast", "a b" }, command.Arguments);
        Assert.Equal("\"C:\\Tools\\My Agent\\agent.exe\" --model fast \"a b\"", command.CommandLine);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("has space", "\"has space\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    public void Quote_FollowsTheArgvRules(string arg, string expected) =>
        Assert.Equal(expected, CommandLineBuilder.Quote(arg));

    [Fact]
    public void RepoDefaults_SeedReposUntilTheOperatorHasTheirOwn()
    {
        var config = new FleetMateConfig();
        FleetMateConfig.ApplyRegistryValues(name => name == "RepoDefaults" ? new[] { @"C:\src\one", "https://git.example/org/two.git", "" } : null,
            config, fromPolicy: true);

        Assert.Equal(new[] { @"C:\src\one", "https://git.example/org/two.git" }, config.Terminal.EffectiveRepos);

        FleetMateConfig.ApplyRegistryValues(name => name == "Repos" ? new[] { @"D:\mine" } : null, config, fromPolicy: false);
        Assert.Equal(new[] { @"D:\mine" }, config.Terminal.EffectiveRepos);
    }

    [Fact]
    public void RepoDefaults_ComeOnlyFromPolicy_AndAcceptSemicolonText()
    {
        var config = new FleetMateConfig();
        FleetMateConfig.ApplyRegistryValues(name => name == "RepoDefaults" ? @"C:\a;C:\b" : null, config, fromPolicy: false);
        Assert.Empty(config.Terminal.RepoDefaults);

        FleetMateConfig.ApplyRegistryValues(name => name == "RepoDefaults" ? @"C:\a;C:\b" : null, config, fromPolicy: true);
        Assert.Equal(new[] { @"C:\a", @"C:\b" }, config.Terminal.RepoDefaults);
    }

    [Fact]
    public void AgentSettings_ReadFromTheOperatorsKey()
    {
        var config = new FleetMateConfig();
        FleetMateConfig.ApplyRegistryValues(name => name switch { "AgentCommand" => "codex", "AgentAutoStart" => "1", _ => null },
            config, fromPolicy: false);
        Assert.Equal("codex", config.Terminal.AgentCommand);
        Assert.True(config.Terminal.AgentAutoStart);
    }

    [Fact]
    public void CloneUrls_MapUnderTheReposRoot()
    {
        var repo = RepoLocator.Resolve("https://git.example/org/fleet-tools.git", @"C:\Users\x\FleetMate\repos");
        Assert.Equal("fleet-tools", repo.Name);
        Assert.Equal(@"C:\Users\x\FleetMate\repos\fleet-tools", repo.Path);
        Assert.Equal("https://git.example/org/fleet-tools.git", repo.CloneUrl);

        var local = RepoLocator.Resolve(@"C:\src\thing\", @"C:\root");
        Assert.Null(local.CloneUrl);
        Assert.Equal("thing", local.Name);
        Assert.True(RepoLocator.IsCloneUrl("git@git.example:org/repo.git"));
    }

    [Fact]
    public void ContextFile_CarriesTabAndSelections()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fm-context-{Guid.NewGuid():N}.json");
        try
        {
            var file = new AppContextFile(path);
            file.SetTab("Devices");
            file.SetSelection("device", new[]
            {
                new ContextSelection("device", "abc", new Dictionary<string, string?> { ["serial"] = "S1", ["name"] = "PC-1" }),
            });

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            Assert.Equal("Devices", root.GetProperty("tab").GetString());
            var device = root.GetProperty("selection").GetProperty("device")[0];
            Assert.Equal("abc", device.GetProperty("id").GetString());
            Assert.Equal("S1", device.GetProperty("fields").GetProperty("serial").GetString());

            file.SetSelection("device", Array.Empty<ContextSelection>());
            using var cleared = JsonDocument.Parse(File.ReadAllText(path));
            Assert.False(cleared.RootElement.GetProperty("selection").TryGetProperty("device", out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Environment_AddsContextAndCliFirstOnPath()
    {
        var env = TerminalEnvironment.Build(new Hashtable { ["Path"] = @"C:\Windows", ["HOME"] = "h" }, @"C:\ctx.json", @"C:\FleetMate");
        Assert.Equal(@"C:\ctx.json", env[TerminalEnvironment.ContextVariable]);
        Assert.Equal(@"C:\FleetMate;C:\Windows", env["PATH"]);

        var again = TerminalEnvironment.Build(new Hashtable { ["PATH"] = @"C:\FleetMate\;C:\Windows" }, "c", @"C:\FleetMate");
        Assert.Equal(@"C:\FleetMate\;C:\Windows", again["PATH"]);
    }

    [Fact]
    public void EnvironmentBlock_IsSortedAndDoubleNulTerminated() =>
        Assert.Equal("A=1\0b=2\0\0", TerminalEnvironment.ToEnvironmentBlock(new Dictionary<string, string> { ["b"] = "2", ["A"] = "1" }));

    [Theory]
    [InlineData("terminal.html")]
    [InlineData("xterm.js")]
    [InlineData("xterm.css")]
    [InlineData("addon-fit.js")]
    public void TerminalAssets_AreEmbeddedInTheApp(string name)
    {
        using var stream = typeof(FleetMate.GUI.Views.Terminal.TerminalView).Assembly
            .GetManifestResourceStream($"FleetMate.GUI.Terminal.{name}");
        Assert.NotNull(stream);
    }
}
