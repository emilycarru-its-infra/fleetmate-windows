using FleetMate.Core.Models.Manage;
using FleetMate.Core.Services.Manage;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The YAML command library round-trips through YamlDotNet in the ScanLab
/// shape, merges bundled additions without touching user edits, and the
/// audit catches the mistakes a hand-edited library accumulates.
/// </summary>
public class CommandLibraryTests
{
    private const string Sample = """
        # fleet command library
        categories:
          - name: System
            commands:
              - label: Hostname
                command: hostname
                trust: safe
              - label: 'Disk usage (C:)'
                command: 'Get-PSDrive C | Select-Object @{n=''FreeGB'';e={[math]::Round($_.Free/1GB,1)}}'
                trust: safe
              - label: Restart now
                command: shutdown /r /t 0
          - name: Users & Sessions
            commands:
              - label: Create local admin (template)
                command: "New-LocalUser -Name '<USERNAME>' -Password (ConvertTo-SecureString '<PASSWORD>' -AsPlainText -Force)"
                trust: caution
        """;

    [Fact]
    public void Parse_ReadsCategoriesLabelsCommandsAndTrust()
    {
        var categories = CommandLibrary.Parse(Sample);

        Assert.Equal(new[] { "System", "Users & Sessions" }, categories.Select(c => c.Name).ToArray());
        var system = categories[0];
        Assert.Equal(3, system.Commands.Count);
        Assert.Equal("Get-PSDrive C | Select-Object @{n='FreeGB';e={[math]::Round($_.Free/1GB,1)}}", system.Commands[1].Command);
        Assert.Equal(CommandTrustLevel.Safe, system.Commands[1].TrustLevel);
        Assert.True(system.Commands[1].TrustWasExplicit);
    }

    [Fact]
    public void Parse_InfersTrustWhenNotStated()
    {
        var categories = CommandLibrary.Parse(Sample);
        var restart = categories[0].Commands[2];

        Assert.False(restart.TrustWasExplicit);
        Assert.Equal(CommandTrustLevel.Destructive, restart.TrustLevel);
    }

    [Fact]
    public void Parse_UnknownTrustFallsBackToInference()
    {
        var yaml = "categories:\n  - name: X\n    commands:\n      - label: A\n        command: shutdown /s\n        trust: sometimes\n";
        var cmd = CommandLibrary.Parse(yaml)[0].Commands[0];
        Assert.False(cmd.TrustWasExplicit);
        Assert.Equal(CommandTrustLevel.Destructive, cmd.TrustLevel);
    }

    [Fact]
    public void Parse_SkipsEntriesWithoutLabelOrCommand()
    {
        var yaml = "categories:\n  - name: X\n    commands:\n      - label: NoCommand\n      - command: no-label\n      - label: Ok\n        command: hostname\n";
        var cmds = CommandLibrary.Parse(yaml)[0].Commands;
        Assert.Single(cmds);
        Assert.Equal("Ok", cmds[0].Label);
    }

    [Fact]
    public void Parse_EmptyOrForeignYamlIsEmpty()
    {
        Assert.Empty(CommandLibrary.Parse(""));
        Assert.Empty(CommandLibrary.Parse("other: thing\n"));
    }

    [Fact]
    public void Serialize_RoundTripsIncludingQuotesAndMultiline()
    {
        var original = new List<CommandCategory>
        {
            new("Diagnostics", new[]
            {
                new ManagedCommand("Quote: it's \"here\"", "Write-Output 'it''s' # not a comment", CommandTrustLevel.Safe),
                new ManagedCommand("Multi-line", "$a = 1\n$b = 2\nWrite-Output ($a + $b)", CommandTrustLevel.Caution),
                new ManagedCommand("Numeric label", "123", CommandTrustLevel.Safe),
            })
        };

        var yaml = CommandLibrary.Serialize(original);
        var parsed = CommandLibrary.Parse(yaml);

        Assert.Single(parsed);
        Assert.Equal(original[0].Name, parsed[0].Name);
        for (var i = 0; i < original[0].Commands.Count; i++)
        {
            Assert.Equal(original[0].Commands[i].Label, parsed[0].Commands[i].Label);
            Assert.Equal(original[0].Commands[i].Command, parsed[0].Commands[i].Command);
            Assert.Equal(original[0].Commands[i].TrustLevel, parsed[0].Commands[i].TrustLevel);
            Assert.True(parsed[0].Commands[i].TrustWasExplicit);
        }
        Assert.Contains("command: |-", yaml);
    }

