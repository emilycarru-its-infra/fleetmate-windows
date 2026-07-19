using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using FleetMate.Commands.Devices;
using FleetMate.Commands.Identity;
using FleetMate.Commands.Inventory;
using FleetMate.Commands.Projects;
using FleetMate.Commands.Reporting;
using FleetMate.Commands.Shared;
using FleetMate.Commands.Tickets;
using FleetMate.Core.Config;
using FleetMate.Core.Services.Devices;
using FleetMate.Core.Services.Reporting;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Verifies the CLI command tree is wired correctly: every top-level command
/// factory constructs, the assembled root exposes exactly the expected command
/// set, and representative argument/subcommand parsing succeeds. These run with
/// throwaway (non-connecting) services and no credentials.
/// </summary>
public class CommandTreeTests
{
    /// <summary>
    /// Builds the root command exactly as Program.cs does, using inert services
    /// that never open a connection at construction time.
    /// </summary>
    private static RootCommand BuildRoot()
    {
        var config = new FleetMateConfig();
        var reportMate = new ReportMateService("https://example.invalid");
        var pkgInfo = new PkgInfoService(config);
        var cimianService = new CimianService(config);

        var root = new RootCommand("FleetMate") { Name = "fleetmate" };

        root.AddCommand(ErrorsCommand.Create(reportMate, pkgInfo));
        root.AddCommand(TroubleshootCommand.Create(reportMate, pkgInfo, config));
        root.AddCommand(DeviceCommand.Create(reportMate));
        root.AddCommand(TestCommand.Create(config));
        root.AddCommand(LintCommand.Create(config, pkgInfo));
        root.AddCommand(ValidateCommand.Create(config, pkgInfo));
        root.AddCommand(QaCommand.Create(config));
        root.AddCommand(StatusCommand.Create(config, reportMate));
        root.AddCommand(ConfigureCommand.Create(config));
        root.AddCommand(SnipeCommand.Create(null));
        root.AddCommand(SshCommand.Create(null, reportMate));
        root.AddCommand(DeadlineCommand.Create(null));
        root.AddCommand(DevOpsCommand.Create(null, reportMate));
        root.AddCommand(CimianCommand.Create(null, null, cimianService));
        root.AddCommand(IntuneCommand.Create(null, reportMate));
        root.AddCommand(EntraCommand.Create(null, reportMate));
        root.AddCommand(TdxCommand.Create(null, reportMate));
        root.AddCommand(TasksCommand.Create(config));
        root.AddCommand(ProjectsCommand.Create(config));
        root.AddCommand(ReportMateCommand.Create(reportMate));

        return root;
    }

    private static readonly string[] ExpectedCommands =
    {
        "errors", "troubleshoot", "device", "test", "lint", "validate", "qa",
        "status", "configure", "snipe", "ssh", "deadline", "devops", "cimian",
        "intune", "entra", "tdx", "tasks", "projects", "reportmate",
    };

    [Fact]
    public void Root_ExposesExactlyTheExpectedCommandSet()
    {
        var root = BuildRoot();
        var names = root.Subcommands.Select(c => c.Name).OrderBy(n => n).ToArray();

        Assert.Equal(ExpectedCommands.OrderBy(n => n).ToArray(), names);
    }

    [Fact]
    public void Root_HasTwentyCommands()
    {
        Assert.Equal(20, BuildRoot().Subcommands.Count);
    }

    [Theory]
    [InlineData("errors")]
    [InlineData("intune")]
    [InlineData("entra")]
    [InlineData("cimian")]
    [InlineData("tdx")]
    [InlineData("snipe")]
    [InlineData("devops")]
    [InlineData("projects")]
    public void Command_ResolvesHelpThroughInvocationPipeline(string command)
    {
        // --help is wired by the invocation pipeline (UseDefaults), not by a bare
        // Command.Parse, so build the parser the way the real CLI runs.
        var parser = new CommandLineBuilder(BuildRoot()).UseDefaults().Build();

        var result = parser.Parse($"{command} --help");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void QaCommand_ParsesPositionalPackageArgument()
    {
        var root = BuildRoot();

        var result = root.Parse("qa ZEDSDK");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void IntuneSubcommand_ParsesWithoutErrors()
    {
        var root = BuildRoot();

        var result = root.Parse("intune devices --limit 10");

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void UnknownCommand_ProducesParseError()
    {
        var root = BuildRoot();

        var result = root.Parse("definitely-not-a-command");

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void EveryFactory_ProducesANamedCommand()
    {
        foreach (var command in BuildRoot().Subcommands)
        {
            Assert.False(string.IsNullOrWhiteSpace(command.Name));
            Assert.False(string.IsNullOrWhiteSpace(command.Description));
        }
    }
}
