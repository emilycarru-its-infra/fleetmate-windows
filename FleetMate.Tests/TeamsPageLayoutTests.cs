using System.Windows;
using System.Windows.Controls;
using FleetMate.GUI.Views.Shared;
using FleetMate.GUI.Views.Teams;
using Xunit;

namespace FleetMate.Tests;

[Collection(WpfCollection.Name)]
public class TeamsPageLayoutTests
{
    [Theory]
    [InlineData(900, 600)]
    [InlineData(1500, 1000)]
    public void OpeningViewKeepsFiltersAndSearchGuidanceAtTheTop(double width, double height) => WpfFixture.Run(() =>
    {
        var page = new TeamsPage();
        page.Measure(new Size(width, height));
        page.Arrange(new Rect(0, 0, width, height));
        page.UpdateLayout();

        var welcome = Assert.IsType<Border>(page.FindName("Welcome"));
        var sidebar = Assert.IsType<DockPanel>(page.FindName("Sidebar"));
        var scope = Assert.IsType<TextBlock>(page.FindName("ScopeText"));
        Assert.True(welcome.TranslatePoint(new Point(), page).Y < 200);
        Assert.True(scope.TranslatePoint(new Point(), page).Y < 150);
        Assert.True(sidebar.ActualWidth >= 180);
        Assert.Equal(Visibility.Visible, welcome.Visibility);
        Assert.Equal("Search Teams channels", ((ITabSearch)page).SearchScope!.Prompt);
        Assert.NotNull(((ITabSearch)page).SearchScope!.Submit);
    });
}
