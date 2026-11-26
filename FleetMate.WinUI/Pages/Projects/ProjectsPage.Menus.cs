using FleetMate.Core.Links;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Serilog;
using Windows.ApplicationModel.DataTransfer;

namespace FleetMate.WinUI.Pages.Projects;

/// <summary>
/// The card menu shared by board cards, Mine and Recent rows and query rows
/// (macOS taskContextMenu): Open in Browser, Copy Link and Copy ID for
/// everything; for Azure DevOps work items, State, Priority, Assign To, Area,
/// Iteration, Type, Reschedule, Create Branch and Create New Alike. Option
/// lists load once per project, the first time the menu opens for it. And
/// the New menu, whose items show only when this setup can create them.
/// </summary>
public sealed partial class ProjectsPage
{
    private sealed class MenuOptions
    {
        public List<DevOpsMember>? Members;
        public List<string>? Areas;
        public List<string>? Iterations;
        public List<string>? Types;
        public List<DevOpsRepository>? Repositories;
        public readonly Dictionary<string, List<string>> States = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, MenuOptions> _menuOptions = new(StringComparer.OrdinalIgnoreCase);

    private MenuOptions OptionsFor(string project)
    {
        if (!_menuOptions.TryGetValue(project, out var options)) _menuOptions[project] = options = new MenuOptions();
        return options;
    }

    private static string ProjectOf(UnifiedTask task) => TaskBoard.Meta(task, "project");

