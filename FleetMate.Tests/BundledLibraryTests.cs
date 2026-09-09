using FleetMate.Core.Models.Manage;
using FleetMate.Core.Services.Manage;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The library that ships inside FleetMate must parse, audit clean, fit the
/// remote command line, and cover the categories the macOS library has.
/// </summary>
public class BundledLibraryTests
{
    private static readonly string[] ExpectedCategories =
    {
        "System", "Storage", "Cimian Config", "Cimian Operations", "Windows Update", "MDM & Enrollment",
        "Security & Profiles", "Users & Sessions", "Network", "Printing", "Diagnostics", "Logs", "Power", "Adobe", "StartSet"
    };

    [Fact]
    public void Bundled_ParsesWithEveryCategory()
    {
        var categories = CommandLibrary.LoadBundled();
        Assert.Equal(ExpectedCategories, categories.Select(c => c.Name).ToArray());
        Assert.True(categories.Sum(c => c.Commands.Count) >= 150, $"only {categories.Sum(c => c.Commands.Count)} commands");
        Assert.All(categories, c => Assert.NotEmpty(c.Commands));
    }

    [Fact]
    public void Bundled_AuditIsClean()
    {
        var issues = CommandLibrary.Audit(CommandLibrary.LoadBundled());
        Assert.Empty(issues.Where(i => i.Severity == CommandAuditSeverity.Error));
        Assert.Empty(issues.Where(i => i.Severity == CommandAuditSeverity.Warning));
    }

    [Fact]
    public void Bundled_EveryTrustIsExplicit()
    {
        Assert.All(CommandLibrary.LoadBundled().SelectMany(c => c.Commands), c => Assert.True(c.TrustWasExplicit, c.Label));
    }

    [Fact]
    public void Bundled_EveryCommandFitsTheRemoteCommandLine()
    {
        foreach (var command in CommandLibrary.LoadBundled().SelectMany(c => c.Commands))
            Assert.True(RemoteScriptEncoder.Fits(command.Command), $"{command.Label} is too long");
    }

    [Fact]
    public void Bundled_TrustIsNeverWeakerThanInference()
    {
        foreach (var command in CommandLibrary.LoadBundled().SelectMany(c => c.Commands))
            Assert.True(command.TrustLevel >= TrustInference.Infer(command.Command), $"{command.Label}: {command.TrustLevel} < inferred {TrustInference.Infer(command.Command)}");
    }

    [Fact]
    public void Bundled_PlaceholdersUseTheAngleBracketConvention()
    {
        var templates = CommandLibrary.LoadBundled().SelectMany(c => c.Commands)
            .Select(c => PlaceholderTemplate.Detect(c.Label, c.Command)).Where(t => t != null).ToList();
        Assert.NotEmpty(templates);
        Assert.Contains(templates, t => t!.Placeholders.Contains("<USERNAME>"));
        Assert.Contains(templates, t => t!.HasSensitive);
    }

    [Fact]
    public void Bundled_RoundTripsThroughSerializer()
    {
        var bundled = CommandLibrary.LoadBundled();
        var again = CommandLibrary.Parse(CommandLibrary.Serialize(bundled));
        Assert.Equal(bundled.Count, again.Count);
        for (var i = 0; i < bundled.Count; i++)
        {
            Assert.Equal(bundled[i].Name, again[i].Name);
            Assert.Equal(bundled[i].Commands.Select(c => c.Command), again[i].Commands.Select(c => c.Command));
            Assert.Equal(bundled[i].Commands.Select(c => c.TrustLevel), again[i].Commands.Select(c => c.TrustLevel));
        }
    }

    [Fact]
    public void Bundled_ContainsNoEnvironmentSpecificNames()
    {
        // Public repository: the library must read as a generic Windows fleet library.
        var text = CommandLibrary.BundledYaml();
        foreach (var forbidden in new[] { "ecuad", "emilycarr", "10.15.", "10.16.", "ANIM-", "winadmins" })
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
    }
}
