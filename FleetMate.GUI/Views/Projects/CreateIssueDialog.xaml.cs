using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Serilog;

namespace FleetMate.GUI.Views.Projects;

/// <summary>
/// New Issue (macOS CreateIssueView). Assignees, labels and milestones come
/// from the chosen repository; with a GitHub project in view the issue can be
/// added to it and given a status.
/// </summary>
public partial class CreateIssueDialog : Window
{
    private readonly GitHubProviderConfig _config;
    private readonly string? _projectId;
    private readonly GitHubProjectField? _statusField;
    private string? _repositoryId;
    private string? _selectedRepo;

    public bool IssueCreated { get; private set; }

    private string RepoOwner => _config.Owner ?? _config.Organization ?? "";
    private string Repo => _selectedRepo ?? _config.Repo ?? "";
    private bool RepoFromConfig => !string.IsNullOrEmpty(_config.Repo);

    public CreateIssueDialog(GitHubProviderConfig config, string? projectId = null, GitHubProjectField? statusField = null)
    {
        InitializeComponent();
        _config = config;
        _projectId = projectId;
        _statusField = statusField;

        MilestoneCombo.ItemsSource = new List<ComboBoxItem> { new() { Content = "None", Tag = "" } };
        MilestoneCombo.SelectedIndex = 0;

        if (_projectId != null)
        {
            AddToProjectCheck.Visibility = Visibility.Visible;
            if (_statusField is { Options.Count: > 0 })
            {
                var options = new List<ComboBoxItem> { new() { Content = "None", Tag = "" } };
                options.AddRange(_statusField.Options.Select(o => new ComboBoxItem { Content = o.Name, Tag = o.Id }));
                StatusCombo.ItemsSource = options;
                StatusCombo.SelectedIndex = 0;
                StatusPanel.Visibility = Visibility.Visible;
            }
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        TitleBox.Focus();
        if (RepoOwner.Length == 0)
        {
            StatusText.Text = "Set an organization or owner in the GitHub settings to create issues.";
            return;
        }
        await LoadMetadataAsync();
    }

    private async Task LoadMetadataAsync()
    {
        LoadingText.Visibility = Visibility.Visible;
        try
        {
            var service = new GitHubProjectsService(_config);
            if (!await service.AuthenticateAsync())
            {
                StatusText.Text = "GitHub authentication failed";
                return;
            }

            // With no repository in the settings, pick one first.
            if (!RepoFromConfig)
            {
                if (RepoCombo.ItemsSource == null)
                {
                    var repos = await service.ListOrganizationReposAsync(RepoOwner, 50);
                    RepoCombo.ItemsSource = repos.Select(r => r.Name).ToList();
                    RepoPanel.Visibility = Visibility.Visible;
                    if (repos.Count == 0) StatusText.Text = $"No repositories found for {RepoOwner}";
                }
                if (string.IsNullOrEmpty(_selectedRepo)) return;
            }

            var repoId = service.GetRepositoryIdAsync(RepoOwner, Repo);
            var users = service.ListAssignableUsersAsync(RepoOwner, Repo);
            var labels = service.ListRepositoryLabelsAsync(RepoOwner, Repo);
            var milestones = service.ListRepositoryMilestonesAsync(RepoOwner, Repo);
            await Task.WhenAll(repoId, users, labels, milestones);

            _repositoryId = repoId.Result;
            FillAssignees(users.Result);
            FillLabels(labels.Result);
            var ms = new List<ComboBoxItem> { new() { Content = "None", Tag = "" } };
            ms.AddRange(milestones.Result.Select(m => new ComboBoxItem { Content = m.Title, Tag = m.Id }));
            MilestoneCombo.ItemsSource = ms;
            MilestoneCombo.SelectedIndex = 0;
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[github] New Issue: metadata unavailable");
            StatusText.Text = ex.Message;
        }
        finally
        {
            LoadingText.Visibility = Visibility.Collapsed;
            UpdateCreateEnabled();
        }
    }

    private void FillAssignees(List<(string Id, string Login)> users)
    {
        AssigneesPanel.Children.Clear();
        foreach (var user in users)
            AssigneesPanel.Children.Add(new CheckBox { Content = user.Login, Tag = user.Id, Margin = new Thickness(0, 0, 0, 2) });
        NoAssigneesText.Visibility = users.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Each label is a chip in its own colour that toggles on click.</summary>
    private void FillLabels(List<(string Id, string Name, string? Color)> labels)
    {
        LabelsPanel.Children.Clear();
        foreach (var label in labels)
        {
            var color = ParseColor(label.Color);
            var chip = new ToggleButton
            {
                Content = label.Name,
                Tag = label.Id,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(8, 2, 8, 2),
                FontSize = 12,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, color.R, color.G, color.B)),
                Background = new SolidColorBrush(Color.FromArgb(0x1A, color.R, color.G, color.B)),
            };
            chip.Checked += (_, _) =>
            {
                chip.Background = new SolidColorBrush(Color.FromArgb(0x4D, color.R, color.G, color.B));
                chip.BorderBrush = new SolidColorBrush(color);
            };
            chip.Unchecked += (_, _) =>
            {
                chip.Background = new SolidColorBrush(Color.FromArgb(0x1A, color.R, color.G, color.B));
                chip.BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, color.R, color.G, color.B));
            };
            LabelsPanel.Children.Add(chip);
        }
        NoLabelsText.Visibility = labels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Color ParseColor(string? hex)
    {
        try
        {
            if (!string.IsNullOrEmpty(hex) && hex.TrimStart('#').Length == 6)
                return (Color)ColorConverter.ConvertFromString("#" + hex.TrimStart('#'));
        }
        catch { }
        return Colors.Gray;
    }

    private async void OnRepoChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedRepo = RepoCombo.SelectedItem as string;
        _repositoryId = null;
        UpdateCreateEnabled();
        await LoadMetadataAsync();
    }

    private void OnBodyModeChanged(object sender, RoutedEventArgs e)
    {
        if (BodyBox == null || PreviewBorder == null) return;
        var preview = PreviewRadio.IsChecked == true;
        if (preview) PreviewViewer.MarkdownText = string.IsNullOrWhiteSpace(BodyBox.Text) ? "*No content*" : BodyBox.Text;
        BodyBox.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
        PreviewBorder.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTitleChanged(object sender, TextChangedEventArgs e) => UpdateCreateEnabled();

    private void UpdateCreateEnabled()
    {
        if (CreateButton == null) return;
        CreateButton.IsEnabled = !string.IsNullOrWhiteSpace(TitleBox.Text) && RepoOwner.Length > 0 && Repo.Length > 0;
    }

    private static string? Picked(ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem { Tag: string tag } && tag.Length > 0 ? tag : null;

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text)) return;
        if (_repositoryId == null)
        {
            StatusText.Text = "Repository not resolved";
            return;
        }

        CreateButton.IsEnabled = false;
        StatusText.Text = "";

        try
        {
            var service = new GitHubProjectsService(_config);
            if (!await service.AuthenticateAsync())
            {
                StatusText.Text = "Authentication failed";
                return;
            }

            var assignees = AssigneesPanel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
            var labels = LabelsPanel.Children.OfType<ToggleButton>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
            var body = BodyBox.Text.Trim();

            var result = await service.CreateIssueAsync(
                _repositoryId,
                TitleBox.Text.Trim(),
                body.Length == 0 ? null : body,
                assignees.Count > 0 ? assignees : null,
                labels.Count > 0 ? labels : null,
                Picked(MilestoneCombo));

            if (_projectId != null && AddToProjectCheck.IsChecked == true)
            {
                var itemId = await service.AddItemToProjectAsync(_projectId, result.Id);
                if (_statusField != null && Picked(StatusCombo) is { } optionId)
                    await service.MoveItemToStatusAsync(_projectId, itemId, _statusField.Id, optionId);
            }

            IssueCreated = true;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create GitHub issue");
            StatusText.Text = $"Failed to create issue: {ex.Message}";
        }
        finally
        {
            UpdateCreateEnabled();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
