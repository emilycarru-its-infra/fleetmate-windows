using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Serilog;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// In-place viewer for a DevOps work item or a GitHub issue: body and comments
/// on the left, metadata in a fixed 360px column on the right, presented
/// centred and large so a dashboard or query row can be read and acted on
/// without leaving its tab — the macOS TaskLightboxView, ported. The header's
/// Projects button hands the item to its home tab.
/// </summary>
public partial class TaskLightboxWindow : Window
{
    private UnifiedTask _task;
    private readonly AzureDevOpsService? _devOps;

    public TaskLightboxWindow(UnifiedTask task, Window? owner = null)
    {
        InitializeComponent();
        _task = task;
        _devOps = (Application.Current as App)?.DevOpsService;

        var host = owner ?? Application.Current?.MainWindow;
        if (host != null)
        {
            Owner = host;
            // The window is a scrim covering the owner; the card floats at ~80%.
            var source = PresentationSource.FromVisual(host);
            if (source?.CompositionTarget != null)
            {
                var topLeft = source.CompositionTarget.TransformFromDevice
                    .Transform(host.PointToScreen(new System.Windows.Point(0, 0)));
                Left = topLeft.X;
                Top = topLeft.Y;
            }
            else
            {
                Left = host.Left;
                Top = host.Top;
            }
            Width = host.ActualWidth;
            Height = host.ActualHeight;
            CardBorder.Width = Math.Min(Width - 32, Math.Max(860, Width * 0.8));
            CardBorder.Height = Math.Min(Height - 32, Math.Max(560, Height * 0.8));
        }
        else
        {
            Width = 1100;
            Height = 720;
            CardBorder.Width = 1060;
            CardBorder.Height = 680;
        }

        Loaded += async (_, _) => await LoadAsync();
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private void OnScrimMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Only a click on the scrim itself dismisses; clicks inside the card bubble
        // up with the card (or a child) as the source.
        if (!CardBorder.IsMouseOver) Close();
    }

    /// <summary>A stub with only an id is enough; everything else loads here.</summary>
    private async Task LoadAsync()
    {
        Render();

        if (_task.Provider == "azdevops" && _devOps != null && int.TryParse(_task.Id, out var id))
        {
            try
            {
                var item = await _devOps.GetWorkItemAsync(id);
                if (item != null) _task = item.AsUnifiedTask();
                Render();

                var comments = await _devOps.GetWorkItemCommentsAsync(id);
                RenderComments(comments);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Lightbox load failed for work item {Id}", _task.Id);
            }
        }
    }

    private void Render()
    {
        Title = _task.Provider == "github" ? $"Issue #{_task.Id}" : $"Work Item #{_task.Id}";
        TaskIdText.Text = $"#{_task.Id}";
        ProviderText.Text = _task.Provider.ToUpperInvariant();
        TitleText.Text = _task.Title;

        BodyPanel.Children.Clear();
        AddBodyHeading("Description");
        if (!string.IsNullOrWhiteSpace(_task.Description))
        {
            BodyPanel.Children.Add(new MarkdownViewer { MarkdownText = HtmlToText(_task.Description), MinHeight = 60 });
        }
        else
        {
            BodyPanel.Children.Add(Secondary("No description."));
        }

        MetaPanel.Children.Clear();
        AddStateBadge(_task.Metadata.TryGetValue("state", out var s) && s.Length > 0 ? s : _task.State.ToString());
        AddMetaRow("Type", Meta("workItemType"));
        AddMetaRow("Priority", _task.Priority?.ToString() ?? "-");
        AddMetaRow("Assigned To", _task.Assignees.Count > 0 ? string.Join(", ", _task.Assignees) : "(unassigned)");
        AddMetaRow("Area", Meta("areaPath"));
        AddMetaRow("Iteration", Meta("iterationPath"));
        if (_task.Labels.Count > 0) AddMetaRow("Tags", string.Join(", ", _task.Labels));
        if (_task.CreatedAt > DateTime.MinValue) AddMetaRow("Created", _task.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        if (_task.UpdatedAt > DateTime.MinValue) AddMetaRow("Modified", _task.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
    }

    private void RenderComments(List<WorkItemComment> comments)
    {
        AddBodyHeading($"Discussion ({comments.Count})");
        if (comments.Count == 0)
        {
            BodyPanel.Children.Add(Secondary("No comments."));
            return;
        }

        foreach (var comment in comments)
        {
            var card = new Border
            {
                Background = (Brush)FindResource("CardBackgroundBrush"),
                BorderBrush = (Brush)FindResource("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var stack = new StackPanel();
            var header = new DockPanel();
            var author = new TextBlock
            {
                Text = comment.CreatedBy?.DisplayName ?? "Unknown",
                FontWeight = FontWeights.SemiBold,
                FontSize = 12
            };
            var when = new TextBlock
            {
                Text = comment.CreatedDate?.ToLocalTime().ToString("MMM d, yyyy h:mm tt") ?? "",
                FontSize = 11,
                Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush"),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(when, Dock.Right);
            header.Children.Add(when);
            header.Children.Add(author);
            stack.Children.Add(header);
            stack.Children.Add(new TextBlock
            {
                Text = HtmlToText(comment.Text ?? ""),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0)
            });
            card.Child = stack;
            BodyPanel.Children.Add(card);
        }
    }

    // ── Actions ──────────────────────────────────────────────────────────

    private void OnOpenInProjects(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app && int.TryParse(_task.Id, out var id))
            app.PendingNavigateWorkItemId = id;
        (Owner as MainWindow ?? Application.Current?.MainWindow as MainWindow)?.NavigateToTab("Projects");
        Close();
    }

    private void OnOpenInBrowser(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_task.ExternalUrl)) return;
        try { Process.Start(new ProcessStartInfo(_task.ExternalUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "Failed to open URL"); }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    // ── Helpers ──────────────────────────────────────────────────────────

    private string Meta(string key) =>
        _task.Metadata.TryGetValue(key, out var v) && v.Length > 0 ? v : "-";

    private void AddBodyHeading(string text)
    {
        BodyPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Margin = new Thickness(0, BodyPanel.Children.Count == 0 ? 0 : 16, 0, 8)
        });
    }

    private TextBlock Secondary(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush")
    };

    private void AddMetaRow(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = (Brush)FindResource("SystemControlForegroundBaseMediumBrush")
        };
        Grid.SetColumn(labelBlock, 0);
        var valueBlock = new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(valueBlock, 1);
        grid.Children.Add(labelBlock);
        grid.Children.Add(valueBlock);
        MetaPanel.Children.Add(grid);
    }

    private void AddStateBadge(string state)
    {
        var color = state.ToLowerInvariant() switch
        {
            "new" or "to do" or "proposed" or "open" => "#27ae60",
            "active" or "in progress" or "doing" or "committed" or "inprogress" => "#3182CE",
            _ => "#8b5cf6"
        };
        var border = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 10)
        };
        border.Child = new TextBlock
        {
            Text = state,
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        };
        MetaPanel.Children.Add(border);
    }

    /// <summary>
    /// Azure DevOps descriptions and comments arrive as HTML; flatten to
    /// readable text (paragraph and break tags become newlines).
    /// </summary>
    internal static string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html) || !html.Contains('<')) return html;
        var text = Regex.Replace(html, @"<br\s*/?>|</p>|</div>|</li>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
    }
}
