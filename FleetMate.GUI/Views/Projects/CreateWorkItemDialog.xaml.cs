using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Serilog;

namespace FleetMate.GUI.Views.Projects;

public partial class CreateWorkItemDialog : Window
{
    private readonly AzureDevOpsService _devOpsService;
    public WorkItem? CreatedWorkItem { get; private set; }

    public CreateWorkItemDialog(AzureDevOpsService devOpsService)
    {
        InitializeComponent();
        _devOpsService = devOpsService;

        // Pre-fill area/iteration from config
        var app = (App)Application.Current;
        if (app.Config.AzureDevOps != null)
        {
            AreaPathBox.Text = app.Config.AzureDevOps.AreaPath ?? app.Config.AzureDevOps.Project ?? "";
            IterationPathBox.Text = app.Config.AzureDevOps.IterationPath ?? "";
        }
    }

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            StatusText.Text = "Title is required.";
            return;
        }

        CreateButton.IsEnabled = false;
        StatusText.Text = "Creating...";

        try
        {
            var type = (TypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Bug";
            var priority = (PriorityCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();

            var request = new CreateWorkItemRequest
            {
                Title = TitleBox.Text.Trim(),
                Type = type,
                Description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim(),
                AssignedTo = string.IsNullOrWhiteSpace(AssignedToBox.Text) ? null : AssignedToBox.Text.Trim(),
                Priority = int.TryParse(priority, out var p) ? p : null,
                AreaPath = string.IsNullOrWhiteSpace(AreaPathBox.Text) ? null : AreaPathBox.Text.Trim(),
                IterationPath = string.IsNullOrWhiteSpace(IterationPathBox.Text) ? null : IterationPathBox.Text.Trim(),
                Tags = string.IsNullOrWhiteSpace(TagsBox.Text) ? null :
                    TagsBox.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
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
                StatusText.Text = "Failed to create work item.";
                CreateButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create work item");
            StatusText.Text = $"Error: {ex.Message}";
            CreateButton.IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
