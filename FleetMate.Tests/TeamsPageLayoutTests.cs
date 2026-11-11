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
        var thread = new FleetMate.Core.Services.Teams.TeamsThread("root", "team", "Example Team", "channel", "General", "Topic", null, new[] { root, reply, reply with { MessageId = "reply-2", Text = "Another reply" } });
        var host = new StackPanel();
        string? copied = null;
        TeamsThreadCards.Show(host, thread, FleetMate.Core.Services.Teams.TeamsLinkPolicy.DefaultHosts, text => copied = text, _ => { });
        var rootRow = Assert.IsType<ConversationRailRow>(host.Children[1]);
        var rootBody = Assert.IsType<StackPanel>(rootRow.Child);
        var card = Assert.IsType<Border>(rootBody.Children[0]);
        var body = Assert.IsType<StackPanel>(card.Child);
        Assert.Equal(root.Text, Assert.IsType<TextBox>(body.Children[1]).Text);
        var header = Assert.IsType<DockPanel>(body.Children[0]);
        Assert.IsType<Button>(header.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Contains(root.Text, copied!);
        var disclosure = Assert.IsType<Expander>(rootBody.Children[1]);
        var replies = Assert.IsType<StackPanel>(host.Children[2]);
        Assert.False(disclosure.IsExpanded);
        Assert.Equal(Visibility.Collapsed, replies.Visibility);
        disclosure.IsExpanded = true;
        Assert.True(rootRow.Continues);
        Assert.Equal(Visibility.Visible, replies.Visibility);
        Assert.Equal(2, replies.Children.Count);
        var replyRow = Assert.IsType<ConversationRailRow>(replies.Children[0]);
        Assert.True(replyRow.IsFirstReply);
        Assert.True(replyRow.Continues);
        var last = Assert.IsType<ConversationRailRow>(replies.Children[1]);
        Assert.False(last.IsFirstReply);
        Assert.False(last.Continues);
        Assert.Equal("EA", ConversationRailRow.Initials(root.AuthorDisplayName));
    });
    [Fact]
    public void FollowingFollowsAllChannelsAndAnEmptySetShowsLocalGuidance() => WpfFixture.Run(() =>
    {
        var page = new TeamsPage();
        var tree = Assert.IsType<TreeView>(page.FindName("ChannelTree"));
        var rows = tree.Items.Cast<TreeViewItem>().ToList();
        var all = rows.FindIndex(row => row.Header as string == "All channels");
        Assert.StartsWith("Following", rows[all + 1].Header as string);
        rows[all + 1].IsSelected = true;
        Assert.Equal("Follow channels", Assert.IsType<TextBlock>(page.FindName("WelcomeTitle")).Text);
        Assert.Equal("Search Following", ((ITabSearch)page).SearchScope!.Prompt);
        foreach (var name in new[] { "CopyMarkdownButton", "CopyButton", "OpenButton" })
            Assert.IsType<WrapPanel>(Assert.IsType<Button>(page.FindName(name)).Parent);
    });
}
