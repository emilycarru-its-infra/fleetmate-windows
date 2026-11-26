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

    /// <summary>Drop the rows, e.g. while another item loads.</summary>
    public void Clear() => ShowFiles(Array.Empty<DiffFile>());

    private void Render() => Rows.ItemsSource = DiffRows.Flatten(_files, UserPreferences.DiffLayout);

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
