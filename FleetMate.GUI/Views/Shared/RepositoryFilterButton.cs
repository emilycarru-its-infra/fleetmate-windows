using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FleetMate.Core.Models.Projects;

namespace FleetMate.GUI.Views.Shared;

/// <summary>A compact native menu with provider headings and owner/project submenus.</summary>
public sealed class RepositoryFilterButton : Button
{
    public string? Selection { get; private set; }
    public event SelectionChangedEventHandler? SelectionChanged;
    private List<RepositoryFilterEntry> _entries = new();

    public RepositoryFilterButton()
    {
        Padding = new Thickness(8, 4, 8, 4);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Reset();
        Unloaded += (_, _) => { if (ContextMenu != null) ContextMenu.IsOpen = false; };
    }

    public void Reset() => SetEntries(new(), null);

    public string? SetEntries(List<RepositoryFilterEntry> entries, string? selection)
    {
        _entries = entries;
        if (selection != null && !entries.Any(entry => entry.Scope.Matches(selection, allowRepositoryOnly: true))) selection = null;
        Selection = selection;
        var label = selection == null ? RepoFilterMenu.AllLabel : entries.First(entry => entry.Scope.Matches(selection, allowRepositoryOnly: true)).Scope.Label;
        var panel = new DockPanel();
        var arrow = new TextBlock { Text = "⌄", Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(arrow, Dock.Right);
        panel.Children.Add(arrow);
        panel.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis });
        Content = panel;
        ToolTip = selection == null ? "Filter by repository" : label;
        Visibility = entries.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ContextMenu = BuildMenu();
        return selection;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.Bottom };
        menu.Items.Add(Leaf(RepoFilterMenu.AllLabel, null, Selection == null));
        foreach (var provider in _entries.GroupBy(entry => entry.Scope.Source))
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = $"{provider.Key.DisplayName()}  {provider.Sum(entry => entry.Count)}", IsEnabled = false, FontWeight = FontWeights.SemiBold });
            foreach (var container in provider.GroupBy(entry => entry.Scope.Container, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                var submenu = new MenuItem { Header = $"{container.Key}  {container.Sum(entry => entry.Count)}" };
                foreach (var entry in container.OrderBy(entry => entry.Scope.Repository, StringComparer.OrdinalIgnoreCase))
                    submenu.Items.Add(Leaf($"{entry.Scope.Repository ?? "Project pipelines"}  {entry.Count}", entry.Scope.Key,
                        Selection != null && entry.Scope.Matches(Selection, allowRepositoryOnly: true)));
                menu.Items.Add(submenu);
            }
        }
        return menu;
    }

    private MenuItem Leaf(string label, string? key, bool selected)
    {
        var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = selected };
        item.Click += (_, _) =>
        {
            var previous = Selection;
            SetEntries(_entries, key);
            SelectionChanged?.Invoke(this, new SelectionChangedEventArgs(Selector.SelectionChangedEvent,
                new object?[] { previous }, new object?[] { key }));
        };
        return item;
    }

    protected override void OnClick()
    {
        base.OnClick();
        if (ContextMenu != null) ContextMenu.IsOpen = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            OnClick();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
