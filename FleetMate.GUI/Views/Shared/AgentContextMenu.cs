using System.Collections;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Models.Reporting;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Shared;
using FleetMate.GUI.ViewModels.Manage;
using FleetMate.GUI.Views.Development;
using FleetMate.GUI.Views.Development.Repos;
using FleetMate.GUI.Views.Identity;
using FleetMate.GUI.Views.Projects;
using FleetMate.GUI.Views.Tickets;
using ModernWpf.Controls;

namespace FleetMate.GUI.Views.Shared;

/// <summary>Puts agent context blocks on the clipboard, retrying while another app holds it.</summary>
public static class AgentContextClipboard
{
    public static bool SetText(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (COMException) { Thread.Sleep(30); }
            catch (ExternalException) { Thread.Sleep(30); }
        }
        return false;
    }
}

/// <summary>
/// "Copy for Agent" and "Send to Agent" on the rows of every module. Copy puts
/// the rows' context blocks on the clipboard. Send pastes them into a running
/// agent's input without pressing Return; with no agent running it copies them
/// and opens the terminal idle, so nothing acts before the person has written
/// their request.
/// </summary>
public static class AgentContextMenu
{
    private const string AgentTag = "AgentContextMenuItem";

    // ── Actions ──────────────────────────────────────────────────────────

    public static void Copy(IReadOnlyList<AgentContext> contexts)
    {
        if (contexts.Count == 0) return;
        AgentContextClipboard.SetText(AgentContextRenderer.Render(contexts));
    }

    public static void Send(IReadOnlyList<AgentContext> contexts)
    {
        if (contexts.Count == 0) return;
        var text = AgentContextRenderer.Render(contexts);
        if (Application.Current?.MainWindow is MainWindow window) window.SendToAgent(text);
        else AgentContextClipboard.SetText(text);
    }

    /// <summary>The two menu items, for a menu built in code.</summary>
    public static IEnumerable<MenuItem> Items(Func<IReadOnlyList<AgentContext>> contexts)
    {
        var copy = new MenuItem { Header = "Copy for Agent", Icon = new FontIcon { Glyph = "" }, Tag = AgentTag };
        AutomationProperties.SetAutomationId(copy, "CopyForAgentMenuItem");
        copy.Click += (_, _) => Copy(contexts());
        yield return copy;
        if (AppEdition.Current.IsTicketsOnly) yield break;
        var send = new MenuItem { Header = "Send to Agent", Icon = new FontIcon { Glyph = "" }, Tag = AgentTag };
        AutomationProperties.SetAutomationId(send, "SendToAgentMenuItem");
        send.ToolTip = "Paste into the running agent's input without pressing Return; with none running, copy and open the terminal";
        send.Click += (_, _) => Send(contexts());
        yield return send;
    }

    // ── Rows ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Give the rows inside <paramref name="host"/> the two items on right
    /// click. The clicked row is found from its data (or Tag); when it is
    /// part of a multiple selection, every selected row goes. A row that
    /// already has a menu gets the items at its top; one without gets a menu
    /// of its own. <paramref name="extra"/> resolves what only the page can,
    /// such as a file in the open repository; it is tried before the
    /// app-wide types.
    /// </summary>
    public static void Attach(FrameworkElement host, Func<object, AgentContext?>? extra = null)
    {
        host.PreviewMouseRightButtonUp += (_, e) => OnRightClick(host, e, extra);
    }

    private static void OnRightClick(FrameworkElement host, MouseButtonEventArgs e, Func<object, AgentContext?>? extra)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        AgentContext? Build(object? o) => o == null ? null : extra?.Invoke(o) ?? Resolve(o);

        // Walk up to the host; the first element whose data (or Tag) is a
        // record wins. An item container ends the walk, so a row nested in
        // another row is never taken for its parent.
        object? item = null;
        AgentContext? clicked = null;
        ContextMenu? owner = null;
        for (var node = source; node != null && clicked == null; node = Parent(node))
        {
            if (node is FrameworkElement { ContextMenu: { } menu } && owner == null) owner = menu;
            if (node is FrameworkElement element)
            {
                foreach (var candidate in new object?[] { element, element.Tag, element.DataContext })
                {
                    if (candidate == null || (candidate == element.DataContext && candidate == host.DataContext)) continue;
                    if (Build(candidate) is { } built) { item = candidate; clicked = built; break; }
                }
            }
            if (clicked == null && node is ListBoxItem or DataGridRow or TreeViewItem) return;
            if (ReferenceEquals(node, host)) break;
        }
        if (clicked == null || item == null) return;
        // Keep walking to the host for the menu that will open, if the row's own has not been met.
        if (owner == null)
            for (var node = source; node != null; node = Parent(node))
            {
                if (node is FrameworkElement { ContextMenu: { } menu }) { owner = menu; break; }
                if (ReferenceEquals(node, host)) break;
            }