    [Fact]
    public void Serialize_ProducesTheScanLabShape()
    {
        var yaml = CommandLibrary.Serialize(new[] { new CommandCategory("System", new[] { new ManagedCommand("Hostname", "hostname") }) });
        Assert.Equal("categories:\n  - name: System\n    commands:\n      - label: Hostname\n        command: hostname\n        trust: safe\n", yaml);
    }

    [Fact]
    public void MergeMissing_AddsOnlyWhatIsAbsent()
    {
        var user = CommandLibrary.Parse(Sample);
        user[0].Commands[0].Command = "hostname.exe";   // a user edit that must survive

        var bundled = new List<CommandCategory>
        {
            new("system", new[] { new ManagedCommand("HOSTNAME", "hostname"), new ManagedCommand("Uptime", "uptime") }),
            new("Network", new[] { new ManagedCommand("IP", "ipconfig") })
        };

        Assert.True(CommandLibrary.MergeMissing(user, bundled));
        Assert.Equal("hostname.exe", user[0].Commands[0].Command);
        Assert.Contains(user[0].Commands, c => c.Label == "Uptime");
        Assert.Equal(4, user[0].Commands.Count);
        Assert.Contains(user, c => c.Name == "Network");

        Assert.False(CommandLibrary.MergeMissing(user, bundled));
    }

    [Fact]
    public void Audit_FlagsDuplicatesEmptyAndUnderstatedTrust()
    {
        var categories = new List<CommandCategory>
        {
            new("A", new[]
            {
                new ManagedCommand("Same", "hostname"),
                new ManagedCommand("same", "hostname"),
                new ManagedCommand("Empty", ""),
                new ManagedCommand("Understated", "Remove-Item -Recurse C:\\Temp\\x", CommandTrustLevel.Safe),
                new ManagedCommand("Inferred", "hostname", CommandTrustLevel.Safe, trustWasExplicit: false),
            }),
            new("a"),
        };

        var issues = CommandLibrary.Audit(categories);

        Assert.Contains(issues, i => i.Severity == CommandAuditSeverity.Error && i.Message.Contains("duplicate label"));
        Assert.Contains(issues, i => i.Severity == CommandAuditSeverity.Error && i.Message.Contains("empty command"));
        Assert.Contains(issues, i => i.Severity == CommandAuditSeverity.Error && i.Message.Contains("duplicate category"));
        Assert.Contains(issues, i => i.Label == "Understated" && i.Message.Contains("destructive"));
        Assert.Contains(issues, i => i.Label == "Inferred" && i.Message.Contains("not stated"));
        Assert.Contains(issues, i => i.Category == "a" && i.Message.Contains("no commands"));
    }

    [Fact]
    public void Audit_CleanLibraryHasNoIssues()
    {
        var issues = CommandLibrary.Audit(CommandLibrary.DefaultCategories());
        Assert.Empty(issues);
    }

    [Fact]
    public void SaveAndLoad_RoundTripThroughDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fleetmate-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "sub", "commands.yaml");
        try
        {
            var categories = CommandLibrary.Parse(Sample);
            CommandLibrary.Save(categories, path);
            var loaded = CommandLibrary.Load(path);
            Assert.Equal(categories.Count, loaded.Count);
            Assert.Equal(categories[1].Commands[0].Command, loaded[1].Commands[0].Command);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_MissingFileGivesDefaults()
    {
        var loaded = CommandLibrary.Load(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".yaml"));
        Assert.NotEmpty(loaded);
        Assert.Equal("System", loaded[0].Name);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("has: colon", "'has: colon'")]
    [InlineData("it's", "'it''s'")]
    [InlineData("", "''")]
    [InlineData("true", "'true'")]
    [InlineData("42", "'42'")]
    [InlineData("- dash", "'- dash'")]
    public void Quote_ProtectsYamlSpecialValues(string input, string expected)
    {
        Assert.Equal(expected, CommandLibrary.Quote(input));
    }
}
