using FleetMate.Core.Config;
using FleetMate.Core.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Serilog;
using Windows.ApplicationModel.DataTransfer;

namespace FleetMate.WinUI.Shell;

/// <summary>
/// Copy for Agent and Send to Agent, as FleetMate.GUI's
/// <c>AgentContextMenu</c> and <c>AgentContextButton</c> offer them. Copy puts
/// the records' context blocks (<see cref="AgentContextRenderer"/>) on the
/// clipboard. Send pastes them into a running agent's input without pressing
/// Return; with no agent running it copies them and opens the terminal idle,
/// so nothing acts before the person has written their request. TicketsMate
/// has no agent terminal: there Send is absent and the button copies.
///
/// <code>
/// menu.Items.Add(new MenuFlyoutSeparator());
/// foreach (var item in AgentHandOff.MenuItems(() => [AgentContexts.PullRequest(pr)], row)) menu.Items.Add(item);
/// </code>
/// </summary>
public static class AgentHandOff
{
    private static bool TicketsOnly => AppEdition.Current.IsTicketsOnly;

    public static void Copy(IReadOnlyList<AgentContext> contexts, FrameworkElement? anchor)
    {
        if (contexts.Count == 0) return;
        CopyWithPreview(AgentContextRenderer.Render(contexts), anchor);
    }

    public static void Send(IReadOnlyList<AgentContext> contexts, FrameworkElement? anchor)
    {
        if (contexts.Count == 0) return;
        var text = AgentContextRenderer.Render(contexts);
        if (TicketsOnly || App.Current.Window is not { } window) CopyWithPreview(text, anchor);
        else window.SendToAgent(text, anchor);
    }

    /// <summary>Put <paramref name="text"/> on the clipboard and show what was copied beside <paramref name="anchor"/>.</summary>
    public static bool CopyWithPreview(string text, FrameworkElement? anchor)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[agent] Clipboard unavailable");
            return false;
        }
        App.Current.Window?.ShowCopyPreview(text, anchor);
        return true;
    }

    /// <summary>The two menu items, for a row's menu; Copy alone in TicketsMate.</summary>
    public static IEnumerable<MenuFlyoutItem> MenuItems(Func<IReadOnlyList<AgentContext>> contexts, FrameworkElement? anchor)
    {
        var copy = new MenuFlyoutItem { Text = AgentContextLabels.CopyHeader, Icon = new FontIcon { Glyph = AgentContextLabels.CopyGlyph } };
        AutomationProperties.SetAutomationId(copy, "CopyForAgentMenuItem");
        copy.Click += (_, _) => Copy(contexts(), anchor);
        yield return copy;
        if (TicketsOnly) yield break;
        var send = new MenuFlyoutItem { Text = AgentContextLabels.SendHeader, Icon = new FontIcon { Glyph = AgentContextLabels.SendGlyph } };
        AutomationProperties.SetAutomationId(send, "SendToAgentMenuItem");
        ToolTipService.SetToolTip(send, AgentContextLabels.SendToolTip);
        send.Click += (_, _) => Send(contexts(), anchor);
        yield return send;
    }

    /// <summary>Add the two items to the top of <paramref name="menu"/>, with a separator before the rest.</summary>
    public static void Prepend(MenuFlyout menu, Func<IReadOnlyList<AgentContext>> contexts, FrameworkElement? anchor)
    {
        var index = 0;
        foreach (var item in MenuItems(contexts, anchor)) menu.Items.Insert(index++, item);
        if (menu.Items.Count > index) menu.Items.Insert(index, new MenuFlyoutSeparator());
    }

    /// <summary>
    /// A detail view's Agent button: a click sends (copies in TicketsMate),
    /// right-click or Shift+F10 offers both. <paramref name="source"/> builds
    /// the record shown now; null does nothing.
    /// </summary>
    public static void AttachButton(ButtonBase button, Func<AgentContext?> source)
    {
        button.Click += (_, _) =>
        {
            if (source() is not { } context) return;
            if (TicketsOnly) Copy([context], button);
            else Send([context], button);
        };
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            if (source() is not { } context) return;
            foreach (var item in MenuItems(() => [context], button)) menu.Items.Add(item);
        };
        button.ContextFlyout = menu;
        ToolTipService.SetToolTip(button, AgentContextLabels.ButtonToolTip(source()?.Kind, TicketsOnly));
    }

    /// <summary>
    /// Rows in <paramref name="list"/> drag out as their blocks: as plain text,
    /// so they land wherever text goes, and onto the agent terminal, which
    /// hands them over the way Send to Agent does. <paramref name="resolve"/>
    /// gives a row's block, or null for an item that is not a record.
    /// </summary>
    public static void AttachDrag(ListViewBase list, Func<object, AgentContext?> resolve)
    {
        if (TicketsOnly) return;
        list.CanDragItems = true;
        list.DragItemsStarting += (_, e) =>
        {
            var contexts = e.Items.Select(resolve).OfType<AgentContext>().ToList();
            if (contexts.Count == 0) { e.Cancel = true; return; }
            var text = AgentContextRenderer.Render(contexts);
            e.Data.SetText(text);
            e.Data.SetData(AgentContextDragState.Format, text);
            e.Data.RequestedOperation = DataPackageOperation.Copy;
            AgentContextDragState.Begin(text);
        };
        list.DragItemsCompleted += (_, _) => AgentContextDragState.End(DateTime.UtcNow);
    }
}
