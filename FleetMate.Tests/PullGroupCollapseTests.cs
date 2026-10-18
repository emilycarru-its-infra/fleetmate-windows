using FleetMate.GUI.Views.Development;
using Xunit;

namespace FleetMate.Tests;

/// <summary>Development › Pulls repository groups collapse and expand, matching the macOS list.</summary>
public class PullGroupCollapseTests
{
    private static readonly string[] Visible = { "Systems/Handbook", "Devices/Munki", "Cloud/Pulse" };

    private static HashSet<string> Set(params string[] keys) => new(keys, StringComparer.Ordinal);

    [Fact]
    public void ClickTogglesOneGroup()
    {
        var collapsed = PullGroupCollapse.Toggle(Set(), "Devices/Munki", all: false, Visible);
        Assert.Equal(new[] { "Devices/Munki" }, collapsed);
        Assert.Empty(PullGroupCollapse.Toggle(collapsed, "Devices/Munki", all: false, Visible));
    }

    [Fact]
    public void ModifierClickMakesEveryGroupFollowTheOneClicked()
    {
        var all = PullGroupCollapse.Toggle(Set(), "Cloud/Pulse", all: true, Visible);
        Assert.Equal(Visible.OrderBy(k => k), all.OrderBy(k => k));
        Assert.Empty(PullGroupCollapse.Toggle(all, "Cloud/Pulse", all: true, Visible));
    }

    [Fact]
    public void CollapseAllKeepsGroupsHiddenByFilters()
    {
        var collapsed = PullGroupCollapse.SetAll(Set("Other/Repo"), true, Visible);
        Assert.Contains("Other/Repo", collapsed);
        Assert.Equal(4, collapsed.Count);
        Assert.Empty(PullGroupCollapse.SetAll(collapsed, false, Visible));
    }

    [Fact]
    public void StoredOneKeyPerLine()
    {
        Assert.Null(PullGroupCollapse.Format(Array.Empty<string>()));
        var stored = PullGroupCollapse.Format(new[] { "b/two", "a/one", "b/two" });
        Assert.Equal("a/one\nb/two", stored);
        Assert.Equal(Set("a/one", "b/two"), PullGroupCollapse.Parse(stored));
        Assert.Empty(PullGroupCollapse.Parse(null));
    }
}
