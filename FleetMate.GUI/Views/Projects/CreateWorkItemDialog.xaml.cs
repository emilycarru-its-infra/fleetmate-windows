using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Serilog;

namespace FleetMate.GUI.Views.Projects;

/// <summary>
/// New DevOps Work Item (macOS CreateWorkItemView). Opened plain from the
/// Projects New menu, or from a card's Create New Alike with that card's type,
/// assignee, priority, area, iteration, tags and description filled in.
/// </summary>
public partial class CreateWorkItemDialog : Window
{
    private readonly AzureDevOpsService _devOpsService;
    private readonly WorkItemPrefill _prefill;
    private List<DevOpsMember> _members = new();
    public WorkItem? CreatedWorkItem { get; private set; }

    public CreateWorkItemDialog(AzureDevOpsService devOpsService, WorkItemPrefill? prefill = null)
    {
        InitializeComponent();
        _devOpsService = devOpsService;
        _prefill = prefill ?? new WorkItemPrefill();

        if (_prefill.Priority is int p and >= 1 and <= 4) PriorityCombo.SelectedIndex = p - 1;
        AssignedToBox.Text = _prefill.AssignedTo ?? "";
        DescriptionBox.Text = _prefill.Description ?? "";
        if (_prefill.Tags is { Count: > 0 } tags) TagsBox.Text = string.Join("; ", tags);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && CreateButton.IsEnabled)
            {
                e.Handled = true;
                OnCreate(this, new RoutedEventArgs());
            }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        TitleBox.Focus();
        // Each list loads on its own; one failing leaves the others usable.
        await Task.WhenAll(LoadBoardsAsync(), LoadMembersAsync(), LoadAreasAsync(), LoadIterationsAsync());
    }

    private async Task LoadBoardsAsync()
    {
        try
        {
            var boards = await _devOpsService.GetBoardsAsync();
            if (boards.Count == 0)
            {
                await LoadTypesAsync(null);
                return;
            }
            BoardCombo.ItemsSource = boards;
            BoardPanel.Visibility = Visibility.Visible;
            BoardCombo.SelectedIndex = 0; // OnBoardChanged loads the board's types.
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[azdo] New Work Item: boards unavailable");
            await LoadTypesAsync(null);
        }
    }

    private async void OnBoardChanged(object sender, SelectionChangedEventArgs e) =>
        await LoadTypesAsync((BoardCombo.SelectedItem as Board)?.Name);

    /// <summary>The board's types; the project's when there is no board or it shows none.</summary>
    private async Task LoadTypesAsync(string? board)
    {
        var current = TypeCombo.SelectedItem as string;
        var types = board != null ? await _devOpsService.GetBoardWorkItemTypesAsync(board) : new List<string>();
        if (types.Count == 0) types = await _devOpsService.GetWorkItemTypeNamesAsync();

        TypeCombo.ItemsSource = types;
        var pick = NewItemForm.PickType(types, _prefill.Type, current);
        TypeCombo.SelectedItem = pick.Length > 0 ? pick : null;
        TypeCombo.ToolTip = types.Count == 0 ? "No work item types could be loaded" : null;
        UpdateCreateEnabled();
    }

    private async Task LoadMembersAsync()
    {
        _members = await _devOpsService.GetTeamMembersAsync();
        if (_members.Count == 0) return; // Keep the free-text field.

        var assignee = NewItemForm.ResolveAssignee(AssignedToBox.Text, _members);
        var items = new List<ComboBoxItem> { new() { Content = "Unassigned", Tag = "" } };
        items.AddRange(_members
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(m => new ComboBoxItem { Content = string.IsNullOrEmpty(m.DisplayName) ? m.UniqueName : m.DisplayName, Tag = m.UniqueName, ToolTip = m.UniqueName }));
        // An assignee who is not on the team still carries over.
        if (assignee.Length > 0 && !items.Any(i => string.Equals((string)i.Tag, assignee, StringComparison.OrdinalIgnoreCase)))
            items.Add(new ComboBoxItem { Content = assignee, Tag = assignee });

        AssignedToCombo.ItemsSource = items;
        AssignedToCombo.SelectedItem = items.FirstOrDefault(i => string.Equals((string)i.Tag, assignee, StringComparison.OrdinalIgnoreCase)) ?? items[0];
        AssignedToCombo.Visibility = Visibility.Visible;
        AssignedToBox.Visibility = Visibility.Collapsed;
    }

    private async Task LoadAreasAsync() =>
        FillPathPicker(AreaPanel, AreaCombo, await _devOpsService.GetAreaPathsAsync(), _prefill.AreaPath);

    private async Task LoadIterationsAsync() =>
        FillPathPicker(IterationPanel, IterationCombo, await _devOpsService.GetIterationPathsAsync(), _prefill.IterationPath);

    /// <summary>"Default" plus every path; a prefilled path not in the list is still offered.</summary>
    private static void FillPathPicker(FrameworkElement panel, ComboBox combo, List<string> paths, string? selected)
    {
        if (paths.Count == 0 && string.IsNullOrEmpty(selected)) return;
        var items = new List<ComboBoxItem> { new() { Content = "Default", Tag = "" } };
        items.AddRange(paths.Select(p => new ComboBoxItem { Content = p, Tag = p }));
        if (!string.IsNullOrEmpty(selected) && !paths.Contains(selected, StringComparer.OrdinalIgnoreCase))
            items.Add(new ComboBoxItem { Content = selected, Tag = selected });
        combo.ItemsSource = items;
        combo.SelectedItem = items.FirstOrDefault(i => string.Equals((string)i.Tag, selected ?? "", StringComparison.OrdinalIgnoreCase)) ?? items[0];
        panel.Visibility = Visibility.Visible;
    }

    private static string? Picked(ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem { Tag: string tag } && tag.Length > 0 ? tag : null;

    private void OnFieldChanged(object sender, RoutedEventArgs e) => UpdateCreateEnabled();

    private void UpdateCreateEnabled()
    {
        if (CreateButton == null) return;
        CreateButton.IsEnabled = !string.IsNullOrWhiteSpace(TitleBox.Text) && TypeCombo.SelectedItem is string;
    }

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text) || TypeCombo.SelectedItem is not string type) return;

        CreateButton.IsEnabled = false;
        StatusText.Text = "";

        try
        {
            var assignee = AssignedToCombo.Visibility == Visibility.Visible
                ? Picked(AssignedToCombo)
                : (string.IsNullOrWhiteSpace(AssignedToBox.Text) ? null : AssignedToBox.Text.Trim());
            var tags = NewItemForm.SplitTags(TagsBox.Text);

            var request = new CreateWorkItemRequest
            {
                Title = TitleBox.Text.Trim(),
                Type = type,
                Description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text,
                AssignedTo = assignee,
                Priority = int.TryParse((PriorityCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var p) ? p : null,
                AreaPath = Picked(AreaCombo),
                IterationPath = Picked(IterationCombo),
                Tags = tags.Count > 0 ? tags : null,
            };

            var workItem = await _devOpsService.CreateWorkItemAsync(request);
            if (workItem != null)
            {
                CreatedWorkItem = workItem;
                DialogResult = true;
                Close();
            }
            else
            {
                StatusText.Text = "Azure DevOps did not create the work item.";
                UpdateCreateEnabled();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create work item");
            StatusText.Text = ex.Message;
            UpdateCreateEnabled();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
