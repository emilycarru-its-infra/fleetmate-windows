using FleetMate.Core.Services.Terminal;
using Xunit;

namespace FleetMate.Tests;

public class AgentWorkspaceTests
{
    [Fact]
    public void WorkspaceRefreshesGuideAndPreservesOperatorInstructions()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fleetmate-workspace-" + Guid.NewGuid().ToString("N"));
        try
        {
            var guide = AgentWorkspace.Prepare(folder, "brief.md", "context.json");
            Assert.Contains("fleetmate --help", File.ReadAllText(guide));
            Assert.Contains("FLEETMATE_CONTEXT", File.ReadAllText(guide));
            Assert.Contains("FLEETMATE.md", File.ReadAllText(Path.Combine(folder, "AGENTS.md")));
            File.WriteAllText(Path.Combine(folder, "AGENTS.md"), "My instructions");
            AgentWorkspace.Prepare(folder, "new-brief.md", "new-context.json");
            Assert.Equal("My instructions", File.ReadAllText(Path.Combine(folder, "AGENTS.md")));
            Assert.Contains("new-context.json", File.ReadAllText(guide));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void SplitViewExitsFullWindowAndUsesHalfTheAvailableHeight()
    {
        var layout = new TerminalLayoutState();
        layout.Toggle(800);
        layout.Split(800);
        Assert.False(layout.FullWindow);
        Assert.Equal(400, layout.DisplayHeight);
        layout.Toggle(800);
        layout.Toggle(800);
        Assert.Equal(400, layout.DisplayHeight);
    }
}
