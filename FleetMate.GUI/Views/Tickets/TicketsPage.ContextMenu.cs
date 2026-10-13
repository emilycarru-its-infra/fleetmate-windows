using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Tickets;
using FleetMate.GUI.Views.Shared;
using ModernWpf.Controls;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>
/// The right-click menu of ticket rows and board cards: the detail pane's
/// actions without opening the ticket first. Assign to Me, Unassign, Status
/// and Priority save at once; Reassign, Set Parent and Create Parent open the
/// ticket and its popup. The menu opens on right click and, on a focused row,
/// with Shift+F10 or the Menu key.
/// </summary>
public partial class TicketsPage
{
    /// <summary>TeamDynamix's statuses and priorities, once fetched; the loaded tickets fill in until then.</summary>
    private IReadOnlyDictionary<int, string>? _statusChoices;
    private IReadOnlyDictionary<int, string>? _priorityChoices;

    private void AttachTicketMenus()
    {
        TicketsListView.ContextMenuOpening += OnTicketContextMenuOpening;
        BoardColumnsControl.ContextMenuOpening += OnTicketContextMenuOpening;
        // The list's rows still drag out as their agent block; cards drag
        // between columns, and that drag carries the block already.
        AgentContextDrag.Attach(TicketsListView, source => AgentContextMenu.Targets(TicketsListView, source, null));
    }

    /// <summary>Fetch the status and priority lists for the menu's submenus.</summary>
    private async Task WarmTicketChoicesAsync()
    {
        if (_tdxService == null) return;
        try
        {
            _statusChoices = new Dictionary<int, string>(await _tdxService.GetStatusesAsync());
            _priorityChoices = new Dictionary<int, string>(await _tdxService.GetPrioritiesAsync());
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[tickets] Could not load statuses and priorities for the ticket menu");
        }
    }

