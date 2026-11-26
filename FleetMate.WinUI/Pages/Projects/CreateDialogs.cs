using FleetMate.Core.Config;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.WinUI.Pages.Development.Shared;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Serilog;

namespace FleetMate.WinUI.Pages.Projects;

/// <summary>The shared shape of the New dialogs: a scrolling form, a status line, Create and Cancel.</summary>
public abstract partial class CreateDialogBase : ContentDialog
{
    protected readonly StackPanel Form = new() { Spacing = 12, Width = 440 };
    protected readonly TextBlock Status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        Visibility = Visibility.Collapsed,
    };

    protected CreateDialogBase(string title)
    {
        Title = title;
        PrimaryButtonText = "Create";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        IsPrimaryButtonEnabled = false;
        Content = new ScrollViewer { Content = new StackPanel { Spacing = 12, Children = { Form, Status } }, MaxHeight = 560 };
        PrimaryButtonClick += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            IsPrimaryButtonEnabled = false;
            ShowStatus(null);
            try { e.Cancel = !await CreateAsync(); }
            catch (Exception ex)
            {
                Log.Error(ex, "[projects] {Dialog} failed", title);
                ShowStatus(ex.Message);
                e.Cancel = true;
            }
            finally
            {
                UpdateCreateEnabled();
                deferral.Complete();
            }
        };
    }

    /// <summary>Make the item; false keeps the dialog open with <see cref="Status"/> saying why.</summary>
    protected abstract Task<bool> CreateAsync();

    protected abstract void UpdateCreateEnabled();

    protected void ShowStatus(string? message)
    {
        Status.Text = message ?? "";
        Status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    protected static ComboBox Combo(string header) => new() { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch };

    protected static string? Picked(ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem { Tag: string tag } && tag.Length > 0 ? tag : null;
}

/// <summary>
/// New DevOps Work Item (macOS CreateWorkItemView). Opened plain from the
/// New menu, or from a card's Create New Alike with that card's type,
/// assignee, priority, area, iteration, tags and description filled in.
/// </summary>
public sealed partial class CreateWorkItemDialog : CreateDialogBase
{
    private readonly AzureDevOpsService _devOps;
    private readonly WorkItemPrefill _prefill;
    private readonly ComboBox _board = Combo("Board");
    private readonly ComboBox _type = Combo("Type");
    private readonly TextBox _title = new() { Header = "Title", PlaceholderText = "What needs doing" };
    private readonly TextBox _description = new() { Header = "Description", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96 };
    private readonly ComboBox _priority = Combo("Priority");
    private readonly ComboBox _assignee = Combo("Assigned To");
    private readonly TextBox _assigneeText = new() { Header = "Assigned To", PlaceholderText = "name@example.edu" };
    private readonly ComboBox _area = Combo("Area Path");
    private readonly ComboBox _iteration = Combo("Iteration");
    private readonly TextBox _tags = new() { Header = "Tags", PlaceholderText = "Separate tags with semicolons" };

    public WorkItem? Created { get; private set; }

    public CreateWorkItemDialog(AzureDevOpsService devOps, WorkItemPrefill? prefill) : base("New DevOps Work Item")
    {
        _devOps = devOps;
        _prefill = prefill ?? new WorkItemPrefill();
        foreach (var (value, label) in new[] { (1, "1 - Critical"), (2, "2 - High"), (3, "3 - Medium"), (4, "4 - Low") })
            _priority.Items.Add(new ComboBoxItem { Content = label, Tag = value.ToString() });
        _priority.SelectedIndex = _prefill.Priority is int p and >= 1 and <= 4 ? p - 1 : 1;
        _assigneeText.Text = _prefill.AssignedTo ?? "";
        _description.Text = _prefill.Description ?? "";
        if (_prefill.Tags is { Count: > 0 } tags) _tags.Text = string.Join("; ", tags);
        _board.Visibility = _assignee.Visibility = _area.Visibility = _iteration.Visibility = Visibility.Collapsed;
        foreach (var control in new FrameworkElement[] { _board, _type, _title, _description, _priority, _assignee, _assigneeText, _area, _iteration, _tags })
            Form.Children.Add(control);
        _title.TextChanged += (_, _) => UpdateCreateEnabled();
        _type.SelectionChanged += (_, _) => UpdateCreateEnabled();
        _board.SelectionChanged += async (_, _) => await LoadTypesAsync((_board.SelectedItem as ComboBoxItem)?.Tag as string);
        Opened += async (_, _) =>
        {
            _title.Focus(FocusState.Programmatic);
            // Each list loads on its own; one failing leaves the others usable.
            await Task.WhenAll(Guard(LoadBoardsAsync), Guard(LoadMembersAsync), Guard(LoadAreasAsync), Guard(LoadIterationsAsync));
        };
    }

    private static async Task Guard(Func<Task> load)
    {
        try { await load(); }
        catch (Exception ex) { Log.Debug(ex, "[azdo] New Work Item: a list did not load"); }
    }

    private async Task LoadBoardsAsync()
    {
        var boards = await _devOps.GetBoardsAsync();
        if (boards.Count == 0)
        {
            await LoadTypesAsync(null);
            return;
        }
        foreach (var board in boards) _board.Items.Add(new ComboBoxItem { Content = board.Name, Tag = board.Name });
        _board.Visibility = Visibility.Visible;
        _board.SelectedIndex = 0;
    }

    /// <summary>The board's types; the project's when there is no board or it shows none.</summary>
    private async Task LoadTypesAsync(string? board)
    {
        var current = _type.SelectedItem as string;
        var types = board != null ? await _devOps.GetBoardWorkItemTypesAsync(board) : new List<string>();
        if (types.Count == 0) types = await _devOps.GetWorkItemTypeNamesAsync();
        _type.ItemsSource = types;
        var pick = NewItemForm.PickType(types, _prefill.Type, current);
        _type.SelectedItem = pick.Length > 0 ? pick : null;
        _type.PlaceholderText = types.Count == 0 ? "No work item types could be loaded" : "Choose a type";
        UpdateCreateEnabled();
    }

    private async Task LoadMembersAsync()
    {
        var members = await _devOps.GetTeamMembersAsync();
        if (members.Count == 0) return; // Keep the free-text field.
        var assignee = NewItemForm.ResolveAssignee(_assigneeText.Text, members);
        _assignee.Items.Add(new ComboBoxItem { Content = "Unassigned", Tag = "" });
        foreach (var m in members.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var item = new ComboBoxItem { Content = string.IsNullOrEmpty(m.DisplayName) ? m.UniqueName : m.DisplayName, Tag = m.UniqueName };
            ToolTipService.SetToolTip(item, m.UniqueName);
            _assignee.Items.Add(item);
        }
        // An assignee who is not on the team still carries over.
        var items = _assignee.Items.OfType<ComboBoxItem>().ToList();
        if (assignee.Length > 0 && !items.Any(i => string.Equals((string)i.Tag, assignee, StringComparison.OrdinalIgnoreCase)))
            _assignee.Items.Add(new ComboBoxItem { Content = assignee, Tag = assignee });
        _assignee.SelectedItem = _assignee.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals((string)i.Tag, assignee, StringComparison.OrdinalIgnoreCase)) ?? _assignee.Items[0];
        _assignee.Visibility = Visibility.Visible;
        _assigneeText.Visibility = Visibility.Collapsed;
    }

    private async Task LoadAreasAsync() => FillPaths(_area, await _devOps.GetAreaPathsAsync(), _prefill.AreaPath);

    private async Task LoadIterationsAsync() => FillPaths(_iteration, await _devOps.GetIterationPathsAsync(), _prefill.IterationPath);

    /// <summary>"Default" plus every path; a prefilled path not in the list is still offered.</summary>
    private static void FillPaths(ComboBox combo, List<string> paths, string? selected)
    {
        if (paths.Count == 0 && string.IsNullOrEmpty(selected)) return;
        combo.Items.Add(new ComboBoxItem { Content = "Default", Tag = "" });
        foreach (var p in paths) combo.Items.Add(new ComboBoxItem { Content = p, Tag = p });
        if (!string.IsNullOrEmpty(selected) && !paths.Contains(selected, StringComparer.OrdinalIgnoreCase))
            combo.Items.Add(new ComboBoxItem { Content = selected, Tag = selected });
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals((string)i.Tag, selected ?? "", StringComparison.OrdinalIgnoreCase)) ?? combo.Items[0];
        combo.Visibility = Visibility.Visible;
    }

    protected override void UpdateCreateEnabled() =>
        IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(_title.Text) && _type.SelectedItem is string;

    protected override async Task<bool> CreateAsync()
    {
        if (_type.SelectedItem is not string type) return false;
        var assignee = _assignee.Visibility == Visibility.Visible ? Picked(_assignee)
            : string.IsNullOrWhiteSpace(_assigneeText.Text) ? null : _assigneeText.Text.Trim();
        var tags = NewItemForm.SplitTags(_tags.Text);
        Created = await _devOps.CreateWorkItemAsync(new CreateWorkItemRequest
        {
            Title = _title.Text.Trim(),
            Type = type,
            Description = string.IsNullOrWhiteSpace(_description.Text) ? null : _description.Text,
            AssignedTo = assignee,
            Priority = int.TryParse(Picked(_priority), out var p) ? p : null,
            AreaPath = Picked(_area),
            IterationPath = Picked(_iteration),
            Tags = tags.Count > 0 ? tags : null,
        });
        if (Created == null) ShowStatus("Azure DevOps did not create the work item.");
        return Created != null;
    }
}

