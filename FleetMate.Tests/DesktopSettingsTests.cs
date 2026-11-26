using FleetMate.Core.Config;
using Xunit;

namespace FleetMate.Tests;

/// <summary>What Settings writes, shared by the WPF and WinUI pages. Synthetic values only.</summary>
public class DesktopSettingsTests
{
    private sealed class MemorySink : ISettingsSink
    {
        public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Deleted { get; } = new(StringComparer.OrdinalIgnoreCase);
        public void Set(string name, string value) => Values[name] = value;
        public void SetList(string name, IReadOnlyList<string> values) => Values[name] = values.ToArray();
        public void Delete(string name) { Values.Remove(name); Deleted.Add(name); }
    }

    [Fact]
    public void RoundTripsTheConfiguredConnections()
    {
        var config = new FleetMateConfig
        {
            SnipeUrl = "https://assets.example.invalid",
            Tdx = new FleetMate.Core.Models.Tickets.TdxConfig { BaseUrl = "https://service.example.invalid", AppId = 12 },
        };
        var settings = DesktopSettings.From(config);
        Assert.Equal("https://assets.example.invalid", settings.SnipeUrl);
        Assert.Equal("12", settings.TdxAppId);

        var sink = new MemorySink();
        settings.Save(sink, config.Terminal);
        Assert.Equal("https://service.example.invalid", sink.Values["TdxBaseUrl"]);
        Assert.Equal("12", sink.Values["TdxAppId"]);
    }

    [Fact]
    public void NeverWritesASecretAndRemovesOldOnes()
    {
        var sink = new MemorySink();
        new DesktopSettings().Save(sink, new TerminalSettings());
        foreach (var secret in DesktopSettings.RetiredSecrets)
        {
            Assert.Contains(secret, sink.Deleted);
            Assert.False(sink.Values.ContainsKey(secret));
        }
    }

    [Fact]
    public void EmptyOptionalFieldsAreRemovedSoManagedValuesApply()
    {
        var sink = new MemorySink();
        new DesktopSettings { HandbookRepoUrl = "  ", ManageRosterPath = "" }.Save(sink, new TerminalSettings());
        Assert.Contains("HandbookRepoUrl", sink.Deleted);
        Assert.Contains("ManageRosterPath", sink.Deleted);
        // An empty connection keeps what is there rather than clearing it.
        Assert.DoesNotContain("TdxBaseUrl", sink.Deleted);
        Assert.False(sink.Values.ContainsKey("TdxBaseUrl"));
    }

    [Fact]
    public void TerminalChoicesMatchingTheManagedDefaultAreNotStored()
    {
        var terminal = new TerminalSettings { RepoDefaults = new() { "org/repo-a" } };
        var sink = new MemorySink();
        new DesktopSettings
        {
            AgentCommand = terminal.AgentCommandFallback,
            AgentAutoStart = terminal.AgentAutoStartFallback,
            Repos = new[] { "org/repo-a" },
        }.Save(sink, terminal);
        Assert.Contains("AgentCommand", sink.Deleted);
        Assert.Contains("AgentAutoStart", sink.Deleted);
        Assert.Contains("Repos", sink.Deleted);

        var own = new MemorySink();
        new DesktopSettings { AgentCommand = "custom-agent", Repos = new[] { "org/repo-b" } }.Save(own, terminal);
        Assert.Equal("custom-agent", own.Values["AgentCommand"]);
        Assert.Equal(new[] { "org/repo-b" }, own.Values["Repos"]);
    }
}
