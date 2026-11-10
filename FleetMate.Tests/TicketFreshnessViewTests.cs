using System.Reflection;
using System.Windows.Controls;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using FleetMate.GUI.Views.Tickets;
using Xunit;

namespace FleetMate.Tests;

[Collection(WpfCollection.Name)]
public sealed class TicketFreshnessViewTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(TicketsPage page, string field, object? value) => typeof(TicketsPage).GetField(field, Flags)!.SetValue(page, value);

    [Fact]
    public Task AutomaticDetailRefreshPreservesCommentNotifyDescriptionAndPendingPicks() => WpfFixture.RunAsync(() =>
    {
        var page = new TicketsPage();
        var person = Guid.NewGuid();
        var original = new TdxTicket { Id = 42, Title = "Original", StatusId = 1, ModifiedDate = DateTime.UtcNow.AddMinutes(-1), ResponsibleUid = person, ResponsibleFullName = "Example Person", ResponsibleEmail = "person@example.invalid" };
        Set(page, "_selectedTicket", original);
        typeof(TicketsPage).GetMethod("UpdateDetailPanel", Flags)!.Invoke(page, new object[] { original });
        var loads = (TicketDetailLoadState)typeof(TicketsPage).GetField("_detailLoads", Flags)!.GetValue(page)!;
        var request = loads.Begin(42);
        var edits = new TicketPendingEdits(original);
        edits.Set(TicketDetailFields.Status, 6);
        Set(page, "_pendingEdits", edits);
        Assert.IsType<TextBox>(page.FindName("CommentTextBox")).Text = "Unsent comment";
        Assert.IsType<CheckBox>(page.FindName("PrivateCheckBox")).IsChecked = true;
        var recipients = Assert.IsType<StackPanel>(page.FindName("NotifyCheckboxesPanel"));
        var selected = recipients.Children.OfType<CheckBox>().First();
        selected.IsChecked = true;
        var recipient = selected.Tag;
        Set(page, "_descriptionEditTicketId", 42);
        Assert.IsType<TextBox>(page.FindName("DescriptionEditor")).Text = "Unsent description";
        var fresh = new TdxTicket { Id = 42, Title = "Fresh", StatusId = 7, Description = "Server description", ModifiedDate = DateTime.UtcNow, ResponsibleUid = person, ResponsibleFullName = "Example Person", ResponsibleEmail = "person@example.invalid" };
        Assert.True(page.ApplyBackgroundTicketRefresh(request, fresh, new()));
        Assert.Equal("Unsent comment", Assert.IsType<TextBox>(page.FindName("CommentTextBox")).Text);
        Assert.True(Assert.IsType<CheckBox>(page.FindName("PrivateCheckBox")).IsChecked);
        Assert.Equal("Unsent description", Assert.IsType<TextBox>(page.FindName("DescriptionEditor")).Text);
        Assert.Contains(recipients.Children.OfType<CheckBox>(), box => Equals(box.Tag, recipient) && box.IsChecked == true);
        Assert.True(edits.IsEdited(TicketDetailFields.Status));
        Assert.Equal(6, edits.Value(TicketDetailFields.Status));
        return Task.CompletedTask;
    });

    [Fact]
    public Task OldSelectionGenerationCannotPaintAfterReturningToTheSameTicket() => WpfFixture.RunAsync(() =>
    {
        var page = new TicketsPage();
        var loads = (TicketDetailLoadState)typeof(TicketsPage).GetField("_detailLoads", Flags)!.GetValue(page)!;
        var stale = loads.Begin(42);
        loads.Begin(43); loads.Begin(42);
        var current = new TdxTicket { Id = 42, Title = "Current" };
        Set(page, "_selectedTicket", current);
        Assert.False(page.ApplyBackgroundTicketRefresh(stale, new TdxTicket { Id = 42, Title = "Stale" }, new()));
        Assert.Same(current, typeof(TicketsPage).GetField("_selectedTicket", Flags)!.GetValue(page));
        return Task.CompletedTask;
    });
}