/// <summary>
/// New GitHub Issue (macOS CreateIssueView). Assignees, labels and
/// milestones come from the chosen repository; with a GitHub Project in view
/// the issue can join it with a status.
/// </summary>
public sealed partial class CreateIssueDialog : CreateDialogBase
{
    private readonly GitHubProviderConfig _config;
    private readonly string? _projectId;
    private readonly GitHubProjectField? _statusField;
    private readonly ComboBox _repo = Combo("Repository");
    private readonly TextBox _title = new() { Header = "Title" };
    private readonly SelectorBar _bodyMode = new();
    private readonly TextBox _body = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, PlaceholderText = "Leave a comment (Markdown)" };
    private readonly MarkdownView _preview = new() { Visibility = Visibility.Collapsed, MinHeight = 120 };
    private readonly StackPanel _assignees = new() { Spacing = 2 };
    private readonly ChipWrapPanel _labels = new() { Spacing = 6, LineSpacing = 6 };
    private readonly ComboBox _milestone = Combo("Milestone");
    private readonly CheckBox _addToProject = new() { Content = "Add to the GitHub Project", IsChecked = true, Visibility = Visibility.Collapsed };
    private readonly ComboBox _status = Combo("Status");
    private readonly ProgressBar _loading = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private string? _repositoryId;

    private string Owner => _config.Owner ?? _config.Organization ?? "";
    private string Repo => _repo.SelectedItem as string ?? _config.Repo ?? "";

    public CreateIssueDialog(GitHubProviderConfig config, string? projectId, GitHubProjectField? statusField) : base("New GitHub Issue")
    {
        _config = config;
        _projectId = projectId;
        _statusField = statusField;
        _repo.Visibility = Visibility.Collapsed;
        _milestone.Items.Add(new ComboBoxItem { Content = "None", Tag = "" });
        _milestone.SelectedIndex = 0;
        _status.Visibility = Visibility.Collapsed;
        if (_projectId != null)
        {
            _addToProject.Visibility = Visibility.Visible;
            if (_statusField is { Options.Count: > 0 })
            {
                _status.Items.Add(new ComboBoxItem { Content = "None", Tag = "" });
                foreach (var o in _statusField.Options) _status.Items.Add(new ComboBoxItem { Content = o.Name, Tag = o.Id });
                _status.SelectedIndex = 0;
                _status.Visibility = Visibility.Visible;
            }
        }
        _bodyMode.Items.Add(new SelectorBarItem { Text = "Write", IsSelected = true });
        _bodyMode.Items.Add(new SelectorBarItem { Text = "Preview" });
        AutomationProperties.SetName(_bodyMode, "Description mode");
        _bodyMode.SelectionChanged += (_, _) =>
        {
            var preview = _bodyMode.SelectedItem?.Text == "Preview";
            if (preview) _preview.Show(string.IsNullOrWhiteSpace(_body.Text) ? "*No content*" : _body.Text);
            _body.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
            _preview.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        };
        AutomationProperties.SetName(_body, "Description");

        Form.Children.Add(_loading);
        Form.Children.Add(_repo);
        Form.Children.Add(_title);
        Form.Children.Add(new StackPanel { Spacing = 4, Children = { Label("Description"), _bodyMode, _body, _preview } });
        Form.Children.Add(new StackPanel { Spacing = 4, Children = { Label("Assignees"), _assignees } });
        Form.Children.Add(new StackPanel { Spacing = 4, Children = { Label("Labels"), _labels } });
        Form.Children.Add(_milestone);
        Form.Children.Add(_addToProject);
        Form.Children.Add(_status);

        _title.TextChanged += (_, _) => UpdateCreateEnabled();
        _repo.SelectionChanged += async (_, _) =>
        {
            _repositoryId = null;
            UpdateCreateEnabled();
            await LoadMetadataAsync();
        };
        Opened += async (_, _) =>
        {
            _title.Focus(FocusState.Programmatic);
            if (Owner.Length == 0)
            {
                ShowStatus("Set an organization or owner in the GitHub settings to create issues.");
                return;
            }
            await LoadMetadataAsync();
        };
    }

    private static TextBlock Label(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };

    private async Task LoadMetadataAsync()
    {
        _loading.Visibility = Visibility.Visible;
        try
        {
            var service = new GitHubProjectsService(_config);
            if (!await service.AuthenticateAsync())
            {
                ShowStatus("GitHub authentication failed");
                return;
            }
            // With no repository in the settings, pick one first.
            if (string.IsNullOrEmpty(_config.Repo))
            {
                if (_repo.ItemsSource == null)
                {
                    var repos = await service.ListOrganizationReposAsync(Owner, 50);
                    _repo.ItemsSource = repos.Select(r => r.Name).ToList();
                    _repo.Visibility = Visibility.Visible;
                    if (repos.Count == 0) ShowStatus($"No repositories found for {Owner}");
                }
                if (_repo.SelectedItem == null) return;
            }
            var repoId = service.GetRepositoryIdAsync(Owner, Repo);
            var users = service.ListAssignableUsersAsync(Owner, Repo);
            var labels = service.ListRepositoryLabelsAsync(Owner, Repo);
            var milestones = service.ListRepositoryMilestonesAsync(Owner, Repo);
            await Task.WhenAll(repoId, users, labels, milestones);
            _repositoryId = repoId.Result;

            _assignees.Children.Clear();
            foreach (var user in users.Result) _assignees.Children.Add(new CheckBox { Content = user.Login, Tag = user.Id });
            if (users.Result.Count == 0) _assignees.Children.Add(None("No assignable users"));

            _labels.Children.Clear();
            foreach (var label in labels.Result) _labels.Children.Add(Chip(label.Id, label.Name, label.Color));
            if (labels.Result.Count == 0) _labels.Children.Add(None("No labels"));

            _milestone.Items.Clear();
            _milestone.Items.Add(new ComboBoxItem { Content = "None", Tag = "" });
            foreach (var m in milestones.Result) _milestone.Items.Add(new ComboBoxItem { Content = m.Title, Tag = m.Id });
            _milestone.SelectedIndex = 0;
            ShowStatus(null);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[github] New Issue: metadata unavailable");
            ShowStatus(ex.Message);
        }
        finally
        {
            _loading.Visibility = Visibility.Collapsed;
            UpdateCreateEnabled();
        }
    }

    private static TextBlock None(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    /// <summary>Each label is a chip ringed in its own colour that toggles on click.</summary>
    private static ToggleButton Chip(string id, string name, string? hex)
    {
        var color = Colors.Gray;
        if (hex?.TrimStart('#') is { Length: 6 } h && uint.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var v))
            color = Windows.UI.Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        var chip = new ToggleButton
        {
            Content = name,
            Tag = id,
            Padding = new Thickness(10, 3, 10, 3),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(color),
        };
        AutomationProperties.SetName(chip, $"Label {name}");
        return chip;
    }

    protected override void UpdateCreateEnabled() =>
        IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(_title.Text) && Owner.Length > 0 && Repo.Length > 0 && _repositoryId != null;

    protected override async Task<bool> CreateAsync()
    {
        if (_repositoryId == null)
        {
            ShowStatus("Repository not resolved");
            return false;
        }
        var service = new GitHubProjectsService(_config);
        if (!await service.AuthenticateAsync())
        {
            ShowStatus("Authentication failed");
            return false;
        }
        var assignees = _assignees.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
        var labels = _labels.Children.OfType<ToggleButton>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
        var body = _body.Text.Trim();
        var result = await service.CreateIssueAsync(_repositoryId, _title.Text.Trim(), body.Length == 0 ? null : body,
            assignees.Count > 0 ? assignees : null, labels.Count > 0 ? labels : null, Picked(_milestone));
        if (_projectId != null && _addToProject.IsChecked == true)
        {
            var itemId = await service.AddItemToProjectAsync(_projectId, result.Id);
            if (_statusField != null && Picked(_status) is { } optionId)
                await service.MoveItemToStatusAsync(_projectId, itemId, _statusField.Id, optionId);
        }
        return true;
    }
}

