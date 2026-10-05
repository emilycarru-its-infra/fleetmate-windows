using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Projects.Tasks;
using FleetMate.GUI.Views.Shared;
using Serilog;

namespace FleetMate.GUI.Views.Projects;

/// <summary>
/// The Azure DevOps work item sidebar, after the macOS AzDoTaskSidebarView:
/// header actions (Edit / Save All, Done or Reactivate, Delete, copy link,
/// open in the browser), parent, metadata, description, Development (code
/// links and Link to Code), relations, the discussion with reactions, effort,
/// repro steps, acceptance criteria and dates.
/// </summary>
public partial class TaskDetailPanel
{
    /// <summary>Set by the page; falls back to the app's shared service.</summary>
    public AzureDevOpsService? DevOpsService { get; set; }

    private AzureDevOpsService? Service => DevOpsService ?? (Application.Current as App)?.DevOpsService;

    private static readonly string[] TerminalStates = { "closed", "done", "resolved", "completed" };
    private static readonly string[] ActiveStates = { "active", "new", "to do", "doing", "in progress", "open" };
    private static readonly string[] WorkItemTypes = { "Bug", "Task", "User Story", "Feature", "Epic", "Issue" };
    internal static readonly (string Type, string Emoji)[] Reactions =
        { ("like", "👍"), ("dislike", "👎"), ("heart", "❤️"), ("hooray", "🎉"), ("confused", "😕"), ("laugh", "😄") };

    private static readonly Brush Alert = new SolidColorBrush(Color.FromRgb(0xF7, 0x63, 0x0C));

    private WorkItemDetail? _detail;
    private WorkItemDetail? _parent;
    private Dictionary<int, WorkItem> _linked = new();
    private List<WorkItemDiscussionComment> _comments = new();
    private List<string> _states = new();
    private List<DevOpsMember> _members = new();
    private List<string> _areas = new();
    private List<string> _iterations = new();
    private static HashSet<string>? _me;
    private bool _editing;
    private int _loadToken;
    private string? _actionError;
    private EditForm? _form;

    /// <summary>The edit-mode controls, read back by Save All.</summary>
    private sealed class EditForm
    {
        public TextBox Title = null!;
        public ComboBox State = null!, Type = null!, Priority = null!, Assignee = null!, Area = null!, Iteration = null!;
        public DatePicker Due = null!;
        public TextBox Tags = null!, Description = null!, Original = null!, Remaining = null!, Completed = null!;
        public TextBox? Repro, Acceptance;
        public string DescriptionStart = "", ReproStart = "", AcceptanceStart = "";
    }

    private async void LoadWorkItemSidebar(UnifiedTask task)
    {
        if (Service is not { } service || !int.TryParse(task.Id, out var id))
        {
            RenderAzDoDetail(task);
            return;
        }

        var token = ++_loadToken;
        _detail = null;
        _editing = false;
        _actionError = null;
        ContentPanel.Children.Add(Muted("Loading work item…"));

        var detail = await service.GetWorkItemDetailAsync(id);
        if (token != _loadToken) return;
        if (detail == null)
        {
            ContentPanel.Children.Clear();
            RenderAzDoDetail(task);
            return;
        }

        _detail = detail;
        TaskTitle.Text = detail.Title;
        Render();

        // Everything else fills in behind the first paint.
        var project = detail.Project;
        var commentsTask = service.GetDiscussionAsync(id, project);
        var parentTask = detail.ParentId is int pid ? service.GetWorkItemDetailAsync(pid) : Task.FromResult<WorkItemDetail?>(null);
        var linkedIds = detail.Relations.Where(r => r.Kind is not (WorkItemRelationKind.Artifact or WorkItemRelationKind.Parent))
            .Select(r => r.LinkedWorkItemId).OfType<int>().Distinct().ToList();
        var linkedTask = linkedIds.Count > 0 ? service.GetWorkItemsByIdsAsync(linkedIds) : Task.FromResult(new List<WorkItem>());
        var statesTask = SafeAsync(() => service.GetWorkItemTypeStatesAsync(detail.Type, project));
        _me ??= await SignedInNamesAsync(service);

        _comments = await commentsTask;
        _parent = await parentTask;
        _linked = (await linkedTask).ToDictionary(w => w.Id);
        _states = await statesTask ?? new();
        if (token != _loadToken) return;
        Render();
    }

