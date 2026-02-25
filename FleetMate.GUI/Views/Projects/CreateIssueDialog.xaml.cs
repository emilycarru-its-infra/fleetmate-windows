using System.Windows;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects.Tasks;
using Serilog;

namespace FleetMate.GUI.Views.Projects;

public partial class CreateIssueDialog : Window
{
    private readonly TaskProviderRegistry _registry;
    public UnifiedTask? CreatedTask { get; private set; }

    public CreateIssueDialog(TaskProviderRegistry registry, FleetMateConfig config)
    {
        InitializeComponent();
        _registry = registry;

        // Pre-fill repo from GitHub config
        var gh = config.Tasks?.Providers?.GitHub;
        if (gh != null && !string.IsNullOrEmpty(gh.Owner) && !string.IsNullOrEmpty(gh.Repo))
        {
            RepoBox.Text = $"{gh.Owner}/{gh.Repo}";
        }

        if (gh?.DefaultLabels?.Count > 0)
        {
            LabelsBox.Text = string.Join(", ", gh.DefaultLabels);
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
            // Find the GitHub provider
            var provider = _registry.GetProvider("github");
            if (provider == null)
            {
                StatusText.Text = "GitHub provider not configured.";
                CreateButton.IsEnabled = true;
                return;
            }

            var request = new CreateTaskRequest
            {
                Title = TitleBox.Text.Trim(),
                Description = string.IsNullOrWhiteSpace(BodyBox.Text) ? null : BodyBox.Text.Trim(),
                Labels = string.IsNullOrWhiteSpace(LabelsBox.Text) ? null :
                    LabelsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                AssignedTo = string.IsNullOrWhiteSpace(AssigneesBox.Text) ? null : AssigneesBox.Text.Trim(),
            };

            var task = await provider.CreateTaskAsync(request);
            CreatedTask = task;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create GitHub issue");
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
