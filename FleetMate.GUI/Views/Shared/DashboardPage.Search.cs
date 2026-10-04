using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FleetMate.Core.Services.Search;
using ModernWpf.Controls;
using Serilog;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// The Dashboard's search-everything box. Results come from the in-memory
/// caches; the one network call is fetching a typed work-item id that is
/// not cached. Clicking a result opens it on its tab and clears the query.
/// </summary>
public partial class DashboardPage
{
    private int _searchGeneration;

    private const double SearchMaxWidth = 700;
    private const double SearchMinWidth = 180;
    private const double SearchGap = 16;

    /// <summary>
    /// Keep the search centred on the page, at most 700 wide, and shrink it
    /// so it never reaches the title on the left or the buttons on the right.
    /// Below its minimum it hides rather than overlap.
    /// </summary>
    private void OnHeaderSizeChanged(object sender, SizeChangedEventArgs e) => FitSearchBox();

    private void FitSearchBox()
    {
        // Fires once from InitializeComponent, before every part exists.
        if (HeaderGrid == null || HeaderTitle == null || HeaderActions == null || GlobalSearchBox == null) return;
        var total = HeaderGrid.ActualWidth;
        if (total <= 0) return;
        var side = Math.Max(HeaderTitle.ActualWidth, HeaderActions.ActualWidth) + SearchGap;
        var width = Math.Min(SearchMaxWidth, total - 2 * side);
        GlobalSearchBox.Visibility = width >= SearchMinWidth ? Visibility.Visible : Visibility.Collapsed;
        if (width >= SearchMinWidth) GlobalSearchBox.Width = width;
        if (GlobalSearchBox.Visibility != Visibility.Visible) GlobalSearchPopup.IsOpen = false;
    }

    private void OnGlobalSearchChanged(object sender, TextChangedEventArgs e) => _ = RunGlobalSearchAsync();

    private void OnGlobalSearchFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (GlobalSearchResults.Children.Count > 0 && GlobalSearch.ShouldSearch(GlobalSearchBox.Text))
            GlobalSearchPopup.IsOpen = true;
    }

    private void OnGlobalSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            GlobalSearchPopup.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && FirstResult() is { } first)
        {
            OpenSearchHit(first);
            e.Handled = true;
        }
    }

    private async Task RunGlobalSearchAsync()
    {
        if (_app == null) return;
        var generation = ++_searchGeneration;
        var query = GlobalSearchBox.Text;

        if (!GlobalSearch.ShouldSearch(query))
        {
            GlobalSearchResults.Children.Clear();
            GlobalSearchPopup.IsOpen = false;
            return;
        }

        var groups = GlobalSearch.Search(query, _app.BuildSearchSources());
        RenderSearchResults(groups);

        // A typed work-item id that isn't cached: fetch just that item.
        if (GlobalSearch.ParseWorkItemId(query) is { } id && _app.DevOpsService != null &&
            !_app.CachedWorkItems.Any(w => w.Id == id))
        {
            try
            {
                var item = await _app.DevOpsService.GetWorkItemAsync(id);
                if (item == null || generation != _searchGeneration) return;
                GlobalSearch.PrependWorkItem(groups, item);
                RenderSearchResults(groups);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Global search: work item {Id} lookup failed", id);
            }
        }
    }

    private void RenderSearchResults(List<SearchGroup> groups)
    {
        GlobalSearchResults.Children.Clear();

        if (groups.Count == 0)
        {
            GlobalSearchResults.Children.Add(new TextBlock
            {
                Text = "No matches in loaded data.",
                FontSize = 12,
                Margin = new Thickness(8, 6, 8, 6),
                Foreground = Secondary()
            });
        }

        foreach (var group in groups)
        {
            var header = new DockPanel { Margin = new Thickness(8, 8, 8, 2) };
            var count = new TextBlock { Text = group.Total.ToString(), FontSize = 11, Foreground = Secondary() };
            DockPanel.SetDock(count, Dock.Right);
            header.Children.Add(count);
            header.Children.Add(new TextBlock
            {
                Text = group.Title.ToUpperInvariant(),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Secondary()
            });
            GlobalSearchResults.Children.Add(header);

            foreach (var hit in group.Hits)
                GlobalSearchResults.Children.Add(SearchRow(hit));
        }

        GlobalSearchPopup.IsOpen = true;
    }

    private Border SearchRow(SearchHit hit)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = CategoryGlyph(hit.Category),
            FontSize = 14,
            Foreground = Secondary(),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = hit.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(hit.Subtitle))
            text.Children.Add(new TextBlock
            {
                Text = hit.Subtitle,
                FontSize = 11,
                Foreground = Secondary(),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var match = new Border
        {
            Background = (Brush)FindResource("SubtleFillBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(10, 0, 0, 0),
            // The title keeps the room; the label takes at most 40% of a narrow panel.
            MaxWidth = Math.Min(260, Math.Max(120, GlobalSearchBox.ActualWidth * 0.4)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = hit.MatchLabel,
                FontSize = 11,
                Foreground = Secondary(),
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
        Grid.SetColumn(match, 2);
        row.Children.Add(match);

        var border = new Border
        {
            Padding = new Thickness(8, 5, 8, 5),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Tag = hit,
            Child = row
        };
        border.MouseEnter += (_, _) => border.Background = (Brush)FindResource("SubtleFillBrush");
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        border.MouseLeftButtonUp += (_, _) => OpenSearchHit(hit);
        return border;
    }

    private SearchHit? FirstResult() =>
        GlobalSearchResults.Children.OfType<Border>().Select(b => b.Tag).OfType<SearchHit>().FirstOrDefault();

    /// <summary>
    /// Open the hit through its fleetmate:// link, the same route an outside
    /// link takes, then clear the query.
    /// </summary>
    private void OpenSearchHit(SearchHit hit)
    {
        if (_app == null) return;
        GlobalSearchPopup.IsOpen = false;
        GlobalSearchBox.Text = "";
        if (hit.Link.Length > 0) _app.OpenLink(hit.Link);
    }

    private static string CategoryGlyph(SearchCategory category) => category switch
    {
        SearchCategory.Devices => GlyphDevice,
        SearchCategory.Inventory => GlyphAsset,
        SearchCategory.Tickets => GlyphTicket,
        SearchCategory.WorkItems => GlyphWorkItem,
        SearchCategory.PullRequests => "\uE8AB",
        SearchCategory.Issues => "\uE7BA",
        SearchCategory.Commits => "\uE73E",
        SearchCategory.PipelineRuns => "\uE768",
        SearchCategory.Users => "",
        _ => "",
    };

    private Brush Secondary() => (Brush)FindResource("SystemControlForegroundBaseMediumBrush");
}
