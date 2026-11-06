using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using FleetMate.GUI.Views.Shared;
using Serilog;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>
/// The Filters button and its popover, as on Mac: categories on the left,
/// the chosen one's values on the right, and a Time Period section holding
/// the created-date window and Show Closed. The widgets change the same
/// <see cref="TicketFilters"/>. Also the Assigned to Me toggle.
/// </summary>
public partial class TicketsPage
{
    private readonly Dictionary<TicketFilterSection, (ListBoxItem Item, TextBlock Title, Border Badge, TextBlock BadgeText)> _filterSectionRows = new();
    private readonly List<CheckBox> _filterValueBoxes = new();
    private TicketFilterSection _filterSection = TicketFiltersPopoverRules.Sections[0];
    private DateTime _filtersPopupClosedAt;
    private bool _assignedToMe;

    private bool IsDefaultDateRange => (_app?.TicketDatePreset ?? TicketDateRangePreset.CurrentTerm) == TicketDateRangePreset.CurrentTerm;

    /// <summary>Build the popover's category rows once; their counts update in place.</summary>
    private void BuildFilterButtons()
    {
        FilterSectionsList.Items.Clear();
        _filterSectionRows.Clear();

        foreach (var section in TicketFiltersPopoverRules.Sections)
        {
            var badgeText = new TextBlock
            {
                Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var badge = new Border
            {
                Child = badgeText, MinWidth = 16, Height = 16, Padding = new Thickness(4, 0, 4, 0),
                CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            badge.SetResourceReference(Border.BackgroundProperty, "SystemControlHighlightAccentBrush");
            DockPanel.SetDock(badge, Dock.Right);
            var title = new TextBlock { Text = section.Title, VerticalAlignment = VerticalAlignment.Center };
            var row = new DockPanel { LastChildFill = true };
            row.Children.Add(badge);
            row.Children.Add(title);

            var item = new ListBoxItem { Content = row, Tag = section, Padding = new Thickness(8, 5, 8, 5) };
            _filterSectionRows[section] = (item, title, badge, badgeText);
            FilterSectionsList.Items.Add(item);
        }

        FilterSectionsList.SelectedItem = _filterSectionRows[_filterSection].Item;
        UpdateFilterButtons();
    }

    /// <summary>
    /// The Filters button's badge and name, Clear Filters, Clear All, and each
    /// category's count. While the popover is open its values follow too.
    /// </summary>
    private void UpdateFilterButtons()
    {
        if (FiltersButton == null) return;
        var defaultDate = IsDefaultDateRange;
        var active = TicketFiltersPopoverRules.ActiveCount(_filters, _showClosed, defaultDate);

        var badge = TicketFiltersPopoverRules.BadgeText(active);
        FiltersBadgeText.Text = badge;
        FiltersBadge.Visibility = badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        FiltersButton.FontWeight = active > 0 ? FontWeights.SemiBold : FontWeights.Normal;
        FiltersButton.ToolTip = TicketFiltersPopoverRules.ButtonToolTip(active);
        AutomationProperties.SetName(FiltersButton, TicketFiltersPopoverRules.ButtonName(active));

        var clear = TicketFiltersPopoverRules.ShowsClearAll(active) ? Visibility.Visible : Visibility.Collapsed;
        ClearFiltersButton.Visibility = clear;
        FiltersClearAllButton.Visibility = clear;

        foreach (var (section, row) in _filterSectionRows)
        {
            var count = TicketFiltersPopoverRules.SectionCount(section, _filters, _showClosed, defaultDate);
            row.BadgeText.Text = TicketFiltersPopoverRules.BadgeText(count);
            row.Badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            row.Title.FontWeight = count > 0 ? FontWeights.SemiBold : FontWeights.Normal;
            AutomationProperties.SetName(row.Item, TicketFiltersPopoverRules.SectionName(section.Title, count));
        }

        if (FiltersPopup.IsOpen) RefreshFilterValues();
    }

    private void OnFiltersButtonClicked(object sender, RoutedEventArgs e)
    {
        // A click on the button while the popover is open closes it on the
        // way down; don't let the same click open it again.
        if ((DateTime.UtcNow - _filtersPopupClosedAt).TotalMilliseconds < 250) return;
        FiltersPopup.IsOpen = true;
    }

    private void OnFiltersPopupOpened(object? sender, EventArgs e)
    {
        FilterValueSearchBox.Text = "";
        ShowFilterSection(_filterSection, rebuild: true);
        Dispatcher.BeginInvoke(() => Keyboard.Focus(_filterSectionRows[_filterSection].Item), DispatcherPriority.Input);
    }

    private void OnFiltersPopupClosed(object? sender, EventArgs e) => _filtersPopupClosedAt = DateTime.UtcNow;

    /// <summary>Esc closes the popover and puts focus back on the button, unless the date list is open.</summary>
    private void OnFiltersPopoverKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DateRangeComboBox.IsDropDownOpen) return;
        FiltersPopup.IsOpen = false;
        Keyboard.Focus(FiltersButton);
        e.Handled = true;
    }

