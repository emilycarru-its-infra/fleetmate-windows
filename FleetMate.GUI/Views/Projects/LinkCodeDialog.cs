using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Serilog;

namespace FleetMate.GUI.Views.Projects;

/// <summary>
/// Link to Code (macOS linkCodePopover): pick a repository, then a branch,
/// commit or pull request, and link it to the work item. Enter in the search
/// box looks up a pasted commit SHA that is not in the recent list.
/// </summary>
public sealed class LinkCodeDialog : Window
{
    private readonly AzureDevOpsService _service;
    private readonly WorkItemDetail _item;
    private readonly ComboBox _repos = new() { MinWidth = 260, Margin = new Thickness(0, 0, 0, 8) };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox _search = new() { Margin = new Thickness(0, 0, 0, 8) };
    private readonly ListBox _list = new() { Height = 300 };
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _link = new() { Content = "Link", IsDefault = true, MinWidth = 80, IsEnabled = false };
    private List<CodeLinkCandidate> _candidates = new();
    private string _kind = "branch";
    private int _loadToken;

    public LinkCodeDialog(AzureDevOpsService service, WorkItemDetail item)
    {
        _service = service;
        _item = item;
        Title = $"Link to Code · #{item.Id}";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty, "SystemControlPageBackgroundChromeLowBrush");
        SetResourceReference(ForegroundProperty, "SystemControlForegroundBaseHighBrush");

        foreach (var (kind, label) in new[] { ("branch", "Branch"), ("commit", "Commit"), ("pr", "Pull Request") })
        {
            var radio = new RadioButton { Content = label, Tag = kind, GroupName = "kind", IsChecked = kind == _kind, Margin = new Thickness(0, 0, 16, 0) };
            radio.Checked += async (_, _) => { _kind = kind; _search.Text = ""; await LoadAsync(); };
            _tabs.Children.Add(radio);
        }

        _repos.DisplayMemberPath = nameof(DevOpsRepository.Name);
        _repos.SelectionChanged += async (_, _) => { _search.Text = ""; await LoadAsync(); };
        _search.TextChanged += (_, _) => Filter();
        _search.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await LookupShaAsync(); } };
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search");
        _list.SelectionChanged += (_, _) => _link.IsEnabled = _list.SelectedItem != null;
        _list.MouseDoubleClick += async (_, _) => await LinkAsync();
        _list.ItemTemplate = Template();
        _link.Click += async (_, _) => await LinkAsync();

        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(_link);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = "Repository", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
        root.Children.Add(_repos);
        root.Children.Add(_tabs);
        root.Children.Add(_search);
        root.Children.Add(_list);
        root.Children.Add(_status);
        root.Children.Add(buttons);
        Content = root;

        Loaded += async (_, _) => await LoadReposAsync();
    }

    private static DataTemplate Template()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(CodeLinkCandidate.Title)));
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(CodeLinkCandidate.Detail)));
        detail.SetValue(TextBlock.FontSizeProperty, 11.0);
        detail.SetValue(TextBlock.OpacityProperty, 0.7);
        panel.AppendChild(title);
        panel.AppendChild(detail);
        return new DataTemplate { VisualTree = panel };
    }

    private DevOpsRepository? Repo => _repos.SelectedItem as DevOpsRepository;

    private async Task LoadReposAsync()
    {
        _status.Text = "Loading repositories…";
        try
        {
            var repos = await _service.GetRepositoriesAsync(_item.Project);
            _repos.ItemsSource = repos;
            _status.Text = repos.Count == 0 ? "No repositories found." : "";
            if (repos.Count > 0) _repos.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[projects] Link to Code repositories");
            _status.Text = "Could not load repositories: " + ex.Message;
        }
    }

    private async Task LoadAsync()
    {
        if (Repo is not { } repo) return;
        var token = ++_loadToken;
        _status.Text = "Loading…";
        _list.ItemsSource = null;
        try
        {
            var items = _kind switch
            {
                "commit" => await _service.GetCommitsAsync(repo),
                "pr" => await _service.GetRepositoryPullRequestsAsync(repo),
                _ => await _service.GetBranchesAsync(repo),
            };
            if (token != _loadToken) return;
            _candidates = items;
            _status.Text = "";
            Filter();
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return;
            Log.Warning(ex, "[projects] Link to Code {Kind}", _kind);
            _status.Text = ex.Message;
        }
    }

    private void Filter()
    {
        var shown = Matches(_candidates, _search.Text);
        _list.ItemsSource = shown;
        if (_status.Text.Length == 0 || _status.Text.StartsWith("No ", StringComparison.Ordinal))
            _status.Text = shown.Count > 0 ? "" : _search.Text.Length > 0 ? "No matches." : "Nothing found.";
    }

    /// <summary>Title, detail or key containing the query, case-insensitively.</summary>
    internal static List<CodeLinkCandidate> Matches(IEnumerable<CodeLinkCandidate> items, string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return items.ToList();
        return items.Where(c =>
            c.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.Key.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (c.Detail?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
    }

    /// <summary>A pasted SHA (or commit URL) that is not in the recent list.</summary>
    internal static string? ShaFromQuery(string query)
    {
        var sha = query.Trim();
        var marker = sha.IndexOf("/commit/", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0) sha = sha[(marker + "/commit/".Length)..];
        return sha.Length >= 7 && sha.All(Uri.IsHexDigit) ? sha : null;
    }

    private async Task LookupShaAsync()
    {
        if (_kind != "commit" || Repo is not { } repo || ShaFromQuery(_search.Text) is not { } sha) return;
        if (_candidates.Any(c => c.Key.StartsWith(sha, StringComparison.OrdinalIgnoreCase))) return;
        _status.Text = "Looking up commit…";
        var commit = await _service.GetCommitAsync(repo, sha);
        if (commit == null)
        {
            _status.Text = $"Commit not found for {sha[..Math.Min(12, sha.Length)]}";
            return;
        }
        _candidates.Insert(0, commit);
        _status.Text = "";
        _search.Text = commit.Key;
    }

    private async Task LinkAsync()
    {
        if (Repo is not { } repo || _list.SelectedItem is not CodeLinkCandidate c) return;
        var (uri, name) = c.Kind switch
        {
            "commit" => (AzureDevOpsService.CommitArtifactUri(repo.ProjectId, repo.Id, c.Key), "Fixed in Commit"),
            "pr" => (AzureDevOpsService.PullRequestArtifactUri(repo.ProjectId, repo.Id, int.Parse(c.Key)), "Pull Request"),
            _ => (AzureDevOpsService.BranchArtifactUri(repo.ProjectId, repo.Id, c.Key), "Branch"),
        };

        _link.IsEnabled = false;
        _status.Text = "Linking…";
        var result = await _service.AddArtifactLinkAsync(_item.Id, uri, name);
        if (result.Success)
        {
            DialogResult = true;
            return;
        }
        _status.Foreground = new SolidColorBrush(Color.FromRgb(0xF7, 0x63, 0x0C));
        _status.Text = result.Error;
        _link.IsEnabled = true;
    }
}
