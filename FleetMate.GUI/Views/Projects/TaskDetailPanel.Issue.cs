using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using FleetMate.GUI.Views.Shared;
using Serilog;

namespace FleetMate.GUI.Views.Projects;

/// <summary>
/// The GitHub issue sidebar, after the macOS GitHubIssueSidebarView: state
/// and lock in the header, title and body edited in place, assignees,
/// labels, milestone, linked pull request, author and dates, the comment
/// thread with a composer, and Actions (Close or Reopen, Lock or Unlock,
/// Duplicate, Delete).
/// </summary>
public partial class TaskDetailPanel
{
    private sealed record IssueRef(string Owner, string Repo, int Number);

    private IssueRef? _issueRef;
    private GitHubIssueDetail? _issue;
    private List<GitHubComment> _issueComments = new();
    private bool _editingIssueTitle;
    private bool _editingIssueBody;
    private bool _issueActionsOpen;
    private string? _issueNotice;

    private static readonly Regex IssueUrl = new(
        @"^https?://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/issues/(?<number>\d+)", RegexOptions.IgnoreCase);

    /// <summary>Owner, repository and number from an issue's web URL; null for pull requests and drafts.</summary>
    internal static (string Owner, string Repo, int Number)? ParseIssueUrl(string? url)
    {
        var m = url == null ? null : IssueUrl.Match(url);
        return m is { Success: true } ? (m.Groups["owner"].Value, m.Groups["repo"].Value, int.Parse(m.Groups["number"].Value)) : null;
    }

    private static GitHubProviderConfig GitHubConfig =>
        (Application.Current as App)?.Config?.GitHubProviderOrDefault() ?? new GitHubProviderConfig();

    /// <summary>Run one call against a fresh service and dispose it, as the Mac sidebar does.</summary>
    private static async Task<T> WithGitHubAsync<T>(Func<GitHubProjectsService, Task<T>> call)
    {
        using var service = new GitHubProjectsService(GitHubConfig);
        return await call(service);
    }

    private async void LoadIssueSidebar(UnifiedTask task)
    {
        if (ParseIssueUrl(task.ExternalUrl) is not { } parsed)
        {
            RenderGitHubDetail(task);
            return;
        }

        var token = ++_loadToken;
        _issueRef = new IssueRef(parsed.Owner, parsed.Repo, parsed.Number);
        _issue = null;
        _issueComments = new();
        _editingIssueTitle = _editingIssueBody = _issueActionsOpen = false;
        _actionError = _issueNotice = null;
        ContentPanel.Children.Add(Muted("Loading issue…"));

        try
        {
            var (detail, comments) = await WithGitHubAsync(async s =>
            {
                var d = s.GetIssueDetailAsync(parsed.Owner, parsed.Repo, parsed.Number);
                var c = s.ListIssueCommentsAsync(parsed.Owner, parsed.Repo, parsed.Number);
                return (await d, await c);
            });
            if (token != _loadToken) return;
            _issue = detail;
            _issueComments = comments;
            TaskTitle.Text = detail.Title;
            TaskId.Text = $"{parsed.Owner}/{parsed.Repo}#{parsed.Number}";
            RenderIssue();
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return;
            Log.Warning(ex, "[projects] issue {Repo}#{Number} unavailable", parsed.Repo, parsed.Number);
            ContentPanel.Children.Clear();
            RenderGitHubDetail(task);
        }
    }

