using System.Windows;
using System.Windows.Controls.Primitives;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;

namespace FleetMate.GUI.Views.Projects;

/// <summary>
/// The Projects "New" menu and a card's Create New Alike (macOS BoardsView
/// toolbar menu and taskContextMenu). Each item shows only when this setup can
/// create it: a work item needs an Azure DevOps organization, an issue or a
/// project needs a GitHub owner.
/// </summary>
public partial class BoardsPage
{
    /// <summary>The GitHub project the board last loaded, so a new issue can join it.</summary>
    private string? _gitHubProjectId;

    private bool CanCreateWorkItem => !string.IsNullOrEmpty(_config.AzureDevOps?.Organization);

    private bool CanCreateGitHubItem
    {
        get
        {
            var gh = _config.GitHubProviderOrDefault();
            return !string.IsNullOrEmpty(gh.Owner ?? gh.Organization);
        }
    }

    private void OnNewButtonClick(object sender, RoutedEventArgs e)
    {
        NewWorkItemMenu.Visibility = CanCreateWorkItem ? Visibility.Visible : Visibility.Collapsed;
        NewIssueMenu.Visibility = CanCreateGitHubItem ? Visibility.Visible : Visibility.Collapsed;
        NewProjectMenu.IsEnabled = CanCreateGitHubItem;
        NewMenu.PlacementTarget = NewButton;
        NewMenu.Placement = PlacementMode.Bottom;
        NewMenu.IsOpen = true;
    }

    private void OnNewWorkItem(object sender, RoutedEventArgs e) => ShowCreateWorkItem(null);

    private void OnTaskCreateAlike(object sender, RoutedEventArgs e)
    {
        if (VmOfMenu(MenuOf(sender)) is not { } vm) return;
        ShowCreateWorkItem(WorkItemPrefill.FromTask(vm.Task));
    }

    private async void ShowCreateWorkItem(WorkItemPrefill? prefill)
    {
        if (!CanCreateWorkItem) return;
        _devOpsService ??= _app?.DevOpsService ?? new AzureDevOpsService(_config.AzureDevOps!);
        var dialog = new CreateWorkItemDialog(_devOpsService, prefill) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) await LoadTasksAsync();
    }

    private async void OnNewIssue(object sender, RoutedEventArgs e)
    {
        if (!CanCreateGitHubItem) return;
        var dialog = new CreateIssueDialog(_config.GitHubProviderOrDefault(), _gitHubProjectId, _statusField)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true) await LoadTasksAsync();
    }

    private async void OnNewProject(object sender, RoutedEventArgs e)
    {
        if (!CanCreateGitHubItem) return;
        var service = new GitHubProjectsService(_config.GitHubProviderOrDefault());
        if (!await service.AuthenticateAsync())
        {
            MessageBox.Show(Window.GetWindow(this)!, "GitHub authentication failed.", "New GitHub Project",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dialog = new CreateProjectDialog(service, _config) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) await LoadTasksAsync();
    }
}
