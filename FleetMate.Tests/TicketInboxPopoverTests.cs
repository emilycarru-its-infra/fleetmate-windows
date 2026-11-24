using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
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

    [Fact]
    public Task LosingTheTicketServiceCannotLoadAnUnscopedLegacyCache() => WpfFixture.RunAsync(() =>
    {
        var page = Page();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var uid = Guid.NewGuid();
        typeof(TicketsPage).GetField("_me", flags)!.SetValue(page, new TdxPerson { Uid = uid });
        typeof(TicketsPage).GetField("_inboxPerson", flags)!.SetValue(page, new TicketInboxPerson(uid, "Example", null));
        typeof(TicketsPage).GetMethod("OnInboxPersonChanged", flags)!.Invoke(page, null);
        Assert.Null(typeof(TicketsPage).GetField("_inboxState", flags)!.GetValue(page));
        Assert.Null(typeof(TicketsPage).GetField("_inboxPerson", flags)!.GetValue(page));
        return Task.CompletedTask;
    });

    [Fact]
    public Task LegacyImportContentionDoesNotBlockTheDispatcher() => WpfFixture.RunAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "inbox-async-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string service = "https://tickets.example.invalid/tdwebapi/api/1/tickets";
            var person = new TicketInboxPerson(Guid.NewGuid(), "Example", null);
            var legacyRoot = Path.Combine(directory, "edition");
            var legacy = new TicketInboxStore(legacyRoot);
            legacy.SaveRead(person.Uid, new TicketInboxState { ReadIds = new() { 1 } });
            var sharedRoot = Path.Combine(directory, "shared");
            var key = TicketInboxReadStore.ServiceKey(service);
            var sharedDirectory = Path.Combine(sharedRoot, key);
            Directory.CreateDirectory(sharedDirectory);
            var store = new TicketInboxStore(legacyRoot, service, sharedRoot, key);
            var page = Page();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(TicketsPage).GetField("_inboxStore", flags)!.SetValue(page, store);
            typeof(TicketsPage).GetField("_inboxPerson", flags)!.SetValue(page, person);
            using var held = new FileStream(Path.Combine(sharedDirectory, $"{person.Uid}.json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var reload = (Task)typeof(TicketsPage).GetMethod("ReloadInboxFromDisk", flags)!.Invoke(page, null)!;
            Assert.False(reload.IsCompleted);
            var responsive = false;
            await page.Dispatcher.InvokeAsync(() => responsive = true);
            Assert.True(responsive);
            held.Dispose();
            await reload.WaitAsync(TimeSpan.FromSeconds(6));
            Assert.Contains(1, ((TicketInboxState)typeof(TicketsPage).GetField("_inboxState", flags)!.GetValue(page)!).ReadIds);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    [Fact]
    public Task PeerReadReloadUpdatesRowsWithoutReplacingSelection() => WpfFixture.RunAsync(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "inbox-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TicketInboxStore(Path.Combine(directory, "one"),
                "https://tickets.example.invalid/tdwebapi/api/1/tickets", Path.Combine(directory, "shared"));
            var person = new TicketInboxPerson(Guid.NewGuid(), "Example Person", "person@example.invalid");
            var state = new TicketInboxState { Items = new() { new() { FeedId = 1, TicketId = 42 } } };
            var page = Page();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(TicketsPage).GetField("_inboxStore", flags)!.SetValue(page, store);
            typeof(TicketsPage).GetField("_inboxPerson", flags)!.SetValue(page, person);
            typeof(TicketsPage).GetField("_inboxState", flags)!.SetValue(page, state);
            typeof(TicketsPage).GetMethod("RenderInboxItems", flags)!.Invoke(page, null);
            var list = Assert.IsType<ListView>(page.FindName("InboxListView"));
            var source = list.ItemsSource;
            var row = Assert.IsType<InboxRow>(Assert.Single(list.Items.Cast<object>()));
            list.SelectedItem = row;
            store.SetRead(person.Uid, new[] { 1 }, true);
            await (Task)typeof(TicketsPage).GetMethod("ReloadInboxReadMarks", flags)!.Invoke(page, null)!;
            Assert.True(row.IsRead);
            Assert.Same(source, list.ItemsSource);
            Assert.Same(row, list.SelectedItem);
            store.SetRead(person.Uid, new[] { 1 }, false);
            await (Task)typeof(TicketsPage).GetMethod("ReloadInboxReadMarks", flags)!.Invoke(page, null)!;
            Assert.False(row.IsRead);
            Assert.Same(row, list.SelectedItem);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    });

    private static void ShowSidebar(TicketsPage page)
    {
        if (Assert.IsType<Border>(page.FindName("InboxSidebar")).Visibility != Visibility.Visible)
            Assert.True(((IPageCommands)page).Execute(AppShortcut.ShowInbox));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task SidebarTogglesWithoutChangingBoardOrListContext(bool boardMode) => WpfFixture.RunAsync(() =>
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
        var sidebar = Assert.IsType<Border>(page.FindName("InboxSidebar"));
        var button = Assert.IsType<ToggleButton>(page.FindName("InboxButton"));
        ShowSidebar(page);
        try
        {
            page.UpdateLayout();
            Assert.True(button.IsChecked);
            Assert.True(sidebar.ActualWidth >= TicketsToolbarRules.InboxSidebarMinWidth);
            Assert.True(((IPageCommands)page).Execute(AppShortcut.ShowInbox));
            page.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, sidebar.Visibility);
            Assert.False(button.IsChecked);
            Assert.True(((IPageCommands)page).Execute(AppShortcut.ShowInbox));
            Assert.Equal(Visibility.Visible, sidebar.Visibility);
            Assert.Equal(boardMode, board.IsChecked);
            Assert.Equal(!boardMode, list.IsChecked);
            Assert.Equal("preserved filter", search.Text);
            Assert.Same(source, ticketList.ItemsSource);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task FirstScanOrUnavailableStateKeepsTheEnvelopeAndSidebarAvailable() => WpfFixture.RunAsync(() =>
    {
        var page = new TicketsPage();
        var host = Assert.IsType<Grid>(page.FindName("InboxButtonHost"));
        Assert.Equal(Visibility.Visible, host.Visibility);
        var window = Host(page);
        try
        {
            ShowSidebar(page);
            typeof(TicketsPage).GetField("_inboxState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(page, new TicketInboxState());
            typeof(TicketsPage).GetMethod("RenderInboxItems", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
            Assert.Equal(Visibility.Visible, Assert.IsType<Border>(page.FindName("InboxSidebar")).Visibility);
            Assert.Equal(Visibility.Visible, host.Visibility);
            Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<TextBlock>(page.FindName("InboxEmptyText")).Text));
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task ActivatingAnInboxRowKeepsTheSidebarAndTheWorkQueue() => WpfFixture.RunAsync(async () =>
    {
        var page = Page();
        var window = Host(page);
        ShowSidebar(page);
        try
        {
            var inbox = Assert.IsType<ListView>(page.FindName("InboxListView"));
            var row = new InboxRow(new TicketInboxItem { FeedId = 1, TicketId = 42, TicketTitle = "Example ticket" }, true);
            inbox.ItemsSource = new[] { row };
            inbox.SelectedItem = row;
            await page.ActivateInboxSelectionAsync();
            Assert.Equal(Visibility.Visible, Assert.IsType<Border>(page.FindName("InboxSidebar")).Visibility);
            Assert.Same(row, inbox.SelectedItem);
            Assert.True(Assert.IsType<RadioButton>(page.FindName("BoardViewRadio")).IsChecked);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task AnUnchangedRefreshKeepsTheRowsAndSelection() => WpfFixture.RunAsync(() =>
    {
        var page = Page();
        var state = new TicketInboxState { Items = new() { new() { FeedId = 1, TicketId = 42 }, new() { FeedId = 2, TicketId = 43 } } };
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(TicketsPage).GetField("_inboxState", flags)!.SetValue(page, state);
        var render = typeof(TicketsPage).GetMethod("RenderInboxItems", flags)!;
        render.Invoke(page, null);
        var list = Assert.IsType<ListView>(page.FindName("InboxListView"));
        var source = list.ItemsSource;
        var selected = list.Items[1];
        list.SelectedItem = selected;
        render.Invoke(page, null);
        Assert.Same(source, list.ItemsSource);
        Assert.Same(selected, list.SelectedItem);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(360, 1600, true, 360)]
    [InlineData(360, 900, true, 266)]
    [InlineData(500, 1000, false, 482)]
    [InlineData(9999, 3000, false, 640)]
    [InlineData(10, 3000, false, 260)]
    public void SidebarWidthLeavesTheWorkspaceItsRoom(double preferred, double page, bool detail, double expected) =>
        Assert.Equal(expected, TicketsToolbarRules.InboxSidebarWidth(preferred, page, detail));
}
