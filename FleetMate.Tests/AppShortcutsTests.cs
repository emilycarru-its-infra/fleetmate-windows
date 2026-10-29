using System.Windows.Input;
using FleetMate.Core.Config;
using FleetMate.GUI.Views.Shared;
using Xunit;

namespace FleetMate.Tests;

public class AppShortcutsTests
{
    [Theory]
    [InlineData(Key.Left, ModifierKeys.Alt, "Back")]
    [InlineData(Key.Right, ModifierKeys.Alt, "Forward")]
    [InlineData(Key.Tab, ModifierKeys.Control, "NextTab")]
    [InlineData(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, "PreviousTab")]
    [InlineData(Key.D1, ModifierKeys.Control | ModifierKeys.Alt, "ShowList")]
    [InlineData(Key.D2, ModifierKeys.Control | ModifierKeys.Alt, "ShowBoard")]
    [InlineData(Key.F, ModifierKeys.Control | ModifierKeys.Shift, "ToggleFilters")]
    [InlineData(Key.K, ModifierKeys.Control | ModifierKeys.Shift, "ClearFilters")]
    [InlineData(Key.F5, ModifierKeys.None, "Refresh")]
    [InlineData(Key.R, ModifierKeys.Control, "Refresh")]
    [InlineData(Key.OemPlus, ModifierKeys.Control, "ZoomIn")]
    [InlineData(Key.OemMinus, ModifierKeys.Control, "ZoomOut")]
    [InlineData(Key.D0, ModifierKeys.Control, "ActualSize")]
    [InlineData(Key.L, ModifierKeys.Control | ModifierKeys.Shift, "ActivityLog")]
    [InlineData(Key.OemComma, ModifierKeys.Control, "Settings")]
    public void MapsMacMenuShortcutsToWindowsKeys(Key key, ModifierKeys mods, string expected) =>
        Assert.Equal(expected, AppShortcuts.Resolve(key, mods)?.ToString());

    [Theory]
    [InlineData(Key.K, ModifierKeys.Control)]
    [InlineData(Key.D1, ModifierKeys.Control)]
    [InlineData(Key.T, ModifierKeys.Control)]
    [InlineData(Key.Left, ModifierKeys.None)]
    [InlineData(Key.L, ModifierKeys.Control | ModifierKeys.Alt)]
    public void LeavesOtherKeysAlone(Key key, ModifierKeys mods) =>
        Assert.Null(AppShortcuts.Resolve(key, mods));

    [Fact]
    public void BackAndForwardRetraceVisits()
    {
        var history = new TabHistory();
        history.Visit("Development", "Devices");
        history.Visit("Devices", "Tickets");

        Assert.Equal("Devices", history.Back("Tickets"));
        Assert.Equal("Development", history.Back("Devices"));
        Assert.Null(history.Back("Development"));
        Assert.Equal("Devices", history.Forward("Development"));
    }

    [Fact]
    public void VisitingClearsForward()
    {
        var history = new TabHistory();
        history.Visit("Development", "Devices");
        history.Back("Devices");
        history.Visit("Development", "Inventory");

        Assert.False(history.CanGoForward);
    }

    // Sub-tabs are places in history too: Back from Commits returns to Pulls
    // in the same tab before it leaves the tab.
    [Fact]
    public void BackRetracesSubTabsWithinATab()
    {
        var history = new TabHistory();
        var repos = new NavigationLocation("Development", "Repos");
        var pulls = new NavigationLocation("Development", "Pulls");
        var commits = new NavigationLocation("Development", "Commits");
        var tickets = new NavigationLocation("Tickets", "Board");
        history.Visit(repos, pulls);
        history.Visit(pulls, commits);
        history.Visit(commits, tickets);

        Assert.Equal(commits, history.Back(tickets));
        Assert.Equal(pulls, history.Back(commits));
        Assert.Equal(repos, history.Back(pulls));
        Assert.False(history.CanGoBack);
        Assert.Equal(pulls, history.Forward(repos));
    }

    [Fact]
    public void ReturningToTheSamePlaceRecordsNothing()
    {
        var history = new TabHistory();
        var place = new NavigationLocation("Projects", "Queries");
        history.Visit(place, place);

        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void ZoomStaysInTheMacRange()
    {
        Assert.Equal(1.6, ZoomScale.Clamp(3));
        Assert.Equal(0.9, ZoomScale.Clamp(0.5));
    }

    // The zoom keys and the Text size slider move in the same 5% steps.
    [Fact]
    public void ZoomStepsMatchTheTextSizeSlider()
    {
        Assert.Equal(0.05, AppTextScale.Step);
        Assert.Equal(1.05, AppTextScale.Clamp(AppTextScale.Default + AppTextScale.Step), 3);
        Assert.Equal(1.6, AppTextScale.Clamp(1.6 + AppTextScale.Step), 3);
        Assert.Equal(0.9, AppTextScale.Clamp(0.9 - AppTextScale.Step), 3);
    }
}
