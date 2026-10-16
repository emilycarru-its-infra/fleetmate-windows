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
    public void SessionMetadataIgnoresInvalidFieldsAndAcceptsPartialUpdates()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"title\":\"Task\",\"section\":\"Work\"}");
            Assert.Equal(new AgentSessionMetadata("Task", "Work"), AgentSessionMetadata.Read(path));
            File.WriteAllText(path, "{\"title\":\"\"}");
            Assert.Equal(new AgentSessionMetadata("", null), AgentSessionMetadata.Read(path));
            File.WriteAllText(path, "{\"section\":42}");
            Assert.Null(AgentSessionMetadata.Read(path)!.Section);
            File.WriteAllText(path, "{");
            Assert.Null(AgentSessionMetadata.Read(path));
        }
        finally { File.Delete(path); }
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
