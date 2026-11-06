using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FleetMate.Core.Models.Tickets;
using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>
/// The List view's table, the macOS app's ticket table: sortable headers,
/// columns that resize and reorder and are kept between launches, and
/// left/right arrows that fold the selected parent.
///
/// The sort itself stays the page's one state, <c>_sortField</c> and
/// <c>_sortAscending</c>, which the toolbar's sort controls also set. A header
/// click sets that state and moves the toolbar controls to match, and every
/// re-sort refreshes the headers, so the two can never disagree.
/// </summary>
public partial class TicketsPage
{
    /// <summary>The header of each table column, which knows its column and sort state.</summary>
    private readonly Dictionary<GridViewColumn, TicketColumnHeader> _columnHeaders = new();

    /// <summary>Saves the layout once a drag settles rather than on every pixel of it.</summary>
    private DispatcherTimer? _columnSaveTimer;

    /// <summary>Set while the saved layout is applied, so applying it is not read as a change.</summary>
    private bool _restoringColumns;

    /// <summary>
    /// Each column's own width: the saved or default one, then whatever a
    /// person drags it to. Fitting the table to the pane shows narrower widths
    /// without touching these, and these are what is saved.
    /// </summary>
    private readonly Dictionary<TicketListColumn, double> _desiredColumnWidths = new();

    /// <summary>Set while fitting sets widths, so a fit is not read as a drag.</summary>
    private bool _fittingColumns;

    /// <summary>Refits once a Title drag settles; Title always fills, so its drag snaps back.</summary>
    private DispatcherTimer? _columnFitTimer;

    /// <summary>
    /// Width the rows add around the columns (the item's padding and the
    /// header row's margin), with a little to spare so rounding never brings
    /// up a horizontal scroll bar.
    /// </summary>
    private const double TicketRowChromeWidth = 14;

    private static readonly SolidColorBrush UnknownStatusBrush = Frozen(Color.FromRgb(0x80, 0x80, 0x80));

    private GridView TicketsGrid => (GridView)TicketsListView.View;

    /// <summary>The status dot colour for a row, the board's status colours.</summary>
    internal static Brush StatusBrush(string? status) =>
        status != null && StatusColors.TryGetValue(status, out var brush) ? brush : UnknownStatusBrush;