    private void OnTicketContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindTicketRow(source, (DependencyObject)sender) is not var (element, row)) return;

        // The cached ticket is the freshest copy the list has.
        var ticket = _allTickets.FirstOrDefault(t => t.Id == row.Id) ?? row.Ticket;
        if (_statusChoices == null || _priorityChoices == null) _ = WarmTicketChoicesAsync();

        var statuses = TicketMenuPlan.Known(_statusChoices, _allTickets.Select(t => ((int?)t.StatusId, t.StatusName)));
        var priorities = TicketMenuPlan.Known(_priorityChoices, _allTickets.Select(t => (t.PriorityId, t.PriorityName)));
        var plan = TicketMenuPlan.Build(
            ticket,
            _me?.Uid,
            AppEdition.Current.IsTicketsOnly,
            canWrite: _tdxService != null && _app != null,
            hasWebUrl: _app?.Config?.Tdx?.GetTicketWebUrl(ticket.Id) != null,
            statuses,
            priorities);

        var keyboard = e.CursorLeft < 0 && e.CursorTop < 0;
        var menu = new ContextMenu
        {
            PlacementTarget = element,
            Placement = keyboard ? PlacementMode.Bottom : PlacementMode.MousePoint,
        };
        AutomationProperties.SetAutomationId(menu, "TicketContextMenu");
        foreach (var entry in plan) menu.Items.Add(ToMenuItem(entry, ticket));
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// The ticket row (list) or card (board) under <paramref name="source"/>,
    /// with the element to anchor the menu to; null over anything else.
    /// </summary>
    private static (FrameworkElement Element, TicketRowViewModel Row)? FindTicketRow(DependencyObject source, DependencyObject host)
    {
        for (var node = source; node != null && !ReferenceEquals(node, host); node = ParentOf(node))
        {
            if (node is FrameworkElement { DataContext: TicketRowViewModel row } element) return (element, row);
        }
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private Control ToMenuItem(TicketMenuEntry entry, TdxTicket ticket)
    {
        if (entry.IsSeparator) return new Separator();

        var item = new MenuItem { Header = entry.Header, IsEnabled = entry.IsEnabled };
        AutomationProperties.SetAutomationId(item, entry.AutomationId);
        if (Glyph(entry.Command) is { } glyph) item.Icon = new FontIcon { Glyph = glyph };

        if (entry.Choices is { } choices)
        {
            var field = entry.Command == TicketMenuCommand.Status ? "StatusID" : "PriorityID";
            foreach (var choice in choices)
            {
                // Checkable, so screen readers hear which value is current; the
                // menu is rebuilt each time it opens, so a click never leaves it toggled.
                var sub = new MenuItem { Header = choice.Name, IsCheckable = true, IsChecked = choice.IsCurrent };
                AutomationProperties.SetAutomationId(sub, $"{entry.AutomationId}_{choice.Id}");
                sub.Click += async (_, _) => await UpdateTicketFieldAsync(ticket.Id, field, choice.Id);
                item.Items.Add(sub);
            }
            return item;
        }

        item.Click += (_, _) => Execute(entry.Command!.Value, ticket);
        return item;
    }

    private static string? Glyph(TicketMenuCommand? command) => command switch
    {
        TicketMenuCommand.OpenInWeb => "",
        TicketMenuCommand.CopyNumber or TicketMenuCommand.CopyTitle => "",
        TicketMenuCommand.CopyLink => "",
        TicketMenuCommand.AssignToMe => "",
        TicketMenuCommand.Reassign => "",
        TicketMenuCommand.Unassign => "",
        TicketMenuCommand.SetParent => "",
        TicketMenuCommand.CreateParent => "",
        TicketMenuCommand.Refresh => "",
        TicketMenuCommand.CopyForAgent => "",
        TicketMenuCommand.SendToAgent => "",
        _ => null,
    };

    private async void Execute(TicketMenuCommand command, TdxTicket ticket)
    {
        switch (command)
        {
            case TicketMenuCommand.OpenInWeb:
                OpenTicketInWeb(ticket.Id);
                break;
            case TicketMenuCommand.CopyNumber:
                CopyToClipboard(ticket.Id.ToString(), "Ticket number copied");
                break;
            case TicketMenuCommand.CopyLink:
                if (TicketUrl(ticket.Id) is { } url) CopyToClipboard(url, "Ticket link copied");
                break;
            case TicketMenuCommand.CopyTitle:
                CopyToClipboard(ticket.Title ?? "", "Ticket title copied");
                break;
            case TicketMenuCommand.AssignToMe:
                if (_me?.Uid is { } uid) await SetResponsibleAsync(ticket.Id, uid, _me.DisplayName);
                break;
            case TicketMenuCommand.Unassign:
                await SetResponsibleAsync(ticket.Id, null, null);
                break;
            case TicketMenuCommand.Reassign:
                OpenTicketWithPopup(ticket, ReassignPopup);
                break;
            case TicketMenuCommand.SetParent:
                OpenTicketWithPopup(ticket, SetParentPopup);
                break;
            case TicketMenuCommand.CreateParent:
                OpenTicketWithPopup(ticket, CreateParentPopup);
                break;
            case TicketMenuCommand.Refresh:
                await RefreshTicketAsync(ticket.Id);
                break;
            case TicketMenuCommand.CopyForAgent:
                if (AgentContextMenu.Resolve(ticket) is { } copy) AgentContextMenu.Copy([copy]);
                break;
            case TicketMenuCommand.SendToAgent:
                if (AgentContextMenu.Resolve(ticket) is { } send) AgentContextMenu.Send([send]);
                break;
        }
    }

    /// <summary>
    /// Open the ticket in the detail pane, then its popup. The popup waits for
    /// the menu to close: a StaysOpen="False" popup opened while the menu still
    /// holds the mouse would close again at once.
    /// </summary>
    private void OpenTicketWithPopup(TdxTicket ticket, Popup popup)
    {
        ShowTicket(ticket);
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (_selectedTicket?.Id == ticket.Id) popup.IsOpen = true;
        });
    }

    /// <summary>
    /// Select <paramref name="ticket"/> and show it in the detail pane: the
    /// list row in list mode (its selection loads the detail), the ticket
    /// itself on the board, as a card click does.
    /// </summary>
    private void ShowTicket(TdxTicket ticket)
    {
        if (!_isBoardView && _ticketRows.Any(r => r.Id == ticket.Id))
        {
            RevealTicket(ticket.Id);
            if (!_detailPanelVisible)
            {
                _detailPanelVisible = true;
                OpenDetailPanel();
            }
            return;
        }
        _ = OpenTicketInDetailAsync(ticket);
    }

    /// <summary>Re-fetch one ticket into the list, and into the detail pane when it is open there.</summary>
    private async Task RefreshTicketAsync(int ticketId)
    {
        if (_tdxService == null) return;
        ShowActionMessage($"Refreshing #{ticketId}…", isLoading: true);
        var fresh = await _tdxService.GetTicketAsync(ticketId);
        if (fresh == null)
        {
            ShowActionMessage($"Could not reload #{ticketId}. See the log for the response.", isError: true);
            return;
        }
        ReplaceCachedTicket(fresh);
        if (_selectedTicket?.Id == ticketId)
        {
            _ticketFeed = await _tdxService.GetTicketFeedAsync(ticketId);
            UpdateFeedPanel();
        }
        ShowActionMessage($"Refreshed #{ticketId}");
    }

    private void CopyToClipboard(string text, string done)
    {
        if (AgentContextClipboard.SetText(text)) ShowActionMessage(done);
        else ShowActionMessage("Copy failed: another app is holding the clipboard.", isError: true);
    }
}
