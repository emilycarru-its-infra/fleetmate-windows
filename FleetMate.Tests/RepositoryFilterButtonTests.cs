using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Models.Projects;
using FleetMate.GUI.Views.Shared;
using Xunit;

namespace FleetMate.Tests;

[Collection(WpfCollection.Name)]
public sealed class RepositoryFilterButtonTests
{
    [Fact]
    public void NativeMenuPicksQualifiedLeafAndKeepsOtherProvidersUnchecked() => WpfFixture.Run(() =>
    {
        var github = new RepositoryFilterScope(PullRequestSource.GitHub, "Example", "tools");
        var azure = new RepositoryFilterScope(PullRequestSource.AzureDevOps, "Example", "tools");
        var button = new RepositoryFilterButton { MaxWidth = 360 };
        button.SetEntries(new() { new(azure, 2), new(github, 3) }, null);
        var changed = 0;
        button.SelectionChanged += (_, _) => changed++;
        var menu = button.ContextMenu!;
        var providerHeadings = menu.Items.OfType<MenuItem>().Where(item => !item.IsEnabled).ToList();
        Assert.Equal(2, providerHeadings.Count);
        var githubProject = menu.Items.OfType<MenuItem>().Last(item => item.HasItems);
        var leaf = Assert.IsType<MenuItem>(githubProject.Items[0]);
        leaf.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(github.Key, button.Selection);
        Assert.Equal(1, changed);
        var branches = button.ContextMenu!.Items.OfType<MenuItem>().Where(item => item.HasItems).ToList();
        Assert.False(Assert.IsType<MenuItem>(branches[0].Items[0]).IsChecked);
        Assert.True(Assert.IsType<MenuItem>(branches[1].Items[0]).IsChecked);
        Assert.Equal(azure.Key, button.SetEntries(new() { new(azure, 2), new(github, 3) }, azure.Key));
        Assert.Equal(1, changed);
        Assert.Null(button.SetEntries(new() { new(github, 3) }, azure.Key));
        Assert.Equal(Visibility.Collapsed, button.Visibility);
    });
}
