using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FleetMate.WinUI.ViewModels;

namespace FleetMate.WinUI.Views;

public sealed partial class ProjectsPage : Page
{
    private List<WorkItemRowViewModel> _all = new();

    public ProjectsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        var devops = App.Current.DevOpsService;
        if (devops == null)
        {
            CountText.Text = "Azure DevOps is not configured — add organization/project in Settings.";
            _all = new();
            ApplyFilter();
            return;
        }

        RefreshButton.IsEnabled = false;
        LoadingRing.IsActive = true;
        WorkItemList.ItemsSource = null;
        try
        {
            var items = await devops.GetWorkItemsAsync(limit: 500);
            _all = items.Select(w => new WorkItemRowViewModel(w)).ToList();
            ApplyFilter();
            if (_all.Count == 0)
                CountText.Text = "No work items (or not signed in to Azure DevOps — run az login).";
        }
        catch (Exception ex)
        {
            CountText.Text = $"Failed to load work items: {ex.Message}";
            _all = new();
            ApplyFilter();
        }
        finally
        {
            LoadingRing.IsActive = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text?.Trim() ?? "";
        var rows = string.IsNullOrEmpty(q) ? _all : _all.Where(r => r.Matches(q)).ToList();
        rows = rows.OrderByDescending(r => r.Item.Fields.ChangedDate ?? r.Item.Fields.CreatedDate).ToList();
        WorkItemList.ItemsSource = rows;
        if (_all.Count > 0)
            CountText.Text = rows.Count == _all.Count ? $"{_all.Count} work items" : $"{rows.Count} of {_all.Count} work items";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void WorkItemList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorkItemList.SelectedItem is not WorkItemRowViewModel row)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            EmptyDetail.Visibility = Visibility.Visible;
            return;
        }

        var f = row.Item.Fields;
        EmptyDetail.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        DetailId.Text = $"{row.IdText}  ·  {row.Type}";
        DetailTitle.Text = row.Title;

        DetailRows.Children.Clear();
        AddRow("Type", row.Type);
        AddRow("State", row.State);
        AddRow("Reason", f.Reason ?? "—");
        AddRow("Assignee", row.Assignee);
        AddRow("Area", f.AreaPath ?? "—");
        AddRow("Iteration", f.IterationPath ?? "—");
        AddRow("Priority", f.Priority?.ToString() ?? "—");
        AddRow("Tags", string.IsNullOrEmpty(f.Tags) ? "—" : f.Tags!);
        AddRow("Created", Created(f.CreatedDate, f.CreatedBy?.DisplayName));
        AddRow("Changed", Created(f.ChangedDate, f.ChangedBy?.DisplayName));

        DetailDescription.Text = TextUtil.StripHtml(f.Description);
    }

    private void AddRow(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var l = new TextBlock { Text = label, FontSize = 12, Opacity = 0.65 };
        l.SetValue(Grid.ColumnProperty, 0);

        var v = new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        v.SetValue(Grid.ColumnProperty, 1);

        grid.Children.Add(l);
        grid.Children.Add(v);
        DetailRows.Children.Add(grid);
    }

    private static string Created(DateTime? date, string? by)
    {
        if (date is null) return "—";
        var d = date.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        return string.IsNullOrEmpty(by) ? d : $"{d} · {by}";
    }
}