    private void Card_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not TaskCardItem card) return;
        args.Handled = true;
        ShowTaskMenu(card.Task, (FrameworkElement)sender, args.TryGetPosition(sender, out var at) ? at : null);
    }

    private void ShowTaskMenu(UnifiedTask task, FrameworkElement target, Windows.Foundation.Point? at)
    {
        var menu = BuildTaskMenu(task);
        if (at is { } point) menu.ShowAt(target, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(target);
    }

    internal MenuFlyout BuildTaskMenu(UnifiedTask task)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Open in Browser", "", () => { if (task.ExternalUrl is { Length: > 0 } url) ExternalBrowser.Open(url); },
            enabled: !string.IsNullOrEmpty(task.ExternalUrl)));
        menu.Items.Add(Item("Copy Link", "", () => Copy(task.ExternalUrl), enabled: !string.IsNullOrEmpty(task.ExternalUrl)));
        menu.Items.Add(Item("Copy ID", "", () => Copy(TaskCardText.CopyId(task))));
        if (task.Provider != "azdevops" || !int.TryParse(task.Id, out var id)) return menu;

        menu.Items.Add(new MenuFlyoutSeparator());
        var state = Sub("Set State", "");
        var priority = Sub("Set Priority", "");
        foreach (var p in TaskBoard.Priorities.Where(p => p.Key > 0))
            priority.Items.Add(Pick(p.Label, task.Priority == p.Key, () => _ = SetPriorityAsync(task, p.Key)));
        var assign = Sub("Assign To", "");
        var area = Sub("Set Area Path", "");
        var iteration = Sub("Set Iteration", "");
        var type = Sub("Set Type", "");
        var reschedule = Sub("Reschedule", "");
        foreach (var (label, choice) in new[] { ("Tomorrow", "tomorrow"), ("Next Monday", "monday"), ("End of Month", "month") })
            reschedule.Items.Add(Item(label, null, () => _ = RescheduleAsync(task, id, choice)));
        var branch = Sub("Create Branch…", "");
        foreach (var sub in new[] { state, priority, assign, area, iteration, type, reschedule }) menu.Items.Add(sub);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(branch);
        menu.Items.Add(Item("Create New Alike…", "", () => _ = ShowCreateWorkItemAsync(WorkItemPrefill.FromTask(task))));

        var dynamic = new[] { (state, "state"), (assign, "assign"), (area, "area"), (iteration, "iteration"), (type, "type"), (branch, "branch") };
        foreach (var (sub, _) in dynamic) sub.Items.Add(new MenuFlyoutItem { Text = "Loading…", IsEnabled = false });
        menu.Opening += async (_, _) =>
        {
            foreach (var (sub, key) in dynamic) await FillAsync(sub, key, task, id);
        };
        return menu;
    }

    /// <summary>Fill a submenu from the project's option lists; the current value shows checked and does nothing.</summary>
    private async Task FillAsync(MenuFlyoutSubItem sub, string key, UnifiedTask task, int id)
    {
        try
        {
            var entries = await EntriesAsync(key, task);
            sub.Items.Clear();
            if (entries.Count == 0)
            {
                sub.Items.Add(new MenuFlyoutItem { Text = _services.DemoMode ? "Not in demo mode" : "Nothing to choose", IsEnabled = false });
                return;
            }
            foreach (var (header, value, current) in entries)
                sub.Items.Add(key == "branch"
                    ? Item(header, null, () => _ = CreateBranchAsync(task, id, value))
                    : Pick(header, current, () => ApplyAsync(task, id, key, value)));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[projects] Could not load the {Key} menu", key);
            sub.Items.Clear();
            sub.Items.Add(new MenuFlyoutItem { Text = "Could not load", IsEnabled = false });
        }
    }

    private async Task<List<(string Header, string Value, bool Current)>> EntriesAsync(string key, UnifiedTask task)
    {
        var entries = new List<(string, string, bool)>();
        if (_services.DevOpsService is not { } devOps) return entries;
        var project = ProjectOf(task);
        var options = OptionsFor(project);
        var p = string.IsNullOrEmpty(project) ? null : project;
        var typeName = TaskCardText.TypeName(task);
        switch (key)
        {
            case "state":
                if (!options.States.TryGetValue(typeName, out var states))
                    options.States[typeName] = states = await devOps.GetWorkItemTypeStatesAsync(typeName, p);
                if (states.Count == 0) states = new() { "New", "Active", "Resolved", "Closed" };
                var current = TaskBoard.Meta(task, "state");
                entries.AddRange(states.Select(s => (s, s, string.Equals(s, current, StringComparison.OrdinalIgnoreCase))));
                break;
            case "assign":
                options.Members ??= await devOps.GetTeamMembersAsync(p);
                var assigned = TaskBoard.Meta(task, "assignedToUniqueName");
                entries.Add(("Unassigned", "", assigned.Length == 0));
                entries.AddRange(options.Members.Select(m => (m.DisplayName, m.UniqueName,
                    string.Equals(m.UniqueName, assigned, StringComparison.OrdinalIgnoreCase))));
                break;
            case "area":
                options.Areas ??= await devOps.GetAreaPathsAsync(p);
                entries.AddRange(options.Areas.Select(a => (a, a, a == TaskCardText.AreaPath(task))));
                break;
            case "iteration":
                options.Iterations ??= await devOps.GetIterationPathsAsync(p);
                var iteration = TaskBoard.Meta(task, "iterationPath");
                entries.AddRange(options.Iterations.Select(i => (i, i, i == iteration)));
                break;
            case "type":
                options.Types ??= await devOps.GetWorkItemTypeNamesAsync(p);
                entries.AddRange(options.Types.Select(t => (t, t, t == typeName)));
                break;
            case "branch":
                options.Repositories ??= await devOps.GetRepositoriesAsync(p);
                entries.AddRange(options.Repositories.Select(r => (r.Name, r.Id, false)));
                break;
        }
        return entries;
    }

    private async void ApplyAsync(UnifiedTask task, int id, string key, string value)
    {
        if (_services.DemoMode || _services.DevOpsService is not { } devOps)
        {
            ShowAction("Demo data can't be changed.", InfoBarSeverity.Informational);
            return;
        }
        var result = key switch
        {
            "state" => await devOps.SetWorkItemFieldAsync(id, "System.State", value, "add"),
            "assign" => await devOps.SetWorkItemFieldAsync(id, "System.AssignedTo", value),
            "area" => await devOps.SetWorkItemFieldAsync(id, "System.AreaPath", value),
            "iteration" => await devOps.SetWorkItemFieldAsync(id, "System.IterationPath", value),
            "type" => await devOps.SetWorkItemFieldAsync(id, "System.WorkItemType", value, "add"),
            _ => PullRequestActionResult.Failed($"Unknown menu action {key}"),
        };
        if (!result.Success)
        {
            ShowAction($"Could not update #{id}: {result.Error}", InfoBarSeverity.Warning);
            return;
        }
        await _services.RefreshProjectTaskAsync(task);
        ViewModel.Rebuild();
    }

    private async Task SetPriorityAsync(UnifiedTask task, int priority)
    {
        if (_services.DemoMode)
        {
            ShowAction("Demo data can't be changed.", InfoBarSeverity.Informational);
            return;
        }
        if (!await _services.UpdateProjectTaskAsync(task, new UpdateWorkItemRequest { Priority = priority }))
            ShowAction($"Azure DevOps did not accept the priority for #{task.Id}.", InfoBarSeverity.Warning);
        else ViewModel.Rebuild();
    }

    private async Task RescheduleAsync(UnifiedTask task, int id, string choice)
    {
        if (_services.DemoMode || _services.DevOpsService is not { } devOps)
        {
            ShowAction("Demo data can't be changed.", InfoBarSeverity.Informational);
            return;
        }
        var due = TaskBoard.RescheduleDate(choice, DateTime.Today).ToString("yyyy-MM-dd");
        var result = await devOps.SetWorkItemFieldAsync(id, "Microsoft.VSTS.Scheduling.DueDate", due);
        if (!result.Success)
        {
            ShowAction($"Could not reschedule #{id}: {result.Error}", InfoBarSeverity.Warning);
            return;
        }
        await _services.RefreshProjectTaskAsync(task);
        ViewModel.Rebuild();
    }

    private async Task CreateBranchAsync(UnifiedTask task, int id, string repoId)
    {
        var repo = OptionsFor(ProjectOf(task)).Repositories?.FirstOrDefault(r => r.Id == repoId);
        if (repo == null || _services.DevOpsService is not { } devOps) return;
        var result = await devOps.CreateBranchForWorkItemAsync(repo, id, task.Title);
        if (result.Success)
            ShowAction($"Created {AzureDevOpsService.BranchNameFor(id, task.Title)} in {repo.Name} and linked it to #{id}.", InfoBarSeverity.Success);
        else
            ShowAction($"Could not create the branch: {result.Error}", InfoBarSeverity.Warning);
    }

    private static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private static MenuFlyoutItem Item(string text, string? glyph, Action action, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        if (glyph != null) item.Icon = new FontIcon { Glyph = glyph };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuFlyoutSubItem Sub(string text, string glyph) => new() { Text = text, Icon = new FontIcon { Glyph = glyph } };

    private static ToggleMenuFlyoutItem Pick(string text, bool current, Action action)
    {
        var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = current, IsEnabled = !current };
        item.Click += (_, _) => action();
        return item;
    }

    // MARK: - New

    private void NewMenu_Opening(object sender, object e)
    {
        NewWorkItemMenu.Visibility = ProjectsViewModel.Show(_services.CanCreateWorkItem);
        NewIssueMenu.Visibility = ProjectsViewModel.Show(_services.CanCreateGitHubItem);
        NewProjectMenu.IsEnabled = _services.CanCreateGitHubItem;
    }

    private async void NewWorkItem_Click(object sender, RoutedEventArgs e) => await ShowCreateWorkItemAsync(null);

    private async Task ShowCreateWorkItemAsync(WorkItemPrefill? prefill)
    {
        if (_services.DemoMode || _services.DevOpsService is not { } devOps)
        {
            ShowAction(_services.DemoMode ? "Demo data can't be changed." : "Azure DevOps is not configured.", InfoBarSeverity.Informational);
            return;
        }
        var dialog = new CreateWorkItemDialog(devOps, prefill) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ShowAction($"Created #{dialog.Created?.Id} {dialog.Created?.Title}.", InfoBarSeverity.Success);
            await LoadAsync(force: true);
        }
    }

    private async void NewIssue_Click(object sender, RoutedEventArgs e)
    {
        if (_services.DemoMode)
        {
            ShowAction("Demo data can't be changed.", InfoBarSeverity.Informational);
            return;
        }
        var dialog = new CreateIssueDialog(_services.Config.GitHubProviderOrDefault(), _services.GitHubProjectId, _services.GitHubProjectStatus)
        {
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ShowAction("Created the issue.", InfoBarSeverity.Success);
            await LoadAsync(force: true);
        }
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (_services.DemoMode)
        {
            ShowAction("Demo data can't be changed.", InfoBarSeverity.Informational);
            return;
        }
        var service = new GitHubProjectsService(_services.Config.GitHubProviderOrDefault());
        if (!await service.AuthenticateAsync())
        {
            ShowAction("GitHub authentication failed.", InfoBarSeverity.Warning);
            return;
        }
        var dialog = new CreateProjectDialog(service, _services.Config) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ShowAction("Created the project.", InfoBarSeverity.Success);
            await LoadAsync(force: true);
        }
    }
}
