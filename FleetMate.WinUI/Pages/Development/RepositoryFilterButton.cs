using FleetMate.Core.Models.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FleetMate.WinUI.Pages.Development;

/// <summary>
/// One repository menu in place of a row of chips: provider headings, then a
/// submenu per owner or project, each with its count; "All repositories"
/// clears it. Hidden when there is at most one repository — no choice to offer.
/// The same menu as FleetMate.GUI's <c>RepositoryFilterButton</c>.
/// </summary>
public sealed partial class RepositoryFilterButton : DropDownButton
{
    public const string AllLabel = "All repositories";

    private List<RepositoryFilterEntry> _entries = new();
    private readonly TextBlock _label = new() { Text = AllLabel, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };

    public RepositoryFilterButton()
    {
        Content = _label;
        HorizontalContentAlignment = HorizontalAlignment.Left;
        Reset();
    }

    /// <summary>The chosen repository's scope key, or null for every repository.</summary>
    public string? Selection { get; private set; }

    /// <summary>Raised after the person picks an entry; not raised by <see cref="SetEntries"/>.</summary>
    public event EventHandler? SelectionChanged;

    public void Reset() => SetEntries(new(), null);

    /// <summary>Fill the menu and keep <paramref name="selection"/> when it is still offered. Returns the selection kept.</summary>
    public string? SetEntries(List<RepositoryFilterEntry> entries, string? selection)
    {
        _entries = entries;
        if (selection != null && !entries.Any(entry => entry.Scope.Matches(selection, allowRepositoryOnly: true))) selection = null;
        Selection = selection;
        var label = selection == null
            ? AllLabel
            : entries.First(entry => entry.Scope.Matches(selection, allowRepositoryOnly: true)).Scope.Label;
        _label.Text = label;
        AutomationProperties.SetName(this, $"Filter by repository: {label}");
        ToolTipService.SetToolTip(this, selection == null ? "Filter by repository" : label);
        Visibility = entries.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Flyout = BuildMenu();
        return selection;
    }

    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        menu.Items.Add(Leaf(AllLabel, null, Selection == null));
        foreach (var provider in _entries.GroupBy(entry => entry.Scope.Source))
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var heading = new MenuFlyoutItem
            {
                Text = $"{provider.Key.DisplayName()}  {provider.Sum(entry => entry.Count)}",
                IsEnabled = false,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };
            menu.Items.Add(heading);
            foreach (var container in provider
                         .GroupBy(entry => entry.Scope.Container, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                var submenu = new MenuFlyoutSubItem { Text = $"{container.Key}  {container.Sum(entry => entry.Count)}" };
                foreach (var entry in container.OrderBy(entry => entry.Scope.Repository, StringComparer.OrdinalIgnoreCase))
                    submenu.Items.Add(Leaf($"{entry.Scope.Repository ?? "Project pipelines"}  {entry.Count}", entry.Scope.Key,
                        Selection != null && entry.Scope.Matches(Selection, allowRepositoryOnly: true)));
                menu.Items.Add(submenu);
            }
        }
        return menu;
    }

    private ToggleMenuFlyoutItem Leaf(string label, string? key, bool selected)
    {
        var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = selected };
        item.Click += (_, _) =>
        {
            SetEntries(_entries, key);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };
        return item;
    }
}
