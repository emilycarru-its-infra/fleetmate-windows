using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>
/// The one-row Tickets toolbar: which controls each view shows, the labels
/// that drop to icons in a narrow window, and the sort state that the board's
/// sort controls and the list's column headers share.
/// </summary>
public partial class TicketsPage
{
    private bool _syncingSortControls;
    private bool _compactToolbar;

    private TicketsViewMode CurrentViewMode => TicketsToolbarRules.ModeOf(_isBoardView, _isInboxView);

    /// <summary>Show the controls that mean something in the current view.</summary>
    private void UpdateToolbarForView()
    {
        var mode = CurrentViewMode;
        BoardGroupComboBox.Visibility = VisibleIf(TicketsToolbarRules.ShowsBoardGrouping(mode));
        SortControls.Visibility = VisibleIf(TicketsToolbarRules.ShowsSortControls(mode));
        FoldControls.Visibility = VisibleIf(TicketsToolbarRules.ShowsFoldControls(mode));
        UpdateToolbarLabels();
    }

    private void OnToolbarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) UpdateToolbarLabels();
    }

    /// <summary>Icons only when the toolbar is narrow; the tooltips and accessible names keep the words.</summary>
    private void UpdateToolbarLabels()
    {
        var compact = TicketsToolbarRules.UsesCompactLabels(TicketsToolbar.ActualWidth, CurrentViewMode);
        var labels = VisibleIf(!compact);
        AssignedToMeLabel.Visibility = labels;
        DetailPanelLabel.Visibility = labels;
        NewTicketLabel.Visibility = labels;
        RefreshLabel.Visibility = labels;
        if (compact == _compactToolbar) return;
        _compactToolbar = compact;
        UpdateFilterButtons();
    }

    /// <summary>A vertical wheel over the filter strip scrolls it sideways once it overflows.</summary>
    private void OnFilterStripMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // A dropdown's open list routes its wheel through here too; leave it be.
        if (e.OriginalSource is not DependencyObject source || !FilterStripScroller.IsAncestorOf(source)) return;
        if (FilterStripScroller.ScrollableWidth <= 0) return;
        FilterStripScroller.ScrollToHorizontalOffset(FilterStripScroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>
    /// The one way to change the ticket sort: the board's sort controls and the
    /// list's column headers both come here, so they always agree.
    /// </summary>
    internal void SetTicketSort(string field, bool ascending)
    {
        _sortField = field;
        _sortAscending = ascending;
        SyncSortControls();
        ApplyFiltersAndSort();
    }

    /// <summary>Make the sort dropdown and direction button show the current sort.</summary>
    private void SyncSortControls()
    {
        _syncingSortControls = true;
        try
        {
            var item = SortComboBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Content?.ToString(), _sortField, StringComparison.Ordinal));
            if (item != null && !ReferenceEquals(SortComboBox.SelectedItem, item)) SortComboBox.SelectedItem = item;
        }
        finally
        {
            _syncingSortControls = false;
        }
        UpdateSortDirectionButton();
    }

    /// <summary>The arrow points the way of the sort and the bars grow or shrink with it.</summary>
    private void UpdateSortDirectionButton()
    {
        // Segoe Fluent Icons Up (E74A) and Down (E74B).
        SortDirectionIcon.Glyph = ((char)(_sortAscending ? 0xE74A : 0xE74B)).ToString();
        var widths = TicketsToolbarRules.SortBarWidths(_sortAscending);
        SortBarTop.Width = widths[0];
        SortBarMiddle.Width = widths[1];
        SortBarBottom.Width = widths[2];
        SortDirectionButton.ToolTip = TicketsToolbarRules.SortDirectionToolTip(_sortAscending);
        AutomationProperties.SetItemStatus(SortDirectionButton, TicketsToolbarRules.SortDirectionName(_sortAscending));
    }

    private static Visibility VisibleIf(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
