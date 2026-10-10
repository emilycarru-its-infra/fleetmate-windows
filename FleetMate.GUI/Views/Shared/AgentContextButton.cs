using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FleetMate.Core.Config;
using FleetMate.Core.Shared;
using ModernWpf.Controls;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// The detail views' Agent button. A click does what Send to Agent does:
/// pastes the record's block into a running agent's input without pressing
/// Return, or copies it and opens the terminal when none is running. Its
/// right-click menu offers Copy for Agent and Send to Agent. TicketsMate has
/// no terminal, so there a click copies. The view sets <see cref="Context"/>
/// to the record it shows (with none the button is disabled), or
/// <see cref="Source"/> where the record shown changes in many places.
/// </summary>
public sealed class AgentContextButton : Button
{
    private Func<AgentContext?> _source = () => null;

    public AgentContextButton()
    {
        Content = new FontIcon { Glyph = "", FontSize = 13 };
        AutomationProperties.SetAutomationId(this, "AgentContextButton");
        AutomationProperties.SetName(this, "Agent");
        IsEnabled = false;
        ToolTip = ToolTipFor(null, CopyOnly);
        Click += (_, _) =>
        {
            if (_source() is not { } context) return;
            if (CopyOnly) AgentContextMenu.Copy([context]);
            else AgentContextMenu.Send([context]);
        };
        // Built when it opens, so it always names the record shown now.
        ContextMenu = new ContextMenu();
        ToolTipOpening += (_, _) => ToolTip = ToolTipFor(_source()?.Kind, CopyOnly);
        ContextMenuOpening += (_, e) =>
        {
            if (_source() is not { } context) { e.Handled = true; return; }
            ContextMenu.Items.Clear();
            foreach (var item in AgentContextMenu.Items(() => [context])) ContextMenu.Items.Add(item);
        };
    }

    /// <summary>The record the view shows, or null while it shows none.</summary>
    public AgentContext? Context
    {
        set
        {
            _source = () => value;
            IsEnabled = value != null;
            ToolTip = ToolTipFor(value?.Kind, CopyOnly);
        }
    }

    /// <summary>Builds the record's block when the button is used; a null result does nothing.</summary>
    public Func<AgentContext?> Source
    {
        set
        {
            _source = value;
            IsEnabled = true;
        }
    }

    private static bool CopyOnly => AppEdition.Current.IsTicketsOnly;

    internal static string ToolTipFor(AgentContext.ContextKind? kind, bool copyOnly)
    {
        var noun = kind is { } k ? AgentContext.Label(k).ToLowerInvariant() : "item";
        return copyOnly
            ? $"Copy this {noun} for an agent"
            : $"Send this {noun} to the running agent, or copy it when none is running; right-click to copy";
    }
}
