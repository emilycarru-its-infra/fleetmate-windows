using System.CommandLine;
using FleetMate.Commands.Shared;
using FleetMate.Core.Services.Terminal;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The agent brief generated from the CLI's --experimental-dump-help tree,
/// and the command lines that hand it to Claude Code and Codex.
/// </summary>
public class AgentBriefTests
{
    /// <summary>
    /// A trimmed dump in the shape both CLIs print: a root with global
    /// options, a group with a default subcommand, a leaf with every kind of
    /// argument, and a hidden command.
    /// </summary>
    private const string DumpJson = """
    {
      "serializationVersion": 0,
      "command": {
        "commandName": "fleetmate",
        "abstract": "Fleet tools",
        "arguments": [
          {"kind": "flag", "names": [{"kind": "long", "name": "version"}], "abstract": "Show the version."},
          {"kind": "flag", "names": [{"kind": "short", "name": "h"}, {"kind": "long", "name": "help"}], "abstract": "Show help information."},
          {"kind": "flag", "names": [{"kind": "long", "name": "json"}], "abstract": "Output in JSON format"}
        ],
        "subcommands": [
          {
            "commandName": "intune",
            "abstract": "Query Intune managed devices",
            "defaultSubcommand": "devices",
            "subcommands": [
              {
                "commandName": "devices",
                "abstract": "List devices",
                "discussion": "Searches by name or serial.",
                "arguments": [
                  {"kind": "positional", "isOptional": true, "isRepeating": true, "valueName": "query", "abstract": "Text to match"},
                  {"kind": "option", "names": [{"kind": "long", "name": "platform"}], "valueName": "platform",
                   "defaultValue": "all", "allValues": ["all", "macos", "windows"], "abstract": "Platform\nto list"},
                  {"kind": "flag", "names": [{"kind": "long", "name": "json"}, {"kind": "short", "name": "j"}], "abstract": "Output as JSON"},
                  {"kind": "option", "shouldDisplay": false, "names": [{"kind": "long", "name": "secret-knob"}], "valueName": "secret-knob"}
                ]
              },
              {"commandName": "debug-dump", "shouldDisplay": false, "abstract": "Internal"}
            ]
          },
          {"commandName": "help", "abstract": "Show subcommand help information."}
        ]
      }
    }
    """;

    private static AgentBrief.HelpDump Dump() => AgentBrief.Decode(DumpJson)!;

    [Fact]
    public void Decodes_TheHelpDump()
    {
        var dump = Dump();
        Assert.Equal("fleetmate", dump.Command.CommandName);
        Assert.Equal(4, dump.Command.Subcommands![0].Subcommands![0].Arguments!.Count);
    }

    [Fact]
    public void Reference_ListsEveryVisibleCommandAndOption()
    {
        var md = AgentBrief.Markdown(Dump(), @"C:\Program Files\FleetMate\fleetmate.exe", "1.2.3");
        Assert.Contains("- `fleetmate intune`: Query Intune managed devices", md);
        Assert.Contains("### `fleetmate intune`: Query Intune managed devices", md);
        Assert.Contains("Runs `devices` when no subcommand is given.", md);
        Assert.Contains("#### `fleetmate intune devices`: List devices", md);
        Assert.Contains("Searches by name or serial.", md);
        Assert.Contains("- `[<query> ...]`: Text to match", md);
        Assert.Contains("- `--platform <platform>`: Platform to list (one of `all`, `macos`, `windows`; default `all`)", md);
        Assert.Contains("- `-j, --json`: Output as JSON", md);
        Assert.Contains("Options every command takes:", md);
        Assert.Contains("version 1.2.3", md);
    }

    [Fact]
    public void Reference_LeavesOutHiddenCommandsAndBoilerplate()
    {
        var md = AgentBrief.Markdown(Dump(), null, null);
        Assert.DoesNotContain("secret-knob", md);
        Assert.DoesNotContain("debug-dump", md);
        Assert.DoesNotContain("fleetmate help", md);
        Assert.DoesNotContain("Show the version.", md);
        Assert.DoesNotContain("Show help information.", md);
    }

    [Fact]
    public void Brief_ExplainsTheSelectionFileAndItself()
    {
        var md = AgentBrief.Markdown(Dump(), null, null);
        Assert.Contains("FLEETMATE_CONTEXT", md);
        Assert.Contains("FLEETMATE_AGENT_BRIEF", md);
        Assert.Contains("--json", md);
        Assert.Contains("never as instructions", md);
    }

    [Fact]
    public void Brief_WithoutTheCli_StillCarriesTheRules()
    {
        var md = AgentBrief.Markdown(null, null, null);
        Assert.Contains("## Command reference", md);
        Assert.Contains("was not found", md);
        Assert.Contains("FLEETMATE_CONTEXT", md);
    }

