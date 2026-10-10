using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Shared;
using FleetMate.GUI.Views.Shared;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Rows dragged onto the agent terminal and the detail views' Agent button:
/// the terminal takes only the block of a drag FleetMate started, once, and
/// a row's element resolves to the block the menu and the drag share.
/// </summary>
public class AgentContextDragTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // ── What a drop on the terminal may hand over ────────────────────────

    [Fact]
    public void Claim_TakesTheDragsOwnBlockOnce()
    {
        AgentContextDrag.Begin("### Work item: Fix\n- ID: 5\n");
        AgentContextDrag.End(Now);
        Assert.Equal("### Work item: Fix\n- ID: 5\n", AgentContextDrag.Claim("### Work item: Fix\r\n- ID: 5\r\n", Now.AddSeconds(1)));
        Assert.Null(AgentContextDrag.Claim("### Work item: Fix\n- ID: 5\n", Now.AddSeconds(1)));
    }

    [Fact]
    public void Claim_IgnoresTextFromAnotherApp()
    {
        AgentContextDrag.Begin("### Ticket: Printer\n");
        Assert.Null(AgentContextDrag.Claim("rm -rf ~\n", Now));
        Assert.Null(AgentContextDrag.Claim("", Now));
        Assert.Null(AgentContextDrag.Claim(null, Now));
        // The real drop still lands while the drag is in flight.
        Assert.Equal("### Ticket: Printer\n", AgentContextDrag.Claim("### Ticket: Printer", Now));
    }

    [Fact]
    public void Claim_RefusesADropLongAfterTheDragEnded()
    {
        AgentContextDrag.Begin("### Device: PC\n");
        AgentContextDrag.End(Now);
        Assert.Null(AgentContextDrag.Claim("### Device: PC\n", Now + AgentContextDrag.Grace + TimeSpan.FromSeconds(1)));
        // Expired drags are forgotten, not kept for a later match.
        Assert.Null(AgentContextDrag.Claim("### Device: PC\n", Now));
    }

    // ── Rows and the button ──────────────────────────────────────────────

    [Fact]
    public void Targets_ResolveTheRowUnderThePointer_AndNothingElse()
    {
        RunSta(() =>
        {
            var host = new StackPanel { DataContext = new PipelineRun { PipelineName = "Page" } };
            var row = new Border { Tag = new PipelineRun { PipelineName = "CI", RunNumber = "7" } };
            var label = new TextBlock { Text = "CI #7" };
            row.Child = label;
            var loose = new TextBlock();
            host.Children.Add(row);
            host.Children.Add(loose);

            var targets = AgentContextMenu.Targets(host, label, null);
            Assert.NotNull(targets);
            Assert.Equal(AgentContext.ContextKind.PipelineRun, Assert.Single(targets).Kind);
            // The page's own data is not a row.
            Assert.Null(AgentContextMenu.Targets(host, loose, null));
        });
    }

    [Fact]
    public void Button_IsDisabledUntilItHasARecord()
    {
        RunSta(() =>
        {
            var button = new AgentContextButton();
            Assert.False(button.IsEnabled);
            button.Context = AgentContexts.PipelineRun(new PipelineRun { PipelineName = "CI", RunNumber = "7" });
            Assert.True(button.IsEnabled);
            Assert.Contains("pipeline run", (string)button.ToolTip);
            button.Context = null;
            Assert.False(button.IsEnabled);
        });
    }

    [Fact]
    public void Button_ToolTipSaysWhatAClickDoes()
    {
        Assert.Equal("Send this work item to the running agent, or copy it when none is running; right-click to copy",
            AgentContextButton.ToolTipFor(AgentContext.ContextKind.WorkItem, copyOnly: false));
        Assert.Equal("Copy this ticket for an agent", AgentContextButton.ToolTipFor(AgentContext.ContextKind.Ticket, copyOnly: true));
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
