using FleetMate.Core.Config;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The setup wizard's plan, shared by the WPF and WinUI wizards.</summary>
public class SetupWizardPlanTests
{
    private sealed class MemorySink : ISettingsSink
    {
        public Dictionary<string, string> Values { get; } = new();
        public void Set(string name, string value) => Values[name] = value;
        public void SetList(string name, IReadOnlyList<string> values) { }
        public void Delete(string name) => Values.Remove(name);
    }

    [Fact]
    public void OpensByItselfOnlyWhenNothingIsSetUp()
    {
        Assert.True(SetupWizardPlan.NeededAtLaunch(new FleetMateConfig(), setupCompleted: false));
        Assert.False(SetupWizardPlan.NeededAtLaunch(new FleetMateConfig(), setupCompleted: true));
        Assert.False(SetupWizardPlan.NeededAtLaunch(new FleetMateConfig { SnipeUrl = "https://assets.example.invalid" }, setupCompleted: false));
    }

    [Fact]
    public void TicketsMateSkipsModulesAndSignIn()
    {
        Assert.Equal(new[] { "Welcome", "Connections", "Summary" },
            SetupWizardPlan.Steps(AppEdition.TicketsMate, new HashSet<string>(), includeRepositories: true));
    }

    [Fact]
    public void TheSignInStepFollowsTheChosenModules()
    {
        var all = new HashSet<string>();
        Assert.Equal(new[] { "Welcome", "Modules", "Connections", "Development", "Repositories", "Summary" },
            SetupWizardPlan.Steps(AppEdition.FleetMate, all, includeRepositories: true));
        var noDevelopment = new HashSet<string> { "Development" };
        Assert.Equal("Sign In", SetupWizardPlan.SignInStep(noDevelopment));
        Assert.DoesNotContain("Repositories", SetupWizardPlan.Steps(AppEdition.FleetMate, noDevelopment, includeRepositories: true));
        var ticketsOnly = AppModules.All.Select(m => m.Tag).Where(t => t != "Tickets").ToHashSet();
        Assert.Null(SetupWizardPlan.SignInStep(ticketsOnly));
    }

    [Fact]
    public void ConnectionsAskOnlyForTheChosenModules()
    {
        var ticketsOnly = AppModules.All.Select(m => m.Tag).Where(t => t != "Tickets").ToHashSet();
        var section = Assert.Single(SetupWizardPlan.ConnectionSections(ticketsOnly));
        Assert.Equal(new[] { "TdxBaseUrl", "TdxAppId" }, section.Fields.Select(f => f.Key));
    }

    [Fact]
    public void ABlankEndpointLeavesWhatIsThere()
    {
        var sink = new MemorySink();
        SetupWizardPlan.SaveEndpoints(sink, new Dictionary<string, string> { ["SnipeUrl"] = " https://assets.example.invalid ", ["TdxBaseUrl"] = "" });
        Assert.Equal("https://assets.example.invalid", sink.Values["SnipeUrl"]);
        Assert.False(sink.Values.ContainsKey("TdxBaseUrl"));
    }
}