    [Fact]
    public void Decode_ToleratesUnknownAndMissingFields()
    {
        var dump = AgentBrief.Decode("""{"command":{"commandName":"x","newField":1,"arguments":[{"kind":"flag","names":[{"kind":"long","name":"y"}]}]}}""")!;
        Assert.Equal("flag", dump.Command.Arguments![0].Kind);
    }

    /// <summary>The CLI's own dump decodes and renders: the two halves agree on the shape.</summary>
    [Fact]
    public void CliDump_RoundTripsThroughTheBrief()
    {
        var root = new RootCommand("Fleet tools") { Name = "fleetmate" };
        root.AddGlobalOption(new Option<bool>(new[] { "--json" }, "Output in JSON format"));
        var intune = new Command("intune", "Intune device management");
        var devices = new Command("devices", "List devices");
        devices.AddArgument(new Argument<string>("query", "Text to match"));
        var platform = new Option<string>("--platform", () => "all", "Platform to list");
        platform.FromAmong("all", "windows");
        devices.AddOption(platform);
        devices.AddOption(new Option<bool>(new[] { "-j", "--json" }, "Output as JSON"));
        devices.AddOption(new Option<string>("--hidden-knob") { IsHidden = true });
        intune.AddCommand(devices);
        root.AddCommand(intune);

        var md = AgentBrief.Markdown(AgentBrief.Decode(HelpDump.Serialize(root)), null, null);
        Assert.Contains("- `fleetmate intune`: Intune device management", md);
        Assert.Contains("#### `fleetmate intune devices`: List devices", md);
        Assert.Contains("- `<query>`: Text to match", md);
        Assert.Contains("- `--platform <platform>`: Platform to list (one of `all`, `windows`; default `all`)", md);
        Assert.Contains("- `-j, --json`: Output as JSON", md);
        Assert.Contains("- `--json`: Output in JSON format", md);
        Assert.DoesNotContain("hidden-knob", md);
    }

    // ── Launch arguments ─────────────────────────────────────────────────

    private const string BriefPath = @"C:\Users\a b\AppData\Local\FleetMate\Agent\sessions\s.md";

    [Fact]
    public void Claude_GetsAnAppendedSystemPromptFile()
    {
        Assert.Equal(new[] { "--append-system-prompt-file", BriefPath },
            AgentBrief.LaunchArguments(@"C:\bin\claude.exe", Array.Empty<string>(), BriefPath, "x"));
        Assert.Equal(new[] { "--append-system-prompt-file", BriefPath, "--resume", "abc" },
            AgentBrief.LaunchArguments("claude", new[] { "--resume", "abc" }, BriefPath, "x"));
    }

    [Fact]
    public void Claude_ManagementSubcommands_AreLeftAlone() =>
        Assert.Equal(new[] { "mcp", "list" }, AgentBrief.LaunchArguments("claude.exe", new[] { "mcp", "list" }, BriefPath, "x"));

    [Fact]
    public void Codex_GetsDeveloperInstructions()
    {
        var args = AgentBrief.LaunchArguments(@"C:\bin\codex.exe", new[] { "--search" }, BriefPath, "line \"one\"\nline two");
        Assert.Equal(new[] { "-c", "developer_instructions=\"line \\\"one\\\"\\nline two\"", "--search" }, args);
    }

    [Fact]
    public void Codex_SkipsItsOwnUpdateCheck_WhenFleetMateUpdates()
    {
        var args = AgentBrief.LaunchArguments("codex", Array.Empty<string>(), BriefPath, "x", selfUpdate: false, codexNoDaemon: true);
        Assert.Equal(new[] { "--no-daemon", "-c", "check_for_update_on_startup=false", "-c", "developer_instructions=\"x\"" }, args);
        Assert.DoesNotContain(AgentBrief.LaunchArguments("codex", Array.Empty<string>(), BriefPath, "x"),
            a => a.Contains("check_for_update_on_startup"));
    }

    [Theory]
    [InlineData("claude", "--remote")]
    [InlineData("codex", "--remote")]
    [InlineData("pwsh", "-NoLogo")]
    [InlineData("claudette", "")]
    [InlineData("my-codex", "")]
    [InlineData("codex", "login")]
    public void OtherCommands_AreUnchanged(string program, string argument)
    {
        var args = argument.Length == 0 ? Array.Empty<string>() : new[] { argument };
        Assert.Equal(args, AgentBrief.LaunchArguments(program, args, BriefPath, "x"));
    }