    private void OnFilterSectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilterSectionsList.SelectedItem is not ListBoxItem { Tag: TicketFilterSection section }) return;
        if (section == _filterSection) return;
        _filterSection = section;
        FilterValueSearchBox.Text = "";
        ShowFilterSection(section, rebuild: true);
    }

    private void OnFilterValueSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (FiltersPopup.IsOpen) RefreshFilterValues(rebuild: true);
    }

    private void ShowFilterSection(TicketFilterSection section, bool rebuild)
    {
        FilterSectionsList.SelectedItem = _filterSectionRows[section].Item;
        FilterTimePeriodPanel.Visibility = section.IsTimePeriod ? Visibility.Visible : Visibility.Collapsed;
        FilterValuesPanel.Visibility = section.IsTimePeriod ? Visibility.Collapsed : Visibility.Visible;
        RefreshFilterValues(rebuild);
    }

    /// <summary>
    /// The chosen category's values as checkboxes. Rebuilt when the category,
    /// the search or the values themselves change; otherwise only the ticks
    /// follow the selection, so focus stays on the box just toggled.
    /// </summary>
    private void RefreshFilterValues(bool rebuild = false)
    {
        if (_filterSection.Category is not { } category)
        {
            FilterValuesList.Children.Clear();
            _filterValueBoxes.Clear();
            return;
        }

        var selected = _filters.Selected(category);
        var available = TicketFilters.AvailableValues(_allTickets, category, DateTime.UtcNow);
        var values = TicketFiltersPopoverRules.Search(
            TicketFiltersPopoverRules.Values(available, selected), FilterValueSearchBox.Text);

        if (!rebuild && values.SequenceEqual(_filterValueBoxes.Select(b => (string)b.Tag)))
        {
            foreach (var box in _filterValueBoxes)
                box.IsChecked = selected.Contains((string)box.Tag);
            return;
        }

        FilterValuesList.Children.Clear();
        _filterValueBoxes.Clear();
        foreach (var value in values)
        {
            var box = new CheckBox
            {
                Content = value, Tag = value, IsChecked = selected.Contains(value),
                MinWidth = 0, Margin = new Thickness(0, 2, 0, 2),
            };
            AutomationProperties.SetName(box, value);
            box.Click += (_, _) => _filters.Toggle(category, value);
            _filterValueBoxes.Add(box);
            FilterValuesList.Children.Add(box);
        }
        FilterNoValuesText.Visibility = values.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFiltersClearAllClicked(object sender, RoutedEventArgs e)
    {
        ClearAllFilters();
        Keyboard.Focus(_filterSectionRows[_filterSection].Item);
    }

    private void OnClearFiltersClicked(object sender, RoutedEventArgs e) => ClearAllFilters();

    /// <summary>
    /// Every category cleared, closed tickets hidden and the date window back
    /// to the current term, as the Mac's Clear Filters does.
    /// </summary>
    private void ClearAllFilters()
    {
        _filters.ClearAll();
        ShowClosedCheckBox.IsChecked = false;
        if (DateRangeComboBox.ItemsSource is IEnumerable<DateRangeOption> options
            && options.FirstOrDefault(o => o.Preset == TicketDateRangePreset.CurrentTerm) is { } term)
            DateRangeComboBox.SelectedItem = term;
        UpdateFilterButtons();
    }

    // MARK: - Assigned to Me

    private void OnAssignedToMeChanged(object sender, RoutedEventArgs e)
    {
        var on = AssignedToMeToggle.IsChecked == true;
        if (on == _assignedToMe) return;
        _assignedToMe = on;
        UserPreferences.SetTicketsAssignedToMe(on);
        // My tickets read best as a list, so turning the filter on switches to
        // it, as the Mac app does. Switching back to Board stays possible.
        if (TicketsToolbarRules.ViewAfterAssignedToMe(CurrentViewMode, on) == TicketsViewMode.List
            && ListViewRadio.IsChecked != true)
            ListViewRadio.IsChecked = true;
        ApplyFiltersAndSort();
    }

    /// <summary>Find the signed-in TDX person, so Assigned to Me has someone to match.</summary>
    private async Task ResolveMeAsync()
    {
        if (_tdxService == null) return;
        try
        {
            _me = await _tdxService.GetMeAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[tickets] Could not resolve the signed-in TDX person");
            _me = null;
        }

        UpdateAssignedToMeToggle();
        if (_assignedToMe) ApplyFiltersAndSort();
        OnInboxPersonChanged();
    }

    private void UpdateAssignedToMeToggle()
    {
        AssignedToMeToggle.IsEnabled = _me != null;
        AssignedToMeToggle.ToolTip = _me == null
            ? "Waiting to identify you in TeamDynamix"
            : "Show only tickets you are responsible for";
    }
}
