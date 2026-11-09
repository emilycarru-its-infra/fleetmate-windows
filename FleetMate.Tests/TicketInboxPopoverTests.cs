using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FleetMate.Core.Models.Tickets;
using FleetMate.GUI.Views.Shared;
using FleetMate.GUI.Views.Tickets;
using Xunit;

namespace FleetMate.Tests;

[Collection(WpfCollection.Name)]
public sealed class TicketInboxPopoverTests
{
    private static TicketsPage Page()
    {
        var page = new TicketsPage();
        typeof(TicketsPage).GetField("_inboxState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(page, new TicketInboxState { ScannedThrough = DateTime.UtcNow });
        Assert.IsType<Grid>(page.FindName("InboxButtonHost")).Visibility = Visibility.Visible;
        return page;
    }

    private static Window Host(TicketsPage page)
    {
        var window = new Window { Content = page, Width = 1100, Height = 800, Left = -10000, Top = -10000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        window.Show();
        page.UpdateLayout();
        return window;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task InboxOpensWithoutChangingBoardOrListContext(bool boardMode) => WpfFixture.RunAsync(async () =>
    {
        var page = Page();
        var window = Host(page);
        var board = Assert.IsType<RadioButton>(page.FindName("BoardViewRadio"));
        var list = Assert.IsType<RadioButton>(page.FindName("ListViewRadio"));
        if (boardMode) board.IsChecked = true; else list.IsChecked = true;
        var search = Assert.IsType<TextBox>(page.FindName("SearchBox"));
        search.Text = "preserved filter";
        var ticketList = Assert.IsType<ListView>(page.FindName("TicketsListView"));
        var source = ticketList.ItemsSource;
        var popup = Assert.IsType<Popup>(page.FindName("InboxPopup"));
        var button = Assert.IsType<ToggleButton>(page.FindName("InboxButton"));
        Assert.Same(button, popup.PlacementTarget);
        Assert.False(popup.StaysOpen);
        popup.StaysOpen = true;
        Assert.True(((IPageCommands)page).Execute(AppShortcut.ShowInbox));
        try
        {
            Assert.True(popup.IsOpen);
            var popover = Assert.IsType<Border>(page.FindName("InboxPopover"));
            var anchorBottom = button.TranslatePoint(new Point(0, button.ActualHeight), window).Y;
            Assert.Equal(window.ActualHeight - anchorBottom - popup.VerticalOffset, popover.Height, 1);
            var originalHeight = popover.Height;
            window.Height += 80;
            window.UpdateLayout();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.True(popover.Height > originalHeight + 50);
            Assert.Equal(boardMode, board.IsChecked);
            Assert.Equal(!boardMode, list.IsChecked);
            Assert.Equal("preserved filter", search.Text);
            Assert.Same(source, ticketList.ItemsSource);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            popup.Closed += (_, _) => closed.TrySetResult();
            popup.IsOpen = false;
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(button.IsChecked);
            Assert.Equal(boardMode, board.IsChecked);
            Assert.Equal(!boardMode, list.IsChecked);
        }
        finally { popup.IsOpen = false; window.Close(); }
    });

    [Fact]
    public Task ActivatingAnInboxRowDismissesWithoutSwitchingTheWorkQueue() => WpfFixture.RunAsync(async () =>
    {
        var page = Page();
        var window = Host(page);
        var popup = Assert.IsType<Popup>(page.FindName("InboxPopup"));
        popup.StaysOpen = true;
        ((IPageCommands)page).Execute(AppShortcut.ShowInbox);
        try
        {
            var inbox = Assert.IsType<ListView>(page.FindName("InboxListView"));
            var row = new InboxRow(new TicketInboxItem { FeedId = 1, TicketId = 42, TicketTitle = "Example ticket" }, true);
            inbox.ItemsSource = new[] { row };
            inbox.SelectedItem = row;
            Assert.True(popup.IsOpen);
            await page.ActivateInboxSelectionAsync();
            Assert.False(popup.IsOpen);
            Assert.True(Assert.IsType<RadioButton>(page.FindName("BoardViewRadio")).IsChecked);
        }
        finally { popup.IsOpen = false; window.Close(); }
    });
}