        var targets = SelectedPeers(source, host, item, Build) ?? new List<AgentContext> { clicked };

        if (owner != null)
        {
            foreach (var old in owner.Items.OfType<Control>().Where(c => Equals(c.Tag, AgentTag)).ToList())
                owner.Items.Remove(old);
            var index = 0;
            foreach (var menuItem in Items(() => targets)) owner.Items.Insert(index++, menuItem);
            if (owner.Items.Count > index) owner.Items.Insert(index, new Separator { Tag = AgentTag });
            return;
        }

        var own = new ContextMenu { PlacementTarget = source as UIElement ?? host, Placement = PlacementMode.MousePoint };
        foreach (var menuItem in Items(() => targets)) own.Items.Add(menuItem);
        own.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// Every selected row's block when the clicked row is one of several
    /// selected in the nearest list, else null.
    /// </summary>
    private static List<AgentContext>? SelectedPeers(DependencyObject source, FrameworkElement host, object item,
        Func<object?, AgentContext?> build)
    {
        for (var node = source; node != null; node = Parent(node))
        {
            IList? selected = node switch
            {
                MultiSelector grid => grid.SelectedItems,
                ListBox list when list.SelectionMode != SelectionMode.Single => list.SelectedItems,
                _ => null,
            };
            if (node is Selector)
            {
                if (selected is not { Count: > 1 } || !selected.Contains(item)) return null;
                return selected.Cast<object>().Select(build).OfType<AgentContext>().ToList();
            }
            if (ReferenceEquals(node, host)) break;
        }
        return null;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
        : LogicalTreeHelper.GetParent(node);

    // ── What each row is ─────────────────────────────────────────────────

    /// <summary>The agent block for a row's data, for every record type the app lists; null for anything else.</summary>
    public static AgentContext? Resolve(object data)
    {
        var config = (Application.Current as App)?.Config;
        return data switch
        {
            TaskCardVm card => AgentContexts.WorkItem(card.Task),
            UnifiedTask task => AgentContexts.WorkItem(task),
            WorkItem workItem => AgentContexts.WorkItem(workItem.AsUnifiedTask()),
            DevelopmentPullRequestRowViewModel row => AgentContexts.PullRequest(row.PullRequest),
            PullRequestRowViewModel row => AgentContexts.PullRequest(row.PullRequest),
            UnifiedPullRequest pr => AgentContexts.PullRequest(pr),
            CommitRowViewModel row => AgentContexts.Commit(row.Commit, row.Repository),
            PipelineRunRowViewModel row => AgentContexts.PipelineRun(row.Run),
            PipelineRun run => AgentContexts.PipelineRun(run),
            SidebarRepoRow row => AgentContexts.Repository(row.Record),
            DeviceListRow row => AgentContexts.Device(row),
            EntraUserViewModel vm => AgentContexts.User(vm.User),
            EntraUser user => AgentContexts.User(user),
            EntraGroup group => AgentContexts.Group(group),
            SnipeAsset asset => AgentContexts.Asset(asset, config?.SnipeUrl),
            TicketRowViewModel row => Ticket(row.Ticket, config),
            TdxTicket ticket => Ticket(ticket, config),
            MachineRowViewModel row => AgentContexts.ManageTarget(row.Computer, row.HasAddress ? row.Ip : null),
            ReportingDevice device => AgentContexts.ReportingDevice(device),
            _ => null,
        };
    }

    private static AgentContext Ticket(TdxTicket ticket, FleetMateConfig? config) =>
        AgentContexts.Ticket(ticket, ticket.Id > 0 ? config?.Tdx?.GetTicketWebUrl(ticket.Id) : null);
}