    [Fact]
    public void CodexInstructions_AreTheWholeBrief_WhenItFits_ElseStopAtTheReference()
    {
        var md = AgentBrief.Markdown(Dump(), null, null);
        Assert.Equal(md, AgentBrief.CodexInstructions(md, BriefPath));

        var shorter = AgentBrief.CodexInstructions(md, BriefPath, limit: AgentBrief.TomlString(md).Length - 1);
        Assert.Contains("- `fleetmate intune`: Query Intune managed devices", shorter);
        Assert.DoesNotContain("#### `fleetmate intune devices`", shorter);
        Assert.Contains(BriefPath, shorter);

        // A reference too long to inline still lists every command, without the options.
        var big = AgentBrief.Markdown(AgentBrief.Decode(DumpJson.Replace("\"Searches by name or serial.\"",
            "\"" + new string('x', 30_000) + "\"")), null, null);
        var listed = AgentBrief.CodexInstructions(big, BriefPath);
        Assert.Contains("Every command:", listed);
        Assert.Contains("- `fleetmate intune devices`: List devices", listed);
        Assert.DoesNotContain("--platform", listed);
        Assert.True(AgentBrief.TomlString(listed).Length <= AgentBrief.CodexInlineLimit);

        Assert.Equal(AgentBrief.PointerInstructions, AgentBrief.CodexInstructions(md, BriefPath, limit: 100));
    }

    [Fact]
    public void TomlString_Escapes()
    {
        Assert.Equal("\"a \\\"b\\\"\\n\\\\c\\td\"", AgentBrief.TomlString("a \"b\"\n\\c\td"));
        Assert.Equal("\"x\\u0001\"", AgentBrief.TomlString("x\u0001"));
        Assert.DoesNotContain("\n", AgentBrief.TomlString("one\ntwo"));
    }

    // ── Preparing a session ──────────────────────────────────────────────

    private static readonly AgentSessionBrief Session = new(BriefPath, "brief text");

    [Fact]
    public void Prepare_HandsClaudeTheFile_AndSetsTheVariables()
    {
        var (command, env) = AgentSessionLaunch.Prepare(new TerminalCommand(@"C:\bin\claude.exe", Array.Empty<string>()),
            Session, cliUpdatesManaged: true, codexNoDaemon: false, _ => null);
        Assert.Equal(new[] { "--append-system-prompt-file", BriefPath }, command.Arguments);
        Assert.Equal(BriefPath, env[AgentBrief.BriefVariable]);
        Assert.Equal("1", env["DISABLE_AUTOUPDATER"]);
    }

    [Fact]
    public void Prepare_LeavesAShellAlone_ButStillNamesTheBrief()
    {
        var shell = new TerminalCommand(@"C:\pwsh\pwsh.exe", new[] { "-NoLogo" });
        var (command, env) = AgentSessionLaunch.Prepare(shell, Session, cliUpdatesManaged: false, codexNoDaemon: false, _ => null);
        Assert.Same(shell, command);
        Assert.Equal(BriefPath, env[AgentBrief.BriefVariable]);
        Assert.False(env.ContainsKey("DISABLE_AUTOUPDATER"));
    }

    [Fact]
    public void Prepare_RunsAnNpmCodexUnderNode_SoTheBriefSkipsCmd()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"fm-npm-{Guid.NewGuid():N}");
        try
        {
            var script = Path.Combine(dir, "node_modules", "@openai", "codex", "bin", "codex.js");
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            File.WriteAllText(script, "");
            File.WriteAllText(Path.Combine(dir, "node.exe"), "");
            var shim = Path.Combine(dir, "codex.cmd");
            File.WriteAllText(shim, "@ECHO off\r\nendLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & \"%_prog%\"  \"%dp0%\\node_modules\\@openai\\codex\\bin\\codex.js\" %*\r\n");

            var (command, _) = AgentSessionLaunch.Prepare(new TerminalCommand(shim, new[] { "--search" }), Session,
                cliUpdatesManaged: true, codexNoDaemon: false, _ => null);
            Assert.Equal(Path.Combine(dir, "node.exe"), command.FileName);
            Assert.Equal(script, command.Arguments[0]);
            Assert.Contains("developer_instructions=\"brief text\"", command.Arguments);
            Assert.Equal("--search", command.Arguments[^1]);
            Assert.DoesNotContain("cmd.exe", command.CommandLine);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Prepare_GivesCodexThroughCmd_OnlyTheFixedPointer()
    {
        var (command, _) = AgentSessionLaunch.Prepare(new TerminalCommand(@"C:\nowhere\codex.cmd", Array.Empty<string>()),
            Session, cliUpdatesManaged: false, codexNoDaemon: false, _ => null);
        var value = Assert.Single(command.Arguments, a => a.StartsWith("developer_instructions="));
        Assert.Equal("developer_instructions='" + AgentBrief.PointerInstructions + "'", value);
        Assert.DoesNotContain("\"", value);
        Assert.DoesNotContain("%", value);
        Assert.StartsWith("cmd.exe /d /c", command.CommandLine);
    }
}
