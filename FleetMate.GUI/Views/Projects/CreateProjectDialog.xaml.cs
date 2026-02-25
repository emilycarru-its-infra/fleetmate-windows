using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Config;
using FleetMate.Core.Services.Projects;
using Serilog;

namespace FleetMate.GUI.Views.Projects;

public partial class CreateProjectDialog : Window
{
    private readonly GitHubProjectsService _projectsService;
    private readonly FleetMateConfig _config;
    public bool ProjectCreated { get; private set; }

    public CreateProjectDialog(GitHubProjectsService projectsService, FleetMateConfig config)
    {
        InitializeComponent();
        _projectsService = projectsService;
        _config = config;
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
            var scope = (ScopeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "org";
            var owner = _config.Tasks?.Providers?.GitHub?.Organization
                     ?? _config.Tasks?.Providers?.GitHub?.Owner;

            if (string.IsNullOrEmpty(owner))
            {
                StatusText.Text = "GitHub owner/organization not configured.";
                CreateButton.IsEnabled = true;
                return;
            }

            var project = await _projectsService.CreateProjectAsync(
                owner,
                TitleBox.Text.Trim(),
                scope == "org");

            if (project != null)
            {
                ProjectCreated = true;
                DialogResult = true;
                Close();
            }
            else
            {
                StatusText.Text = "Failed to create project.";
                CreateButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create GitHub project");
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
