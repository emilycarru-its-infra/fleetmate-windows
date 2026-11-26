using System.Globalization;
using FleetMate.Core.Models.Tickets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace FleetMate.WinUI.Pages.Tickets;

/// <summary>
/// One row of the ticket outline, for the list and the board: the ticket,
/// its nesting and its fold state, with the display values a template binds
/// to. The nesting rules are Core's <see cref="TicketHierarchy"/>.
/// </summary>
public sealed class TicketListItem
{
    public TicketListItem(TicketRow row) => Row = row;

    public TicketRow Row { get; }
    public TdxTicket Ticket => Row.Ticket;

    /// <summary>The list's shared column widths.</summary>
    public TicketListLayout Layout => TicketListLayout.Current;
    public int Id => Row.Id;

    public string IdLabel => $"#{Id}";
    public string Title => Ticket.Title ?? "Untitled";
    public string Status => Ticket.StatusName ?? "-";
    public string Priority => Ticket.PriorityName ?? "-";
    public string Requestor => Ticket.RequestorName ?? "-";
    public string Responsible => string.IsNullOrWhiteSpace(Ticket.ResponsibleFullName) ? TicketBoardLayout.Unassigned : Ticket.ResponsibleFullName!;
    public string Group => string.IsNullOrWhiteSpace(Ticket.ResponsibleGroupName) ? TicketBoardLayout.NoGroup : Ticket.ResponsibleGroupName!;
    public string RequestorAndGroup => string.Join(" · ", new[] { Ticket.RequestorName, Ticket.ResponsibleGroupName }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string Age => Ticket.AgeLabel();
    public string Modified => TicketListFormat.Modified(Ticket.ModifiedDate);
    public string Created => TicketListFormat.Created(Ticket.CreatedDate);

    /// <summary>The ID cell's indent: 14 per level, as on Mac, so children sit under their parent.</summary>
    public Thickness Indent => new(Row.Depth * 14, 0, 0, 0);

    /// <summary>A board card's indent, narrower because a column is narrow.</summary>
    public Thickness CardIndent => new(Row.Depth * 12, 0, 0, 0);

    /// <summary>The disclosure chevron: down when open, right when folded.</summary>
    public string DisclosureGlyph => Row.IsExpanded ? "" : "";

    /// <summary>Leaves keep the chevron's room so their titles line up with their siblings'.</summary>
    public double DisclosureOpacity => Row.HasChildren ? 1 : 0;
    public bool IsDisclosureEnabled => Row.HasChildren;

    public string ChildCountLabel => Row.ChildCount.ToString(CultureInfo.CurrentCulture);
    public Visibility ChildBadgeVisibility => Row.HasChildren ? Visibility.Visible : Visibility.Collapsed;

    public string ChildCountText => Row.ChildCount == 1 ? "1 child ticket" : $"{Row.ChildCount} child tickets";

    /// <summary>What the disclosure button does, for its tooltip and screen readers.</summary>
    public string DisclosureName => Row.HasChildren ? (Row.IsExpanded ? "Hide " : "Show ") + ChildCountText : "No child tickets";

    public Visibility SlaVisibility => Ticket.IsSlaViolated ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Two weeks or older reads as waiting, as on Mac.</summary>
    public bool IsAgeHighlighted => TicketListFormat.IsAgeHighlighted(Ticket.AgeInDays());

    public Brush AgeForeground => TicketBrushes.Text(IsAgeHighlighted ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");

    public Brush ResponsibleForeground => TicketBrushes.Text(string.IsNullOrWhiteSpace(Ticket.ResponsibleFullName)
        ? "TextFillColorTertiaryBrush" : "TextFillColorPrimaryBrush");

    public Brush StatusBrush => TicketBrushes.Tone(TicketTones.Status(Status));

    public Brush PriorityBrush => TicketBrushes.Tone(TicketTones.Priority(Priority));

    /// <summary>The row as a screen reader announces it: number, title, status, and a parent's fold state.</summary>
    public string SpokenName
    {
        get
        {
            var name = $"Ticket {Id}, {Title}";
            if (!string.IsNullOrEmpty(Ticket.StatusName)) name += $", {Ticket.StatusName}";
            if (Ticket.IsSlaViolated) name += ", SLA violated";
            if (Row.HasChildren) name += $", {ChildCountText}, {(Row.IsExpanded ? "expanded" : "collapsed")}";
            if (Row.Depth > 0) name += ", child ticket";
            return name;
        }
    }
}

/// <summary>One board column: its key (the drop target), its count and its cards as an outline.</summary>
public sealed class TicketBoardColumnItem
{
    public TicketBoardColumnItem(string key, int count, IReadOnlyList<TicketListItem> cards, Brush dot)
    {
        Key = key;
        Count = count;
        Cards = cards;
        Dot = dot;
    }

    public string Key { get; }
    public int Count { get; }
    public string CountLabel => Count.ToString("N0", CultureInfo.CurrentCulture);
    public IReadOnlyList<TicketListItem> Cards { get; }
    public Brush Dot { get; }
    public string SpokenName => $"{Key}, {Count} {(Count == 1 ? "ticket" : "tickets")}";
}

/// <summary>A run of columns under an optional header: one per responsible group, or one headerless block.</summary>
public sealed class TicketBoardBlockItem
{
    public TicketBoardBlockItem(string? header, IReadOnlyList<TicketBoardColumnItem> columns)
    {
        Header = header;
        Columns = columns;
    }

    public string? Header { get; }
    public string HeaderText => Header ?? "";
    public IReadOnlyList<TicketBoardColumnItem> Columns { get; }
    public string CountLabel => Columns.Sum(c => c.Count).ToString("N0", CultureInfo.CurrentCulture);
    public Visibility HeaderVisibility => Header == null ? Visibility.Collapsed : Visibility.Visible;
    public Thickness Padding => Header == null ? new Thickness(0) : new Thickness(8);
    public Thickness BorderThickness => Header == null ? new Thickness(0) : new Thickness(1);
}

/// <summary>Theme brushes for the Tickets page, looked up so they follow Light, Dark and High Contrast.</summary>
internal static class TicketBrushes
{
    public static Brush Text(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>A tone's brush, from the page's theme dictionaries; High Contrast maps every tone to the system highlight.</summary>
    public static Brush Tone(TicketTone tone) => (Brush)Application.Current.Resources[$"TicketTone{tone}Brush"];
}

/// <summary>
/// The list's column widths, shared by its header and every row so they
/// always line up. Title takes what is left; at narrow widths Requestor,
/// Modified and then Priority drop out rather than crushing it.
/// </summary>
public sealed partial class TicketListLayout : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public static TicketListLayout Current { get; } = new();

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial GridLength RequestorWidth { get; private set; } = new(140);

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial GridLength ModifiedWidth { get; private set; } = new(150);

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial GridLength PriorityWidth { get; private set; } = new(90);

    /// <summary>Fit the columns to the list's width in effective pixels.</summary>
    public void Fit(double width)
    {
        RequestorWidth = new GridLength(width >= 1000 ? 140 : 0);
        ModifiedWidth = new GridLength(width >= 880 ? 150 : 0);
        PriorityWidth = new GridLength(width >= 620 ? 90 : 0);
    }
}
