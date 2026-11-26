using FleetMate.Core.Config;
using FleetMate.Core.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FleetMate.WinUI.Pages.Development.Shared;

/// <summary>
/// Diffs for pull requests, commits and repositories: one band per file, then
/// its hunks, side by side or inline. Every row is one item of a single
/// virtualizing <see cref="ItemsRepeater"/>, so a long diff draws only what is
/// on screen — host it inside a ScrollViewer. The layout is the saved
/// <see cref="UserPreferences.DiffLayout"/>; picking one in any open view
/// redraws them all.
/// </summary>
/// <example>
/// <code>
/// Diff.Header = $"Changes ({detail.Files.Count} files)";
/// Diff.ShowFiles(detail.Files, detail.Truncated);
/// </code>
/// </example>
public sealed partial class DiffFilesView : UserControl
{
    private static event Action<DiffLayout>? LayoutPicked;

    private IReadOnlyList<DiffFile> _files = Array.Empty<DiffFile>();

    public DiffFilesView()
    {
        InitializeComponent();
        ShowLayout(UserPreferences.DiffLayout);
        Loaded += (_, _) => { LayoutPicked += OnLayoutPicked; ShowLayout(UserPreferences.DiffLayout); };
        Unloaded += (_, _) => LayoutPicked -= OnLayoutPicked;
    }

    /// <summary>The heading beside the layout picker, e.g. "Changes (3 files, +12 −4)"; empty hides it.</summary>
    public string Header
    {
        get => HeaderText.Text;
        set
        {
            HeaderText.Text = value ?? "";
            HeaderText.Visibility = DevelopmentUi.Show(!string.IsNullOrEmpty(value));
        }
    }

    /// <summary>The files to draw. <paramref name="truncated"/> says the provider cut the list short.</summary>
    public void ShowFiles(IReadOnlyList<DiffFile> files, bool truncated = false)
    {
        _files = files;
        TruncationNotice.Visibility = DevelopmentUi.Show(truncated);
        LayoutPicker.Visibility = DevelopmentUi.Show(files.Any(f => f.Hunks.Count > 0));
        Render();
    }

    /// <summary>
    /// Buttons on every hunk header, such as Repos' Stage, Unstage and Discard
    /// Chunk for the working tree. Empty (the default) leaves hunks read only.
    /// </summary>
    public IReadOnlyList<DiffHunkAction> HunkActions
    {
        get => _hunkActions;
        set
        {
            _hunkActions = value ?? Array.Empty<DiffHunkAction>();
            Render();
        }
    }

    private IReadOnlyList<DiffHunkAction> _hunkActions = Array.Empty<DiffHunkAction>();

    /// <summary>A hunk header was (re)bound to a row: give it this view's hunk actions.</summary>
    private void HunkActions_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is not StackPanel panel) return;
        panel.Children.Clear();
        if (args.NewValue is not DiffRow { Hunk: { } hunk } row) return;
        foreach (var action in _hunkActions)
        {
            var button = new Button
            {
                Content = action.Label,
                Padding = new Thickness(8, 1, 8, 2),
                FontSize = 11,
            };
            if (action.Destructive) button.Foreground = DevelopmentUi.Theme("SystemFillColorCautionBrush");
            ToolTipService.SetToolTip(button, action.Tooltip);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{action.Label}, {hunk.Header}");
            var file = row.File;
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try { await action.Run(file, hunk); }
                finally { button.IsEnabled = true; }
            };
            panel.Children.Add(button);
        }
    }

    /// <summary>Drop the rows, e.g. while another item loads.</summary>
    public void Clear() => ShowFiles(Array.Empty<DiffFile>());

    /// <summary>Up to this many rows draw as one plain stack; more are virtualized.</summary>
    public const int SmallDiffRows = 600;

    private void Render()
    {
        var rows = DiffRows.Flatten(_files, UserPreferences.DiffLayout);
        var small = rows.Count <= SmallDiffRows;
        SmallRows.ItemsSource = small ? rows : null;
        Rows.ItemsSource = small ? null : rows;
        SmallRows.Visibility = DevelopmentUi.Show(small);
        Rows.Visibility = DevelopmentUi.Show(!small);
    }

    private void SplitButton_Click(object sender, RoutedEventArgs e) => Pick(DiffLayout.Split);

    private void InlineButton_Click(object sender, RoutedEventArgs e) => Pick(DiffLayout.Inline);

    private void Pick(DiffLayout layout)
    {
        ShowLayout(layout);
        if (UserPreferences.DiffLayout == layout) return;
        UserPreferences.SetDiffLayout(layout);
        LayoutPicked?.Invoke(layout);
    }

    private void OnLayoutPicked(DiffLayout layout)
    {
        ShowLayout(layout);
        Render();
    }

    private void ShowLayout(DiffLayout layout)
    {
        SplitButton.IsChecked = layout == DiffLayout.Split;
        InlineButton.IsChecked = layout == DiffLayout.Inline;
    }
}

/// <summary>A button on each hunk header of a <see cref="DiffFilesView"/>: its label, hint, and what it does to the hunk.</summary>
public sealed record DiffHunkAction(string Label, string Tooltip, Func<DiffFile, DiffHunk, Task> Run, bool Destructive = false);

/// <summary>Picks a <see cref="DiffFilesView"/> row's template by its <see cref="DiffRowKind"/>.</summary>
public sealed partial class DiffRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? FileHeader { get; set; }
    public DataTemplate? EmptyNotice { get; set; }
    public DataTemplate? HunkHeader { get; set; }
    public DataTemplate? Line { get; set; }
    public DataTemplate? Split { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => (item as DiffRow)?.Kind switch
    {
        DiffRowKind.FileHeader => FileHeader,
        DiffRowKind.EmptyNotice => EmptyNotice,
        DiffRowKind.HunkHeader => HunkHeader,
        DiffRowKind.Split => Split,
        _ => Line,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