    private void InitListTable()
    {
        foreach (var gridColumn in TicketsGrid.Columns)
        {
            if (TicketListColumns.FromHeader(gridColumn.Header as string) is not { } column) continue;
            var header = new TicketColumnHeader(column);
            _columnHeaders[gridColumn] = header;
            gridColumn.Header = header;
        }

        TicketsGrid.ColumnHeaderContainerStyle = BuildColumnHeaderStyle();
        RestoreColumnLayout();

        TicketsGrid.Columns.CollectionChanged += OnTicketColumnsChanged;
        foreach (INotifyPropertyChanged gridColumn in TicketsGrid.Columns)
            gridColumn.PropertyChanged += OnTicketColumnPropertyChanged;

        TicketsListView.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnTicketColumnHeaderClicked));
        TicketsListView.PreviewKeyDown += OnTicketListKeyDown;

        // The vertical scroll bar coming and going changes the viewport as
        // much as resizing the pane does, so both refit.
        TicketsListView.SizeChanged += (_, _) => FitTicketColumns();
        TicketsListView.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.ViewportWidthChange != 0) FitTicketColumns();
        }));

        UpdateColumnHeaders();
    }

    /// <summary>
    /// Left-aligned headers that tell a screen reader their sort state. Based
    /// on the theme's own header style, looked up rather than named in XAML so
    /// a theme without one degrades to the stock header instead of failing to load.
    /// </summary>
    private Style BuildColumnHeaderStyle()
    {
        var style = new Style(typeof(GridViewColumnHeader), TryFindResource(typeof(GridViewColumnHeader)) as Style);
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        style.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding("Content.SpokenName")
        {
            RelativeSource = RelativeSource.Self,
        }));
        style.Setters.Add(new Setter(AutomationProperties.HelpTextProperty,
            "Sorts the list by this column. Activate again to reverse the order."));
        return style;
    }

    // ---- Sorting ----

    private void OnTicketColumnHeaderClicked(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Role: GridViewColumnHeaderRole.Normal, Column: { } gridColumn }
            || !_columnHeaders.TryGetValue(gridColumn, out var header))
            return;

        var (field, ascending) = TicketListSort.AfterHeaderClick(_sortField, _sortAscending, header.Column);
        SetSortState(field, ascending);
        e.Handled = true;
    }

    /// <summary>
    /// Set the page's sort and move the toolbar's sort controls to match.
    /// Selecting the field in the toolbar re-sorts through its own handler, so
    /// the list is sorted once either way.
    /// </summary>
    private void SetSortState(string field, bool ascending)
    {
        _sortAscending = ascending;
        SortDirectionButton.Content = ascending ? "↑" : "↓";

        var item = SortComboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Content as string, field, StringComparison.Ordinal));
        if (item != null && !ReferenceEquals(SortComboBox.SelectedItem, item))
        {
            SortComboBox.SelectedItem = item;
            if (_sortField == field) return;
        }

        _sortField = field;
        ApplyFiltersAndSort();
    }

    /// <summary>Show the chevron on the sorted column, and only there.</summary>
    private void UpdateColumnHeaders()
    {
        foreach (var header in _columnHeaders.Values) header.Update(_sortField, _sortAscending);
    }

    // ---- Layout persistence ----

    private void RestoreColumnLayout()
    {
        _restoringColumns = true;
        try
        {
            var layout = TicketListColumnLayout.Parse(UserPreferences.TicketsListColumns);
            var byColumn = _columnHeaders.ToDictionary(p => p.Value.Column, p => p.Key);
            var columns = TicketsGrid.Columns;

            var index = 0;
            foreach (var column in layout.Arrange())
            {
                if (!byColumn.TryGetValue(column, out var gridColumn)) continue;
                var current = columns.IndexOf(gridColumn);
                if (current != index) columns.Move(current, index);
                _desiredColumnWidths[column] = layout.WidthOf(column);
                gridColumn.Width = _desiredColumnWidths[column];
                index++;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Tickets: could not restore the list's column layout");
        }
        finally
        {
            _restoringColumns = false;
        }
    }

    private void OnTicketColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleColumnSave();

    /// <summary>
    /// A width change that fitting did not make is a person dragging a header
    /// edge. Any column but Title keeps the dragged width, and the rest make
    /// room for it as it moves. Title is the column that fills, so a Title drag
    /// is refit once it settles.
    /// </summary>
    private void OnTicketColumnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GridViewColumn.Width) || _fittingColumns || _restoringColumns) return;
        if (sender is not GridViewColumn gridColumn || !_columnHeaders.TryGetValue(gridColumn, out var header)) return;

        if (header.Column == TicketListColumn.Title)
        {
            ScheduleColumnFit();
            return;
        }

        if (double.IsFinite(gridColumn.Width) && gridColumn.Width > 0)
            _desiredColumnWidths[header.Column] = Math.Max(TicketListColumns.MinWidth, gridColumn.Width);
        FitTicketColumns(held: header.Column);
        ScheduleColumnSave();
    }

    private void ScheduleColumnFit()
    {
        if (_columnFitTimer == null)
        {
            _columnFitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _columnFitTimer.Tick += (_, _) =>
            {
                _columnFitTimer.Stop();
                FitTicketColumns();
            };
        }
        _columnFitTimer.Stop();
        _columnFitTimer.Start();
    }

    /// <summary>
    /// Show the columns at the widths that fit the pane: Title fills what the
    /// others leave, and they give way when the pane is narrow. See
    /// <see cref="TicketListColumnFit"/>.
    /// </summary>
    private void FitTicketColumns(TicketListColumn? held = null)
    {
        if (_columnHeaders.Count == 0 || _desiredColumnWidths.Count == 0) return;

        var fit = TicketListColumnFit.Fit(_desiredColumnWidths, TicketListViewportWidth() - TicketRowChromeWidth, held);
        _fittingColumns = true;
        try
        {
            foreach (var (gridColumn, header) in _columnHeaders)
            {
                if (header.Column == held) continue;
                if (fit.TryGetValue(header.Column, out var width) && Math.Abs(gridColumn.Width - width) >= 0.5)
                    gridColumn.Width = width;
            }
        }
        finally
        {
            _fittingColumns = false;
        }
    }

    /// <summary>The width rows are laid out in: the list less its vertical scroll bar, when shown.</summary>
    private double TicketListViewportWidth()
    {
        var scroller = FindDescendant<ScrollViewer>(TicketsListView);
        if (scroller is { ViewportWidth: > 0 }) return scroller.ViewportWidth;
        return TicketsListView.ActualWidth - SystemParameters.VerticalScrollBarWidth;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void ScheduleColumnSave()
    {
        if (_restoringColumns) return;
        if (_columnSaveTimer == null)
        {
            _columnSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _columnSaveTimer.Tick += (_, _) =>
            {
                _columnSaveTimer.Stop();
                SaveColumnLayout();
            };
        }
        _columnSaveTimer.Stop();
        _columnSaveTimer.Start();
    }

    private void SaveColumnLayout()
    {
        try
        {
            // The widths saved are each column's own, not the narrower ones a
            // small pane shows, so a wider window brings them back.
            var layout = TicketListColumnLayout.Capture(TicketsGrid.Columns
                .Where(_columnHeaders.ContainsKey)
                .Select(c => (_columnHeaders[c].Column,
                    _desiredColumnWidths.TryGetValue(_columnHeaders[c].Column, out var width)
                        ? width
                        : double.IsNaN(c.Width) ? c.ActualWidth : c.Width)));
            UserPreferences.SetTicketsListColumns(layout.Serialize());
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Tickets: could not save the list's column layout");
        }
    }

    // ---- Keyboard folding ----

    /// <summary>
    /// Right unfolds the selected parent, left folds it, and left on a child
    /// moves to its parent, as in a Finder or Explorer outline. Anything else,
    /// and an arrow with nothing to fold, scrolls as usual.
    /// </summary>
    private void OnTicketListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right) || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.OriginalSource is TextBoxBase) return;
        if (TicketsListView.SelectedItem is not TicketRowViewModel row) return;

        if (row.Row.HasChildren && row.Row.IsExpanded == (e.Key == Key.Left))
        {
            if (e.Key == Key.Left) _collapsedTickets.Add(row.Id);
            else _collapsedTickets.Remove(row.Id);
            RebuildOutline();
            FocusSelectedTicketRow();
            e.Handled = true;
        }
        else if (e.Key == Key.Left && row.Row.Depth > 0
                 && TicketHierarchy.ParentTicketId(row.Ticket) is { } parentId
                 && _ticketRows.FirstOrDefault(r => r.Id == parentId) is { } parent)
        {
            TicketsListView.SelectedItem = parent;
            FocusSelectedTicketRow();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Put keyboard focus back on the selected row. Rebuilding the rows
    /// replaces their containers, and without this the next arrow key would go
    /// nowhere.
    /// </summary>
    private void FocusSelectedTicketRow()
    {
        if (TicketsListView.SelectedItem is not { } selected) return;
        TicketsListView.ScrollIntoView(selected);
        TicketsListView.UpdateLayout();
        (TicketsListView.ItemContainerGenerator.ContainerFromItem(selected) as ListViewItem)?.Focus();
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// One table header: its title, the chevron when the list is sorted by it, and
/// what a screen reader says for it.
/// </summary>
public sealed class TicketColumnHeader : INotifyPropertyChanged
{
    /// <summary>Segoe chevrons: up for ascending, down for descending, as on Mac.</summary>
    private const string AscendingGlyph = "";
    private const string DescendingGlyph = "";

    public TicketColumnHeader(TicketListColumn column)
    {
        Column = column;
        Title = TicketListColumns.Header(column);
        SpokenName = TicketListSort.SpokenHeader(column, null, false);
    }

    public TicketListColumn Column { get; }
    public string Title { get; }
    public string SortGlyph { get; private set; } = "";
    public string SpokenName { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(string? sortField, bool ascending)
    {
        var sorted = TicketListSort.ColumnFor(sortField) == Column;
        Set(sorted ? (ascending ? AscendingGlyph : DescendingGlyph) : "", v => SortGlyph = v, SortGlyph, nameof(SortGlyph));
        Set(TicketListSort.SpokenHeader(Column, sortField, ascending), v => SpokenName = v, SpokenName, nameof(SpokenName));
    }

    public override string ToString() => Title;

    private void Set(string value, Action<string> assign, string current, string name)
    {
        if (value == current) return;
        assign(value);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
