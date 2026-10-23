using FleetMate.Core.Services.Terminal;
using Xunit;

namespace FleetMate.Tests;

public class TerminalWorkspacesTests
{
    [Fact]
    public void Load_ReadsAHandWrittenFileWithOptionalFieldsLeftOut()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, """
            {"openAtLaunch": "daily", "workspaces": [
              {"name": "Daily", "sections": [
                {"name": "Repos", "sessions": [{"command": "codex", "directory": "~/src/app"}, {}]},
                {"name": "Empty"}
              ]}
            ]}
            """);
            var file = TerminalWorkspaceStore.Load(path);
            Assert.Equal("Daily", file.LaunchWorkspace?.Name);
            Assert.Equal(2, file.Workspaces[0].SessionCount);
            Assert.Empty(file.Workspaces[0].Sections[1].Sessions);
            Assert.Null(file.Workspaces[0].Show);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_MissingFileIsEmpty()
    {
        var file = TerminalWorkspaceStore.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        Assert.Empty(file.Workspaces);
        Assert.Null(file.OpenAtLaunch);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_WithTheMacFieldNames()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "terminal-workspaces.json");
        try
        {
            var file = new TerminalWorkspaceFile { OpenAtLaunch = "W" };
            file.Upsert(new TerminalWorkspace
            {
                Name = "W", Show = true,
                Sections = { new TerminalWorkspaceSection { Name = "Sessions", Collapsed = true,
                    Sessions = { new TerminalWorkspaceSession { Name = "x", Command = "claude", Directory = "~/a" } } } },
            });
            TerminalWorkspaceStore.Save(file, path);
            var text = File.ReadAllText(path);
            Assert.Contains("\"openAtLaunch\"", text);
            Assert.DoesNotContain("fullWindow", text);
            var loaded = TerminalWorkspaceStore.Load(path);
            Assert.Equal("W", loaded.LaunchWorkspace?.Name);
            Assert.Equal("claude", loaded.Workspaces[0].Sections[0].Sessions[0].Command);
            Assert.True(loaded.Workspaces[0].Sections[0].Collapsed);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    [Fact]
    public void Upsert_ReplacesByName_AndRemoveClearsLaunch()
    {
        var file = new TerminalWorkspaceFile { OpenAtLaunch = "A" };
        file.Upsert(new TerminalWorkspace { Name = "A" });
        file.Upsert(new TerminalWorkspace { Name = "a", FullWindow = true });
        Assert.Single(file.Workspaces);
        Assert.True(file.Workspaces[0].FullWindow);
        file.Upsert(new TerminalWorkspace { Name = "B" });
        file.Remove("A");
        Assert.Null(file.OpenAtLaunch);
        Assert.Equal(new[] { "B" }, file.Workspaces.Select(w => w.Name));
    }

    [Fact]
    public void Home_IsWrittenAsTilde()
    {
        Assert.Equal(@"~\src", TerminalWorkspaceStore.Abbreviate(@"C:\Users\a\src", @"C:\Users\a"));
        Assert.Equal("~", TerminalWorkspaceStore.Abbreviate(@"C:\Users\a", @"C:\Users\a"));
        Assert.Equal(@"C:\Users\ab\src", TerminalWorkspaceStore.Abbreviate(@"C:\Users\ab\src", @"C:\Users\a"));
        Assert.Equal(@"C:\Users\a\src", TerminalWorkspaceStore.Expand(@"~\src", @"C:\Users\a"));
        Assert.Equal(@"C:\Users\a/src", TerminalWorkspaceStore.Expand("~/src", @"C:\Users\a"));
        Assert.Equal(@"D:\x", TerminalWorkspaceStore.Expand(@"D:\x", @"C:\Users\a"));
    }
}
