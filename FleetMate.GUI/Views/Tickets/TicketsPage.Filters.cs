using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FleetMate.Core.Models.Tickets;
using FleetMate.GUI.Views.Shared;
using Serilog;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>
/// The filter row (one dropdown of checkable values per category) and the
/// Assigned to Me toggle. The widgets change the same <see cref="TicketFilters"/>.
/// </summary>
public partial class TicketsPage
{
    private static readonly TicketFilterCategory[] FilterCategories =
    {
        TicketFilterCategory.Status, TicketFilterCategory.Priority, TicketFilterCategory.Group,
        TicketFilterCategory.Responsible, TicketFilterCategory.Age, TicketFilterCategory.Sla,
    };

    private readonly Dictionary<TicketFilterCategory, Button> _filterButtons = new();
    private Button? _clearFiltersButton;
    private bool _assignedToMe;

    private void BuildFilterButtons()
    {
        FilterButtonsPanel.Children.Clear();
        _filterButtons.Clear();

        foreach (var category in FilterCategories)
        {
            var button = new Button { Margin = new Thickness(0, 0, 8, 4), Padding = new Thickness(10, 4, 10, 4) };
            button.Click += (_, _) => OpenFilterMenu(category, button);
            _filterButtons[category] = button;
            FilterButtonsPanel.Children.Add(button);
        }

        _clearFiltersButton = new Button
        {
            Content = "Clear Filters", Margin = new Thickness(0, 0, 8, 4), Padding = new Thickness(10, 4, 10, 4),
            ToolTip = "Clear every filter",
        };
        _clearFiltersButton.Click += (_, _) => _filters.ClearAll();
        FilterButtonsPanel.Children.Add(_clearFiltersButton);

        UpdateFilterButtons();
    }

    /// <summary>Each button names its category and what is picked in it.</summary>
    private void UpdateFilterButtons()
    {
        foreach (var (category, button) in _filterButtons)
        {
            var picked = _filters.Selected(category);
            var title = TicketFilters.Title(category);
            button.Content = picked.Count switch
            {
                0 => $"{title}: All ▾",
                1 => $"{title}: {picked.First()} ▾",
                _ => $"{title}: {picked.Count} selected ▾",
            };
            button.FontWeight = picked.Count > 0 ? FontWeights.SemiBold : FontWeights.Normal;
            button.ToolTip = picked.Count > 1 ? string.Join(", ", picked.OrderBy(p => p)) : $"Filter by {title.ToLowerInvariant()}";
        }

        if (_clearFiltersButton != null)
            _clearFiltersButton.Visibility = _filters.HasActiveFilters ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// A menu of the category's values, each checkable; it stays open so
    /// several can be picked in one go.
    /// </summary>
    private void OpenFilterMenu(TicketFilterCategory category, Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var values = TicketFilters.AvailableValues(_allTickets, category, DateTime.UtcNow);

        // A selection that no longer matches any loaded ticket is still listed, so it can be cleared.
        foreach (var stale in _filters.Selected(category).Where(v => !values.Contains(v)).OrderBy(v => v).ToList())
            values.Add(stale);

        var all = new MenuItem { Header = "All", IsCheckable = true, IsChecked = _filters.Selected(category).Count == 0 };
        all.Click += (_, _) => _filters.Clear(category);
        menu.Items.Add(all);
        menu.Items.Add(new Separator());

        if (values.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No values", IsEnabled = false });

        foreach (var value in values)
        {
            var item = new MenuItem
            {
                Header = value, IsCheckable = true, StaysOpenOnClick = true,
                IsChecked = _filters.IsSelected(category, value),
            };
            item.Click += (_, _) =>
            {
                _filters.Toggle(category, value);
                all.IsChecked = _filters.Selected(category).Count == 0;
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    // MARK: - Assigned to Me

    private void OnAssignedToMeChanged(object sender, RoutedEventArgs e)
    {
        var on = AssignedToMeToggle.IsChecked == true;
        if (on == _assignedToMe) return;
        _assignedToMe = on;
        UserPreferences.SetTicketsAssignedToMe(on);
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
    }

    private void UpdateAssignedToMeToggle()
    {
        AssignedToMeToggle.IsEnabled = _me != null;
        AssignedToMeToggle.ToolTip = _me == null
            ? "Waiting to identify you in TeamDynamix"
            : "Show only tickets you are responsible for";
    }
}
