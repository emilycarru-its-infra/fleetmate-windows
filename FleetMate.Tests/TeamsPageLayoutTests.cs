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
    [Fact]
    public void ResizingTheWindowPreservesTheAdjustedSidebarWidth() => WpfFixture.Run(() =>
    {
        var page = new TeamsPage();
        page.Measure(new Size(1500, 900));
        page.Arrange(new Rect(0, 0, 1500, 900));
        page.UpdateLayout();
        var column = Assert.IsType<ColumnDefinition>(page.FindName("SidebarColumn"));
        column.Width = new GridLength(310);
        page.Measure(new Size(1300, 800));
        page.Arrange(new Rect(0, 0, 1300, 800));
        page.UpdateLayout();
        Assert.Equal(310, column.Width.Value);
    });
    [Fact]
    public void ThreadCardsKeepRootAndRepliesSeparateAndCopyLiteralMessageText() => WpfFixture.Run(() =>
    {
        var root = new FleetMate.Core.Services.Teams.TeamsMessage("root", null, "Example author", "2026-11-01T12:00:00Z", null,
            "<b>Literal message text</b>", Array.Empty<FleetMate.Core.Services.Teams.TeamsAttachment>(), null);
        var reply = root with { MessageId = "reply", ReplyToId = "root", Text = "Reply text" };
        var thread = new FleetMate.Core.Services.Teams.TeamsThread("root", "team", "Example Team", "channel", "General", "Topic", null, new[] { root, reply });
        var host = new StackPanel();
        string? copied = null;
        TeamsThreadCards.Show(host, thread, FleetMate.Core.Services.Teams.TeamsLinkPolicy.DefaultHosts, text => copied = text, _ => { });
        var card = Assert.IsType<Border>(host.Children[1]);
        var body = Assert.IsType<StackPanel>(card.Child);
        Assert.Equal(root.Text, Assert.IsType<TextBox>(body.Children[1]).Text);
        var header = Assert.IsType<DockPanel>(body.Children[0]);
        Assert.IsType<Button>(header.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Contains(root.Text, copied!);
        var replies = Assert.IsType<Expander>(host.Children[2]);
        Assert.False(replies.IsExpanded);
        Assert.Single(Assert.IsType<StackPanel>(replies.Content).Children.Cast<object>());
    });
}