/// <summary>New GitHub Project: a title, owned by the configured organization or by the signed-in user.</summary>
public sealed partial class CreateProjectDialog : CreateDialogBase
{
    private readonly GitHubProjectsService _service;
    private readonly FleetMateConfig _config;
    private readonly TextBox _title = new() { Header = "Title" };
    private readonly ComboBox _scope = Combo("Owner");

    public CreateProjectDialog(GitHubProjectsService service, FleetMateConfig config) : base("New GitHub Project")
    {
        _service = service;
        _config = config;
        _scope.Items.Add(new ComboBoxItem { Content = "Organization", Tag = "org" });
        _scope.Items.Add(new ComboBoxItem { Content = "User", Tag = "user" });
        _scope.SelectedIndex = 0;
        Form.Children.Add(_title);
        Form.Children.Add(_scope);
        _title.TextChanged += (_, _) => UpdateCreateEnabled();
        Opened += (_, _) => _title.Focus(FocusState.Programmatic);
    }

    protected override void UpdateCreateEnabled() => IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(_title.Text);

    protected override async Task<bool> CreateAsync()
    {
        var owner = _config.Tasks?.Providers?.GitHub?.Organization ?? _config.Tasks?.Providers?.GitHub?.Owner;
        if (string.IsNullOrEmpty(owner))
        {
            ShowStatus("GitHub owner/organization not configured.");
            return false;
        }
        // createProjectV2 takes the owner's node id, not its login.
        var ownerId = await _service.GetOwnerIdAsync(owner, Picked(_scope) == "user" ? ProjectScope.User : ProjectScope.Organization);
        var project = await _service.CreateProjectAsync(ownerId, _title.Text.Trim());
        if (project == null) ShowStatus("Failed to create project.");
        return project != null;
    }
}