    /// <summary>
    /// Everything the signed-in user's comments can carry: the identity id,
    /// the account, the display name and the SSO user name. Under SSO the
    /// identity lookup returns the id with no account or name.
    /// </summary>
    private static async Task<HashSet<string>> SignedInNamesAsync(AzureDevOpsService service)
    {
        var identity = await SafeAsync(service.GetCurrentIdentityAsync);
        return new[] { identity?.Id, identity?.Account, identity?.DisplayName, service.SsoUserName }
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static bool IsOwnComment(WorkItemDiscussionComment c, IReadOnlySet<string>? me) =>
        me != null && new[] { c.AuthorId, c.AuthorUniqueName, c.Author }.Any(n => n != null && me.Contains(n));

    private static async Task<T?> SafeAsync<T>(Func<Task<T>> load)
    {
        try { return await load(); }
        catch (Exception ex) { Log.Debug(ex, "[projects] sidebar option list unavailable"); return default; }
    }

    private void Render()
    {
        if (_detail is not { } d) return;
        var scroll = (ContentPanel.Parent as ScrollViewer)?.VerticalOffset ?? 0;
        ContentPanel.Children.Clear();

        ContentPanel.Children.Add(HeaderActions(d));
        if (_actionError != null)
            ContentPanel.Children.Add(new TextBlock { Text = _actionError, Foreground = Alert, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });

        if (_parent != null) ContentPanel.Children.Add(ParentLink(_parent));
        if (_editing) RenderEditFields(d); else RenderFields(d);

        RenderLongText("Description", d.Description, _form?.Description, "No description provided.");
        RenderDevelopment(d);
        RenderRelations(d);

        var isBug = d.Type.Equals("Bug", StringComparison.OrdinalIgnoreCase);
        var isStory = d.Type.Equals("User Story", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(d.ReproSteps) || (_editing && isBug)) RenderLongText("Repro Steps", d.ReproSteps, _form?.Repro, null);
        if (!string.IsNullOrEmpty(d.AcceptanceCriteria) || (_editing && isStory)) RenderLongText("Acceptance Criteria", d.AcceptanceCriteria, _form?.Acceptance, null);

        RenderAddComment(d);
        RenderComments(d);
        if (_editing || d.HasEffort) RenderEffort(d);
        RenderDates(d);

        (ContentPanel.Parent as ScrollViewer)?.ScrollToVerticalOffset(scroll);
    }

    // MARK: - Header

    private FrameworkElement HeaderActions(WorkItemDetail d)
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        Button Icon(string glyph, string tip, RoutedEventHandler click)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 13 },
                ToolTip = tip, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 4, 4),
            };
            AutomationProperties(b, tip);
            b.Click += click;
            return b;
        }

        if (_editing)
        {
            var save = new Button { Content = "Save All", Margin = new Thickness(0, 0, 4, 4), Style = TryStyle("AccentButtonStyle") };
            save.Click += async (_, _) => await SaveAllAsync();
            var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 4) };
            cancel.Click += (_, _) => { _editing = false; _form = null; Render(); };
            bar.Children.Add(save);
            bar.Children.Add(cancel);
        }
        else
        {
            var edit = new Button { Content = "Edit", Margin = new Thickness(0, 0, 8, 4) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(edit, "WorkItemEdit");
            edit.Click += async (_, _) => await EnterEditAsync();
            bar.Children.Add(edit);
        }

        var states = _states.Count > 0 ? _states : new List<string> { "New", "Active", "Resolved", "Closed" };
        var terminal = states.Where(s => TerminalStates.Contains(s.ToLowerInvariant())).ToList();
        var done = terminal.FirstOrDefault(s => s.Equals("Closed", StringComparison.OrdinalIgnoreCase))
                   ?? terminal.FirstOrDefault(s => s.Equals("Done", StringComparison.OrdinalIgnoreCase)) ?? terminal.FirstOrDefault();
        var active = states.Where(s => ActiveStates.Contains(s.ToLowerInvariant())).ToList();
        var reopen = active.FirstOrDefault(s => s.Equals("Active", StringComparison.OrdinalIgnoreCase))
                     ?? active.FirstOrDefault(s => s.Equals("New", StringComparison.OrdinalIgnoreCase)) ?? active.FirstOrDefault();
        var isTerminal = TerminalStates.Contains(d.State.ToLowerInvariant());

        if (!isTerminal && done != null) bar.Children.Add(Icon("", done, async (_, _) => await QuickStateAsync(done)));
        if (isTerminal && reopen != null) bar.Children.Add(Icon("", $"Reactivate ({reopen})", async (_, _) => await QuickStateAsync(reopen)));
        bar.Children.Add(Icon("", "Delete", async (_, _) => await RemoveAsync()));
        bar.Children.Add(Icon("", "Copy link", (_, _) => { try { Clipboard.SetText(d.WebUrl); } catch { } }));
        bar.Children.Add(Icon("", "Open in Azure DevOps", (_, _) => OpenUrl(d.WebUrl)));

        var more = Icon("", "More actions", (_, _) => { });
        var menu = new ContextMenu();
        var notifications = new MenuItem { Header = "Notification Settings…" };
        notifications.Click += (_, _) => OpenUrl(NotificationSettingsUrl(d.WebUrl));
        menu.Items.Add(notifications);
        more.Click += (_, _) => { menu.PlacementTarget = more; menu.IsOpen = true; };
        bar.Children.Add(more);
        return bar;
    }

    private Style? TryStyle(string key) => TryFindResource(key) as Style;

    private static void AutomationProperties(FrameworkElement e, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(e, name);

    /// <summary>The org's notification settings page, from a work item URL.</summary>
    internal static string NotificationSettingsUrl(string workItemUrl)
    {
        var marker = workItemUrl.IndexOf("/_workitems", StringComparison.OrdinalIgnoreCase);
        var projectRoot = marker > 0 ? workItemUrl[..marker] : workItemUrl;
        var orgRoot = projectRoot[..projectRoot.LastIndexOf('/')];
        return $"{orgRoot}/_usersSettings/notifications";
    }

    private FrameworkElement ParentLink(WorkItemDetail parent)
    {
        var text = $"↑ {parent.Type} #{parent.Id} {parent.Title}" + (parent.BoardColumn is { Length: > 0 } col ? $" · {col}" : "");
        return LinkButton(text, () => OpenWorkItem(parent.Id), 12);
    }

    // MARK: - Fields

    private void RenderFields(WorkItemDetail d)
    {
        AddSection("Details");
        AddMetadataRow("State", d.State + (string.IsNullOrEmpty(d.Reason) ? "" : $" ({d.Reason})"));
        AddMetadataRow("Type", d.Type);
        AddMetadataRow("Priority", PriorityLabel(d.Priority));
        AddMetadataRow("Due Date", d.DueDate?.ToLocalTime().ToString("yyyy-MM-dd") ?? "–");
        AddMetadataRow("Assigned To", d.AssignedTo ?? "Unassigned");
        AddMetadataRow("Area Path", d.AreaPath ?? "–");
        AddMetadataRow("Iteration", d.IterationPath ?? "–");
        if (d.TagList.Count > 0) AddTagsPanel(d.TagList);
    }

    internal static string PriorityLabel(int? priority) => priority switch
    {
        1 => "1 – Critical", 2 => "2 – High", 3 => "3 – Medium", 4 => "4 – Low", _ => "–",
    };

    private async Task EnterEditAsync()
    {
        if (_detail is not { } d || Service is not { } service) return;
        var project = d.Project;
        var members = SafeAsync(() => service.GetTeamMembersAsync(project));
        var areas = SafeAsync(() => service.GetAreaPathsAsync(project));
        var iterations = SafeAsync(() => service.GetIterationPathsAsync(project));
        _members = await members ?? new();
        _areas = await areas ?? new();
        _iterations = await iterations ?? new();
        _editing = true;
        _form = null;
        Render();
    }

    /// <summary>
    /// The form is built once per edit session and re-attached on every
    /// render, so a reaction or a posted comment never discards typed edits.
    /// </summary>
    private void RenderEditFields(WorkItemDetail d)
    {
        var f = _form ??= BuildForm(d);
        AddSection("Title");
        ContentPanel.Children.Add(Detach(f.Title));
        AddSection("Details");
        AddField("State", f.State);
        AddField("Type", f.Type);
        AddField("Priority", f.Priority);
        AddField("Due Date", f.Due);
        AddField("Assigned To", f.Assignee);
        AddField("Area Path", f.Area);
        AddField("Iteration", f.Iteration);
        AddField("Tags", f.Tags);
    }

    /// <summary>Take a reused control out of the panel it sat in on the previous render.</summary>
    private static T Detach<T>(T control) where T : FrameworkElement
    {
        if (control.Parent is Panel panel) panel.Children.Remove(control);
        return control;
    }

    private EditForm BuildForm(WorkItemDetail d)
    {
        var f = new EditForm();
        f.Title = new TextBox { Text = d.Title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };

        f.State = Combo(_states.Count > 0 ? _states : new() { d.State }, d.State);
        f.Type = Combo(WorkItemTypes.Union(new[] { d.Type }).ToList(), d.Type);
        f.Priority = Combo(new() { "1 – Critical", "2 – High", "3 – Medium", "4 – Low" }, PriorityLabel(d.Priority));
        f.Due = new DatePicker { SelectedDate = d.DueDate?.ToLocalTime().Date };

        f.Assignee = new ComboBox { IsEditable = _members.Count == 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        f.Assignee.Items.Add(new ComboBoxItem { Content = "Unassigned", Tag = "" });
        foreach (var m in _members) f.Assignee.Items.Add(new ComboBoxItem { Content = m.DisplayName, Tag = m.UniqueName });
        var current = f.Assignee.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, d.AssignedToUniqueName ?? "", StringComparison.OrdinalIgnoreCase));
        if (current == null && !string.IsNullOrEmpty(d.AssignedToUniqueName))
            f.Assignee.Items.Add(current = new ComboBoxItem { Content = d.AssignedTo ?? d.AssignedToUniqueName, Tag = d.AssignedToUniqueName });
        f.Assignee.SelectedItem = current ?? f.Assignee.Items[0];
        f.Area = Combo(_areas.Count > 0 ? _areas : new() { d.AreaPath ?? "" }, d.AreaPath ?? "");
        f.Iteration = Combo(_iterations.Count > 0 ? _iterations : new() { d.IterationPath ?? "" }, d.IterationPath ?? "");
        f.Tags = new TextBox { Text = string.Join("; ", d.TagList) };

        f.DescriptionStart = TaskLightboxWindow.HtmlToText(d.Description ?? "");
        f.Description = LongBox(f.DescriptionStart);
        f.ReproStart = TaskLightboxWindow.HtmlToText(d.ReproSteps ?? "");
        f.Repro = LongBox(f.ReproStart);
        f.AcceptanceStart = TaskLightboxWindow.HtmlToText(d.AcceptanceCriteria ?? "");
        f.Acceptance = LongBox(f.AcceptanceStart);
        f.Original = EffortBox(d.OriginalEstimate);
        f.Remaining = EffortBox(d.RemainingWork);
        f.Completed = EffortBox(d.CompletedWork);
        return f;
    }

    private static ComboBox Combo(List<string> items, string selected)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var item in items.Where(i => i.Length > 0).Distinct()) combo.Items.Add(item);
        if (!combo.Items.Contains(selected) && selected.Length > 0) combo.Items.Insert(0, selected);
        combo.SelectedItem = selected;
        return combo;
    }

    private static TextBox LongBox(string text) => new()
    {
        Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxHeight = 260,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private static TextBox EffortBox(double? value) => new() { Text = value?.ToString("0.##") ?? "", Width = 70 };

    private void AddField(string label, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = MediumBrush };
        Detach(control);
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        ContentPanel.Children.Add(grid);
    }

    private Brush MediumBrush => (Brush)FindResource("SystemControlForegroundBaseMediumBrush");

    private async Task SaveAllAsync()
    {
        if (_detail is not { } d || _form is not { } f || Service is not { } service) return;

        static double? Hours(TextBox box) =>
            double.TryParse(box.Text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var v) ? v : null;
        static string? Changed(TextBox? box, string start) => box == null || box.Text == start ? null : TextToHtml(box.Text);

        var edit = new WorkItemEdit
        {
            Title = f.Title.Text,
            State = f.State.SelectedItem as string,
            Type = f.Type.SelectedItem as string,
            Priority = f.Priority.SelectedIndex >= 0 ? f.Priority.SelectedIndex + 1 : null,
            AssignedToUniqueName = (f.Assignee.SelectedItem as ComboBoxItem)?.Tag as string ?? f.Assignee.Text,
            AreaPath = f.Area.SelectedItem as string,
            IterationPath = f.Iteration.SelectedItem as string,
            Tags = f.Tags.Text,
            DueDate = f.Due.SelectedDate,
            Description = Changed(f.Description, f.DescriptionStart),
            ReproSteps = Changed(f.Repro, f.ReproStart),
            AcceptanceCriteria = Changed(f.Acceptance, f.AcceptanceStart),
            OriginalEstimate = Hours(f.Original),
            RemainingWork = Hours(f.Remaining),
            CompletedWork = Hours(f.Completed),
        };

        var result = await service.SaveWorkItemAsync(d, edit);
        if (!result.Success)
        {
            _actionError = result.Error;
            Render();
            return;
        }

        _editing = false;
        _form = null;
        await ReloadAsync();
    }

    /// <summary>Plain text from the edit box as the HTML Azure DevOps stores.</summary>
    internal static string TextToHtml(string text) =>
        string.Join("<br>", WebUtility.HtmlEncode(text.Trim()).Replace("\r\n", "\n").Split('\n'));

    private async Task QuickStateAsync(string state)
    {
        if (_detail is not { } d || Service is not { } service) return;
        var result = await service.SetWorkItemFieldAsync(d.Id, "System.State", state, "add");
        _actionError = result.Success ? null : result.Error;
        await ReloadAsync();
    }

    private async Task RemoveAsync()
    {
        if (_detail is not { } d || Service is not { } service) return;
        var answer = MessageBox.Show(Window.GetWindow(this)!, "This will set the work item state to Removed.",
            "Delete work item?", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        var result = await service.SetWorkItemFieldAsync(d.Id, "System.State", "Removed", "add");
        if (!result.Success)
        {
            _actionError = result.Error;
            Render();
            return;
        }
        TaskUpdated?.Invoke(this, EventArgs.Empty);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-read the item after an edit and tell the page its row changed.</summary>
    private async Task ReloadAsync()
    {
        if (_detail is not { } d || Service is not { } service) return;
        var fresh = await service.GetWorkItemDetailAsync(d.Id);
        if (fresh != null)
        {
            _detail = fresh;
            TaskTitle.Text = fresh.Title;
            var states = await SafeAsync(() => service.GetWorkItemTypeStatesAsync(fresh.Type, fresh.Project));
            if (states?.Count > 0) _states = states;
        }
        Render();
        TaskUpdated?.Invoke(this, EventArgs.Empty);
    }

    // MARK: - Long text

    private void RenderLongText(string title, string? html, TextBox? editor, string? empty)
    {
        AddSection(title);
        if (_editing && editor != null)
        {
            ContentPanel.Children.Add(Detach(editor));
            return;
        }
        if (string.IsNullOrWhiteSpace(html))
        {
            if (empty != null) ContentPanel.Children.Add(Muted(empty));
            return;
        }
        ContentPanel.Children.Add(new MarkdownViewer
        {
            MarkdownText = TaskLightboxWindow.HtmlToText(html), MinHeight = 60, MaxHeight = 320,
        });
    }

    // MARK: - Development

    private void RenderDevelopment(WorkItemDetail d)
    {
        var header = new DockPanel { Margin = new Thickness(0, 12, 0, 4) };
        var link = new Button { Content = "+ Link", Padding = new Thickness(8, 2, 8, 2), FontSize = 12 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(link, "WorkItemLinkCode");
        link.Click += (_, _) => OpenLinkCode(d);
        DockPanel.SetDock(link, Dock.Right);
        header.Children.Add(link);
        header.Children.Add(new TextBlock { Text = "Development", FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        ContentPanel.Children.Add(header);

        var artifacts = d.Relations.Where(r => r.Kind == WorkItemRelationKind.Artifact).ToList();
        if (artifacts.Count == 0)
        {
            ContentPanel.Children.Add(Muted("No linked commits, branches, or pull requests."));
            return;
        }

        foreach (var a in artifacts)
        {
            var row = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
            row.Children.Add(new TextBlock
            {
                Text = (a.Name ?? AzureDevOpsService.ArtifactKind(a.Url)).ToUpperInvariant(), FontSize = 10, Foreground = MediumBrush,
            });
            var label = AzureDevOpsService.ArtifactLabel(a.Url);
            var web = Service?.ArtifactWebUrl(a.Url);
            row.Children.Add(string.IsNullOrEmpty(web) ? new TextBlock { Text = label, FontSize = 12 } : LinkButton(label, () => OpenUrl(web), 12));
            if (!string.IsNullOrEmpty(a.Comment)) row.Children.Add(new TextBlock { Text = a.Comment, FontSize = 11, Foreground = MediumBrush, TextWrapping = TextWrapping.Wrap });
            ContentPanel.Children.Add(row);
        }
    }

    private async void OpenLinkCode(WorkItemDetail d)
    {
        if (Service is not { } service) return;
        var dialog = new LinkCodeDialog(service, d) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) await ReloadAsync();
    }

    // MARK: - Relations

    private void RenderRelations(WorkItemDetail d)
    {
        var relations = d.Relations.Where(r => r.Kind != WorkItemRelationKind.Artifact).ToList();
        if (relations.Count == 0) return;

        AddSection($"Relations ({relations.Count})");
        var order = new[]
        {
            WorkItemRelationKind.Parent, WorkItemRelationKind.Child, WorkItemRelationKind.Related,
            WorkItemRelationKind.Predecessor, WorkItemRelationKind.Successor, WorkItemRelationKind.Other,
        };
        foreach (var kind in order)
        {
            var group = relations.Where(r => r.Kind == kind).ToList();
            if (group.Count == 0) continue;
            ContentPanel.Children.Add(new TextBlock
            {
                Text = (kind == WorkItemRelationKind.Other ? "Other Links" : kind.ToString()).ToUpperInvariant(),
                FontSize = 10, Foreground = MediumBrush, Margin = new Thickness(0, 4, 0, 2),
            });
            foreach (var r in group)
            {
                if (r.LinkedWorkItemId is not int id)
                {
                    ContentPanel.Children.Add(new TextBlock { Text = r.Name ?? r.Url, FontSize = 12, TextWrapping = TextWrapping.Wrap });
                    continue;
                }
                var info = kind == WorkItemRelationKind.Parent && _parent?.Id == id
                    ? $"{_parent.Title} · {_parent.State}"
                    : _linked.TryGetValue(id, out var w) ? $"{w.Title} · {w.State}"
                    + (w.ChangedDate is { } changed ? $" · {ActivityItem.Relative(changed, DateTime.UtcNow)}" : "") : "";
                ContentPanel.Children.Add(LinkButton($"#{id} {info}".Trim(), () => OpenWorkItem(id), 12));
            }
        }
    }

    /// <summary>Open a parent or related item in this sidebar.</summary>
    private async void OpenWorkItem(int id)
    {
        if (Service is not { } service) return;
        var item = await service.GetWorkItemAsync(id);
        if (item != null) ShowTask(AzureDevOpsTaskProvider.MapToUnifiedTask(item), _provider);
    }

    // MARK: - Discussion

    private void RenderAddComment(WorkItemDetail d)
    {
        AddSection("Add Comment");
        var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, MaxHeight = 200 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, "WorkItemCommentBox");
        var send = new Button { Content = "Send", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };

        async Task Submit()
        {
            var text = box.Text.Trim();
            if (text.Length == 0 || Service is not { } service) return;
            send.IsEnabled = false;
            var added = await service.AddDiscussionCommentAsync(d.Id, d.Project, MentionsToHtml(TextToHtml(text), _members));
            send.IsEnabled = true;
            if (added == null)
            {
                _actionError = "Could not post the comment.";
                Render();
                return;
            }
            _comments.Insert(0, added);
            _actionError = null;
            Render();
        }

        send.Click += async (_, _) => await Submit();
        box.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                await Submit();
            }
        };

        ContentPanel.Children.Add(box);
        var footer = new DockPanel();
        DockPanel.SetDock(send, Dock.Right);
        footer.Children.Add(send);
        footer.Children.Add(new TextBlock { Text = "Ctrl+Enter to post", FontSize = 11, Foreground = MediumBrush, VerticalAlignment = VerticalAlignment.Center });
        ContentPanel.Children.Add(footer);
    }

    /// <summary>"@Display Name" becomes the mention anchor Azure DevOps notifies.</summary>
    internal static string MentionsToHtml(string html, IEnumerable<DevOpsMember> members)
    {
        foreach (var m in members.OrderByDescending(m => m.DisplayName.Length))
        {
            var plain = "@" + WebUtility.HtmlEncode(m.DisplayName);
            if (!html.Contains(plain, StringComparison.Ordinal)) continue;
            html = html.Replace(plain,
                $"<a href=\"mailto:{WebUtility.HtmlEncode(m.UniqueName)}\" data-vss-mention=\"version:2.0\">@{WebUtility.HtmlEncode(m.DisplayName)}</a>");
        }
        return html;
    }

    private void RenderComments(WorkItemDetail d)
    {
        AddSection($"Comments ({_comments.Count})");
        if (_comments.Count == 0)
        {
            ContentPanel.Children.Add(Muted("No comments yet."));
            return;
        }
        foreach (var c in _comments) ContentPanel.Children.Add(CommentBubble(d, c));
    }

    private FrameworkElement CommentBubble(WorkItemDetail d, WorkItemDiscussionComment c)
    {
        var body = new StackPanel();
        var bubble = new Border
        {
            Child = body, Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(18, 128, 128, 128)),
        };

        var own = IsOwnComment(c, _me);
        var head = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        actions.Children.Add(SmallIcon("", "Copy link", () => { try { Clipboard.SetText($"{d.WebUrl}#{c.Id}"); } catch { } }));
        if (own)
        {
            actions.Children.Add(SmallIcon("", "Edit", () => EditComment(d, c, body)));
            actions.Children.Add(SmallIcon("", "Delete", async () => await DeleteCommentAsync(d, c)));
        }
        if (c.Created is { } created)
            actions.Children.Add(new TextBlock { Text = ActivityItem.Relative(created, DateTime.UtcNow), FontSize = 11, Foreground = MediumBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
        head.Children.Add(actions);
        head.Children.Add(new TextBlock { Text = c.Author ?? "Unknown", FontWeight = FontWeights.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(head);

        body.Children.Add(new TextBlock { Text = TaskLightboxWindow.HtmlToText(c.Text), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 4) });
        body.Children.Add(ReactionRow(d, c));
        return bubble;
    }

    private FrameworkElement ReactionRow(WorkItemDetail d, WorkItemDiscussionComment c)
    {
        var row = new WrapPanel();
        foreach (var r in c.Reactions)
        {
            var emoji = Reactions.FirstOrDefault(x => x.Type == r.Type).Emoji;
            if (emoji == null) continue;
            var chip = new ToggleButton
            {
                Content = $"{emoji} {r.Count}", IsChecked = r.Engaged, FontSize = 11, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 0),
                ToolTip = r.Engaged ? "Remove your reaction" : "React",
            };
            chip.Click += async (_, _) => await ReactAsync(d, c, r.Type, !r.Engaged);
            row.Children.Add(chip);
        }

        var add = new Button { Content = "☺+", FontSize = 11, Padding = new Thickness(6, 1, 6, 1), ToolTip = "Add reaction" };
        var menu = new ContextMenu();
        foreach (var (type, emoji) in Reactions)
        {
            var item = new MenuItem { Header = emoji, ToolTip = type };
            item.Click += async (_, _) => await ReactAsync(d, c, type, true);
            menu.Items.Add(item);
        }
        add.Click += (_, _) => { menu.PlacementTarget = add; menu.IsOpen = true; };
        row.Children.Add(add);
        return row;
    }

    private async Task ReactAsync(WorkItemDetail d, WorkItemDiscussionComment c, string type, bool add)
    {
        if (Service is not { } service) return;
        if (!await service.SetCommentReactionAsync(d.Id, d.Project, c.Id, type, add))
        {
            _actionError = "Could not update the reaction.";
            Render();
            return;
        }
        c.Reactions = ApplyReaction(c.Reactions, type, add);
        Render();
    }

    /// <summary>The reaction list after the signed-in user adds or removes one, without a refetch.</summary>
    internal static List<CommentReaction> ApplyReaction(List<CommentReaction> reactions, string type, bool add)
    {
        var list = reactions.ToList();
        var index = list.FindIndex(r => r.Type == type);
        var existing = index >= 0 ? list[index] : null;
        if (add && existing?.Engaged == true) return list;
        if (!add && existing?.Engaged != true) return list;

        var count = (existing?.Count ?? 0) + (add ? 1 : -1);
        var updated = new CommentReaction(type, count, add);
        if (index >= 0)
        {
            if (count <= 0) list.RemoveAt(index); else list[index] = updated;
        }
        else if (count > 0)
        {
            list.Add(updated);
        }
        return list;
    }

    private void EditComment(WorkItemDetail d, WorkItemDiscussionComment c, StackPanel body)
    {
        var editor = new TextBox { Text = TaskLightboxWindow.HtmlToText(c.Text), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
        var save = new Button { Content = "Save", Margin = new Thickness(4, 4, 0, 0) };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 4, 0, 0) };
        cancel.Click += (_, _) => Render();
        save.Click += async (_, _) =>
        {
            if (Service is not { } service || editor.Text.Trim().Length == 0) return;
            var updated = await service.UpdateDiscussionCommentAsync(d.Id, d.Project, c.Id, MentionsToHtml(TextToHtml(editor.Text), _members));
            if (updated != null) c.Text = updated.Text; else _actionError = "Could not save the comment.";
            Render();
        };
        body.Children.Clear();
        body.Children.Add(editor);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        body.Children.Add(buttons);
        editor.Focus();
    }

    private async Task DeleteCommentAsync(WorkItemDetail d, WorkItemDiscussionComment c)
    {
        if (Service is not { } service) return;
        if (MessageBox.Show(Window.GetWindow(this)!, "This cannot be undone.", "Delete comment?",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (await service.DeleteDiscussionCommentAsync(d.Id, d.Project, c.Id)) _comments.Remove(c);
        else _actionError = "Could not delete the comment.";
        Render();
    }

    // MARK: - Effort and dates

    private void RenderEffort(WorkItemDetail d)
    {
        AddSection("Effort");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        void Cell(string label, double? value, TextBox? box)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
            cell.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = MediumBrush });
            cell.Children.Add(_editing && box != null ? Detach(box) : new TextBlock { Text = value is { } v ? $"{v:0.#}h" : "–", FontSize = 12 });
            row.Children.Add(cell);
        }
        Cell("Original", d.OriginalEstimate, _form?.Original);
        Cell("Remaining", d.RemainingWork, _form?.Remaining);
        Cell("Completed", d.CompletedWork, _form?.Completed);
        ContentPanel.Children.Add(row);
    }

    private void RenderDates(WorkItemDetail d)
    {
        AddSection("Dates");
        void Row(string label, DateTime? when, string? by)
        {
            if (when is not { } w) return;
            AddMetadataRow(label, $"{w.ToLocalTime():yyyy-MM-dd HH:mm}" + (by != null ? $" by {by}" : ""));
        }
        Row("Created", d.Created, d.CreatedBy);
        Row("Changed", d.Changed, d.ChangedBy);
        Row("State Changed", d.StateChanged, null);
        Row("Resolved", d.Resolved, d.ResolvedBy);
        Row("Closed", d.Closed, d.ClosedBy);
    }

    // MARK: - Small helpers

    private TextBlock Muted(string text) => new()
    {
        Text = text, FontStyle = FontStyles.Italic, FontSize = 12, Foreground = MediumBrush, TextWrapping = TextWrapping.Wrap,
    };

    private static Button LinkButton(string text, Action click, double size)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, Foreground = SystemColors.HotTrackBrush },
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 1, 0, 1),
            HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand,
        };
        b.Click += (_, _) => click();
        return b;
    }

    private static Button SmallIcon(string glyph, string tip, Action click)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 11 },
            ToolTip = tip, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4, 2, 4, 2),
        };
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "Failed to open URL"); }
    }
}
