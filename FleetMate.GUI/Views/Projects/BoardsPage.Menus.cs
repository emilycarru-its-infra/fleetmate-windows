using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Projects.Tasks;
using Serilog;

namespace FleetMate.GUI.Views.Projects;

/// <summary>
/// The card menu shared by board cards and Mine/Recent rows (macOS
/// taskContextMenu): Copy ID for everything; for Azure DevOps work items,
/// State, Priority, Assign To, Area, Iteration, Type, Reschedule, Create
/// Branch and Create New Alike (BoardsPage.Create.cs). Option lists load once per project, the first time a submenu opens.
/// </summary>
public partial class BoardsPage
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

    private static string ProjectOf(UnifiedTask task) =>
        task.Metadata.TryGetValue("project", out var p) && p.Length > 0 ? p : "";

    private static TaskCardVm? VmOfMenu(System.Windows.Controls.ContextMenu? menu) =>
        menu?.PlacementTarget is FrameworkElement { Tag: TaskCardVm vm } ? vm : null;

    private static System.Windows.Controls.ContextMenu? MenuOf(object sender)
    {
        DependencyObject? current = sender as DependencyObject;
        while (current != null && current is not System.Windows.Controls.ContextMenu)
            current = LogicalTreeHelper.GetParent(current) ?? System.Windows.Media.VisualTreeHelper.GetParent(current);
        return current as System.Windows.Controls.ContextMenu;
    }

    /// <summary>Azure DevOps-only items hide for GitHub and Gitea tasks.</summary>
    private void OnTaskMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu menu || VmOfMenu(menu) is not { } vm) return;
        var isAzdo = vm.Task.Provider == "azdevops";
        foreach (var item in menu.Items.OfType<FrameworkElement>())
        {
            if (item.Tag is string tag && tag.StartsWith("azdo", StringComparison.Ordinal))
                item.Visibility = isAzdo ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnTaskCopyId(object sender, RoutedEventArgs e)
    {
        if (VmOfMenu(MenuOf(sender)) is not { } vm) return;
        try { Clipboard.SetText(vm.Task.Provider == "azdevops" ? vm.Task.Id : $"#{vm.Task.Id}"); } catch { }
    }

    /// <summary>Fill a dynamic submenu the first time it opens for this card.</summary>
    private async void OnTaskSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem parent || !ReferenceEquals(e.OriginalSource, parent)) return;
        if (parent.Tag is not string tag || !tag.StartsWith("azdo:", StringComparison.Ordinal)) return;
        if (VmOfMenu(MenuOf(parent)) is not { } vm || _devOpsService == null) return;

        // Already filled for this card.
        if (parent.Items.Count > 0 && parent.Items[0] is MenuItem { IsEnabled: true }) return;

        var key = tag["azdo:".Length..];
        var project = ProjectOf(vm.Task);
        var options = OptionsFor(project);
        var p = string.IsNullOrEmpty(project) ? null : project;

        try
        {
            var entries = new List<(string Header, string Value, bool Current)>();
            switch (key)
            {
                case "state":
                    var type = vm.TypeName;
                    if (!options.States.TryGetValue(type, out var states))
                        options.States[type] = states = await _devOpsService.GetWorkItemTypeStatesAsync(type, p);
                    if (states.Count == 0) states = new() { "New", "Active", "Resolved", "Closed" };
                    var current = vm.Task.Metadata.GetValueOrDefault("state") ?? "";
                    entries.AddRange(states.Select(st => (st, st, string.Equals(st, current, StringComparison.OrdinalIgnoreCase))));
                    break;

                case "assign":
                    options.Members ??= await _devOpsService.GetTeamMembersAsync(p);
                    var assigned = vm.Task.Metadata.GetValueOrDefault("assignedToUniqueName") ?? "";
                    entries.Add(("Unassigned", "", assigned.Length == 0));
                    entries.AddRange(options.Members.Select(m => (m.DisplayName, m.UniqueName,
                        string.Equals(m.UniqueName, assigned, StringComparison.OrdinalIgnoreCase))));
                    break;

                case "area":
                    options.Areas ??= await _devOpsService.GetAreaPathsAsync(p);
                    entries.AddRange(options.Areas.Select(a => (a, a, a == vm.AreaPath)));
                    break;

                case "iteration":
                    options.Iterations ??= await _devOpsService.GetIterationPathsAsync(p);
                    var iteration = vm.Task.Metadata.GetValueOrDefault("iterationPath") ?? "";
                    entries.AddRange(options.Iterations.Select(i => (i, i, i == iteration)));
                    break;

                case "type":
                    options.Types ??= await _devOpsService.GetWorkItemTypeNamesAsync(p);
                    entries.AddRange(options.Types.Select(t => (t, t, t == vm.TypeName)));
                    break;

                case "branch":
                    options.Repositories ??= await _devOpsService.GetRepositoriesAsync(p);
                    entries.AddRange(options.Repositories.Select(r => (r.Name, r.Id, false)));
                    break;
            }

            parent.Items.Clear();
            if (entries.Count == 0)
            {
                parent.Items.Add(new MenuItem { Header = "Nothing to choose", IsEnabled = false });
                return;
            }

            foreach (var (header, value, current) in entries)
            {
                // Current values show checked and do nothing, like the Mac's
                // disabled current state.
                var item = new MenuItem { Header = header, Tag = $"{key}|{value}", IsChecked = current, IsEnabled = !current };
                item.Click += OnTaskMenuPick;
                parent.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[projects] Could not load the {Key} menu", key);
            parent.Items.Clear();
            parent.Items.Add(new MenuItem { Header = "Could not load", IsEnabled = false });
        }
    }

    private async void OnTaskMenuPick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || _devOpsService == null) return;
        if (VmOfMenu(MenuOf(sender)) is not { } vm || !int.TryParse(vm.Task.Id, out var id)) return;

        var split = tag.IndexOf('|');
        var key = tag[..split];
        var value = tag[(split + 1)..];

        PullRequestActionResult result = key switch
        {
            "state" => await _devOpsService.SetWorkItemFieldAsync(id, "System.State", value, "add"),
            "assign" => await _devOpsService.SetWorkItemFieldAsync(id, "System.AssignedTo", value),
            "area" => await _devOpsService.SetWorkItemFieldAsync(id, "System.AreaPath", value),
            "iteration" => await _devOpsService.SetWorkItemFieldAsync(id, "System.IterationPath", value),
            "type" => await _devOpsService.SetWorkItemFieldAsync(id, "System.WorkItemType", value, "add"),
            "branch" => await CreateBranchAsync(vm, id, value),
            _ => PullRequestActionResult.Failed($"Unknown menu action {key}"),
        };

        if (!result.Success)
        {
            MessageBox.Show(Window.GetWindow(this)!, result.Error, "Could not update the work item",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // A new branch changes nothing on the card.
        if (key != "branch") await RefreshTaskAsync(vm.Task, id);
    }

    private async Task<PullRequestActionResult> CreateBranchAsync(TaskCardVm vm, int id, string repoId)
    {
        var repo = OptionsFor(ProjectOf(vm.Task)).Repositories?.FirstOrDefault(r => r.Id == repoId);
        if (repo == null || _devOpsService == null) return PullRequestActionResult.Failed("Repository not found.");

        var result = await _devOpsService.CreateBranchForWorkItemAsync(repo, id, vm.Task.Title);
        if (result.Success)
        {
            var branch = AzureDevOpsService.BranchNameFor(id, vm.Task.Title);
            MessageBox.Show(Window.GetWindow(this)!, $"Created {branch} in {repo.Name} and linked it to #{id}.",
                "Branch created", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        return result;
    }

    /// <summary>Tomorrow / Next Monday / End of Month, sent as the due date (yyyy-MM-dd).</summary>
    internal static DateTime RescheduleDate(string choice, DateTime today) => choice switch
    {
        "tomorrow" => today.AddDays(1),
        // The coming Monday; on a Monday, the next one.
        "monday" => today.AddDays(((int)DayOfWeek.Monday - (int)today.DayOfWeek + 6) % 7 + 1),
        _ => new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)),
    };

    private async void OnTaskReschedule(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string choice } || _devOpsService == null) return;
        if (VmOfMenu(MenuOf(sender)) is not { } vm || !int.TryParse(vm.Task.Id, out var id)) return;

        var due = RescheduleDate(choice, DateTime.Today).ToString("yyyy-MM-dd");
        var result = await _devOpsService.SetWorkItemFieldAsync(id, "Microsoft.VSTS.Scheduling.DueDate", due);
        if (!result.Success)
        {
            MessageBox.Show(Window.GetWindow(this)!, result.Error, "Could not reschedule", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await RefreshTaskAsync(vm.Task, id);
    }

    /// <summary>Re-read one work item and patch its row in place (macOS refreshLocalTask).</summary>
    private async Task RefreshTaskAsync(UnifiedTask task, int id)
    {
        if (_devOpsService == null) return;
        var item = await _devOpsService.GetWorkItemAsync(id);
        if (item == null) return;

        var fresh = AzureDevOpsTaskProvider.MapToUnifiedTask(item);
        ReplaceTask(_allTasks, task, fresh);
        ReplaceTask(_mineExtra, task, fresh);
        UpdateDisplay();
    }

    private static void ReplaceTask(List<UnifiedTask> list, UnifiedTask old, UnifiedTask fresh)
    {
        var index = list.FindIndex(t => t.Provider == old.Provider && t.Id == old.Id);
        if (index >= 0) list[index] = fresh;
    }
}