    private void RenderIssue()
    {
        if (_issue is not { } issue) return;
        var scroll = (ContentPanel.Parent as ScrollViewer)?.VerticalOffset ?? 0;
        ContentPanel.Children.Clear();

        ContentPanel.Children.Add(IssueHeader(issue));
        if (_actionError != null)
            ContentPanel.Children.Add(new TextBlock { Text = _actionError, Foreground = Alert, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        if (_issueNotice != null)
            ContentPanel.Children.Add(new TextBlock { Text = _issueNotice, Foreground = MediumBrush, TextWrapping = TextWrapping.Wrap, FontSize = 12 });

        RenderIssueTitle(issue);
        RenderIssueBody(issue);
        RenderIssueMetadata(issue);
        RenderIssueComments();
        RenderIssueComposer();
        RenderIssueActions(issue);

        (ContentPanel.Parent as ScrollViewer)?.ScrollToVerticalOffset(scroll);
    }

    private FrameworkElement IssueHeader(GitHubIssueDetail issue)
    {
        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        var open = issue.State == "open";
        bar.Children.Add(new Border
        {
            Background = new SolidColorBrush(open ? Color.FromRgb(0x27, 0xAE, 0x60) : Color.FromRgb(0x8B, 0x5C, 0xF6)),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = open ? "Open" : "Closed", Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold },
        });
        if (issue.Locked)
            bar.Children.Add(new TextBlock
            {
                Text = " Locked", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe UI"), Foreground = Alert, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4),
            });
        bar.Children.Add(SmallIcon("", "Copy link", () => { try { Clipboard.SetText(issue.HtmlUrl); } catch { } }));
        bar.Children.Add(SmallIcon("", "Open in GitHub", () => OpenUrl(issue.HtmlUrl)));
        return bar;
    }

    // MARK: - Title and body

    private void RenderIssueTitle(GitHubIssueDetail issue)
    {
        if (!_editingIssueTitle)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var edit = SmallIcon("", "Edit title", () => { _editingIssueTitle = true; RenderIssue(); });
            DockPanel.SetDock(edit, Dock.Right);
            row.Children.Add(edit);
            row.Children.Add(new TextBlock { Text = issue.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            ContentPanel.Children.Add(row);
            return;
        }

        var box = new TextBox { Text = issue.Title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        ContentPanel.Children.Add(box);
        ContentPanel.Children.Add(SaveCancel(
            async () => await UpdateIssueAsync(s => s.UpdateIssueAsync(_issueRef!.Owner, _issueRef.Repo, _issueRef.Number, title: box.Text.Trim()),
                () => _editingIssueTitle = false),
            () => { _editingIssueTitle = false; RenderIssue(); },
            () => box.Text.Trim().Length > 0));
        box.Focus();
    }

    private void RenderIssueBody(GitHubIssueDetail issue)
    {
        var header = new DockPanel { Margin = new Thickness(0, 12, 0, 4) };
        if (!_editingIssueBody)
        {
            var edit = SmallIcon("", "Edit description", () => { _editingIssueBody = true; RenderIssue(); });
            DockPanel.SetDock(edit, Dock.Right);
            header.Children.Add(edit);
        }
        header.Children.Add(new TextBlock { Text = "Description", FontWeight = FontWeights.SemiBold, FontSize = 13 });
        ContentPanel.Children.Add(header);

        if (_editingIssueBody)
        {
            var box = new TextBox
            {
                Text = issue.Body ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, MaxHeight = 360,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            };
            ContentPanel.Children.Add(box);
            ContentPanel.Children.Add(SaveCancel(
                async () => await UpdateIssueAsync(s => s.UpdateIssueAsync(_issueRef!.Owner, _issueRef.Repo, _issueRef.Number, body: box.Text),
                    () => _editingIssueBody = false),
                () => { _editingIssueBody = false; RenderIssue(); },
                () => true));
            return;
        }

        if (string.IsNullOrWhiteSpace(issue.Body)) ContentPanel.Children.Add(Muted("No description provided."));
        else ContentPanel.Children.Add(new MarkdownViewer { MarkdownText = issue.Body, MinHeight = 60, MaxHeight = 360 });
    }

    private static FrameworkElement SaveCancel(Func<Task> save, Action cancel, Func<bool> canSave)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 4) };
        var cancelButton = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 4, 0) };
        var saveButton = new Button { Content = "Save" };
        cancelButton.Click += (_, _) => cancel();
        saveButton.Click += async (_, _) =>
        {
            if (!canSave()) return;
            saveButton.IsEnabled = false;
            await save();
            saveButton.IsEnabled = true;
        };
        row.Children.Add(cancelButton);
        row.Children.Add(saveButton);
        return row;
    }

    /// <summary>Apply an edit that returns the updated issue, then re-render.</summary>
    private async Task UpdateIssueAsync(Func<GitHubProjectsService, Task<GitHubIssueDetail>> call, Action? onSuccess = null)
    {
        try
        {
            _issue = await WithGitHubAsync(call);
            _actionError = null;
            onSuccess?.Invoke();
            TaskTitle.Text = _issue.Title;
            TaskUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _actionError = ex.Message;
        }
        RenderIssue();
    }

    // MARK: - Metadata

    private void RenderIssueMetadata(GitHubIssueDetail issue)
    {
        AddSection("Details");
        AddMetadataRow("Assignees", issue.Assignees.Count > 0 ? string.Join(", ", issue.Assignees.Select(a => a.Login)) : "No assignees");

        if (issue.Labels.Count == 0)
        {
            AddMetadataRow("Labels", "No labels");
        }
        else
        {
            var labels = new WrapPanel { Margin = new Thickness(0, 2, 0, 2) };
            foreach (var label in issue.Labels)
            {
                labels.Children.Add(new Border
                {
                    Background = new SolidColorBrush(LabelColor(label.Color, 70)),
                    CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 4),
                    Child = new TextBlock { Text = label.Name, FontSize = 11 },
                });
            }
            ContentPanel.Children.Add(labels);
        }

        AddMetadataRow("Milestone", issue.Milestone is { } ms
            ? ms.Title + (ms.DueOn is { } due ? $" · due {due.ToLocalTime():yyyy-MM-dd}" : "")
            : "No milestone");

        if (issue.PullRequest?.HtmlUrl is { Length: > 0 } prUrl)
            ContentPanel.Children.Add(LinkButton($"Pull request #{prUrl.Split('/').Last()}{(issue.PullRequest.MergedAt != null ? " (merged)" : "")}", () => OpenUrl(prUrl), 12));

        if (issue.User is { } user) AddMetadataRow("Opened by", user.Login);
        AddMetadataRow("Created", issue.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        AddMetadataRow("Updated", issue.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        if (issue.ClosedAt is { } closed) AddMetadataRow("Closed", closed.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
    }

    /// <summary>A label's hex colour ("d73a4a") at the given alpha; grey when unreadable.</summary>
    internal static Color LabelColor(string? hex, byte alpha)
    {
        var h = (hex ?? "").TrimStart('#');
        return h.Length == 6 && int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out var v)
            ? Color.FromArgb(alpha, (byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : Color.FromArgb(alpha, 128, 128, 128);
    }

    // MARK: - Comments

    private void RenderIssueComments()
    {
        AddSection($"Comments ({_issueComments.Count})");
        if (_issueComments.Count == 0)
        {
            ContentPanel.Children.Add(Muted("No comments yet."));
            return;
        }

        foreach (var c in _issueComments)
        {
            var body = new StackPanel();
            var head = new DockPanel();
            var when = new TextBlock { Text = ActivityItem.Relative(c.CreatedAt, DateTime.UtcNow), FontSize = 11, Foreground = MediumBrush };
            DockPanel.SetDock(when, Dock.Right);
            head.Children.Add(when);
            head.Children.Add(new TextBlock { Text = c.User?.Login ?? "Unknown", FontWeight = FontWeights.SemiBold, FontSize = 12 });
            body.Children.Add(head);
            body.Children.Add(new TextBlock { Text = c.Body, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) });
            ContentPanel.Children.Add(new Border
            {
                Child = body, Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(18, 128, 128, 128)),
            });
        }
    }

    private void RenderIssueComposer()
    {
        AddSection("Add Comment");
        var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, MaxHeight = 200 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, "IssueCommentBox");
        var send = new Button { Content = "Comment" };

        async Task Submit()
        {
            var text = box.Text.Trim();
            if (text.Length == 0 || _issueRef is not { } r) return;
            send.IsEnabled = false;
            try
            {
                var comment = await WithGitHubAsync(s => s.CreateIssueCommentAsync(r.Owner, r.Repo, r.Number, text));
                _issueComments.Add(comment);
                _actionError = null;
            }
            catch (Exception ex)
            {
                _actionError = ex.Message;
            }
            RenderIssue();
        }

        send.Click += async (_, _) => await Submit();
        box.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
            {
                e.Handled = true;
                await Submit();
            }
        };
        ContentPanel.Children.Add(box);
        var footer = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(send, Dock.Right);
        footer.Children.Add(send);
        footer.Children.Add(new TextBlock { Text = "Supports Markdown · Ctrl+Enter to post", FontSize = 11, Foreground = MediumBrush, VerticalAlignment = VerticalAlignment.Center });
        ContentPanel.Children.Add(footer);
    }

    // MARK: - Actions

    private void RenderIssueActions(GitHubIssueDetail issue)
    {
        var toggle = new Button
        {
            Content = (_issueActionsOpen ? "▾ " : "▸ ") + "Actions", Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 12, 0, 4), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Left,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(toggle, "IssueActions");
        toggle.Click += (_, _) => { _issueActionsOpen = !_issueActionsOpen; RenderIssue(); };
        ContentPanel.Children.Add(toggle);
        if (!_issueActionsOpen || _issueRef is not { } r) return;

        var panel = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        Button Action(string text, Func<Task> run)
        {
            var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6), MinWidth = 140 };
            b.Click += async (_, _) => { b.IsEnabled = false; await run(); b.IsEnabled = true; };
            panel.Children.Add(b);
            return b;
        }

        var open = issue.State == "open";
        Action(open ? "Close Issue" : "Reopen Issue", async () =>
        {
            if (open && !Confirm("Close issue?", $"Close #{r.Number}?")) return;
            await UpdateIssueAsync(s => s.UpdateIssueAsync(r.Owner, r.Repo, r.Number, state: open ? "closed" : "open"));
        });
        Action(issue.Locked ? "Unlock Issue" : "Lock Issue", async () =>
        {
            try
            {
                await WithGitHubAsync(async s =>
                {
                    if (issue.Locked) await s.UnlockIssueAsync(r.Owner, r.Repo, r.Number);
                    else await s.LockIssueAsync(r.Owner, r.Repo, r.Number);
                    return true;
                });
                issue.Locked = !issue.Locked;
                _actionError = null;
            }
            catch (Exception ex) { _actionError = ex.Message; }
            RenderIssue();
        });
        Action("Duplicate Issue", async () =>
        {
            try
            {
                var (title, body) = DuplicateOf(r.Number, issue.Title, issue.Body);
                var created = await WithGitHubAsync(async s =>
                    await s.CreateIssueAsync(await s.GetRepositoryIdAsync(r.Owner, r.Repo), title, body));
                _issueNotice = $"Created #{created.Number}.";
                _actionError = null;
                TaskUpdated?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex) { _actionError = ex.Message; }
            RenderIssue();
        });
        var delete = Action("Delete Issue", async () =>
        {
            if (!Confirm("Delete issue?", $"Permanently delete #{r.Number}? This cannot be undone.")) return;
            try
            {
                await WithGitHubAsync(async s => { await s.DeleteIssueAsync(issue.NodeId); return true; });
                TaskUpdated?.Invoke(this, EventArgs.Empty);
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            catch (Exception ex) { _actionError = ex.Message; }
            RenderIssue();
        });
        delete.Foreground = Alert;
        ContentPanel.Children.Add(panel);
    }

    /// <summary>The Mac's duplicate: "Duplicate of #n: title", with a back-reference above the body.</summary>
    internal static (string Title, string Body) DuplicateOf(int number, string title, string? body) =>
        ($"Duplicate of #{number}: {title}", $"Duplicate of #{number}\n\n{body}");

    private bool Confirm(string title, string message) =>
        MessageBox.Show(Window.GetWindow(this)!, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
}
