using System.ComponentModel;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using FleetMate.WinUI.Pages.Tickets;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Serilog;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace FleetMate.WinUI.Pages;

/// <summary>
/// The Tickets tab, and the whole of TicketsMate: the widgets, the list and
/// the board over the shared ticket cache, and the open ticket beside them.
/// The state and rules live in <see cref="TicketsViewModel"/> and Core; this
/// draws the charts, builds the Filters flyout and handles input.
/// </summary>
public sealed partial class TicketsPage : Page
{
    private static readonly TicketListColumn[] HeaderColumns =
    [
        TicketListColumn.Id, TicketListColumn.Title, TicketListColumn.Status, TicketListColumn.Priority,
        TicketListColumn.Requestor, TicketListColumn.Responsible, TicketListColumn.Age, TicketListColumn.Modified,
    ];

    private readonly Dictionary<TicketListColumn, (Button Button, FontIcon Arrow)> _headers = new();
    private readonly Dictionary<TicketFilterSection, ListViewItem> _sectionItems = new();
    private TicketFilterSection _filterSection = TicketFiltersPopoverRules.Sections[0];
    private bool _detailOpen;
    private bool _syncingSelection;
    private bool _syncingDateRange;
    private int? _linkAwaitingSignIn;

    public TicketsPage()
    {
        EnsureResources();
        ViewModel = new TicketsViewModel(App.Services);
        InitializeComponent();

        BuildListHeader();
        BuildFilterSections();
        var now = DateTime.Now;
        DateRangeBox.ItemsSource = Enum.GetValues<TicketDateRangePreset>()
            .Select(p => new DateRangeOption(p, TicketBoardQuery.Label(p, now)))
            .ToList();

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.Redrawn += OnRedrawn;
        ViewModel.SignedIn += OnSignedIn;
        Detail.LoadFailed += message => ShowAction(message, InfoBarSeverity.Warning);

        MainWindow.SearchChanged += (_, text) => ViewModel.Search = text;
        MainWindow.ViewModeChanged += (_, mode) => ViewModel.IsBoard = mode == "board";
        ViewModel.IsBoard = MainWindow.ViewMode == "board";
        ApplyViewMode();
    }

    public TicketsViewModel ViewModel { get; }

    public TicketDetailViewModel Detail { get; } = new();

    public TicketListLayout Layout => TicketListLayout.Current;

    /// <summary>The Tickets tones live in their own dictionary; merged into the app once so code and templates find them.</summary>
    private static void EnsureResources()
    {
        var resources = Application.Current.Resources;
        if (resources.ContainsKey("TicketToneBlueBrush")) return;
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///Pages/Tickets/TicketResources.xaml") });
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.EnsureLoadedAsync();
        // A link checks on every visit: the page is cached.
        if (App.Services.PendingTicketId is { } linked)
        {
            App.Services.PendingTicketId = null;
            await OpenLinkedTicketAsync(linked);
        }
    }

    // MARK: - View model changes

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TicketsViewModel.Charts):
                DrawCharts();
                break;
            case nameof(TicketsViewModel.EmptyState):
            case nameof(TicketsViewModel.ShowsLoading):
                ShowEmptyState();
                break;
            case nameof(TicketsViewModel.LaunchProgress):
                LaunchProgressBar.Visibility = ViewModel.LaunchProgress is null ? Visibility.Collapsed : Visibility.Visible;
                LoadingRing.Visibility = ViewModel.LaunchProgress is null ? Visibility.Visible : Visibility.Collapsed;
                LaunchProgressBar.Value = ViewModel.LaunchProgress ?? 0;
                break;
            case nameof(TicketsViewModel.IsBoard):
                ApplyViewMode();
                if (!ViewModel.IsBoard) MainWindow.RequestViewMode("list");
                break;
            case nameof(TicketsViewModel.SortField):
            case nameof(TicketsViewModel.SortAscending):
                UpdateHeaderArrows();
                break;
            case nameof(TicketsViewModel.ActiveFilterCount):
                UpdateFilterSectionBadges();
                if (FiltersFlyout.IsOpen) RefreshFilterValues();
                break;
            case nameof(TicketsViewModel.Preset):
                SyncDateRange();
                break;
        }
    }

    /// <summary>
    /// The rows were rebuilt, which drops the list's selection: select the
    /// open ticket again without reopening it, so the pane is left as it is.
    /// </summary>
    private void OnRedrawn()
    {
        if (Detail.Ticket is not { } open) return;
        var row = ViewModel.Rows.FirstOrDefault(r => r.Id == open.Id);
        if (row == null || ReferenceEquals(TicketList.SelectedItem, row)) return;
        _syncingSelection = true;
        TicketList.SelectedItem = row;
        _syncingSelection = false;
    }

    private void ApplyViewMode()
    {
        var board = ViewModel.IsBoard;
        ListCard.Visibility = board ? Visibility.Collapsed : Visibility.Visible;
        BoardScroller.Visibility = board ? Visibility.Visible : Visibility.Collapsed;
        ShowEmptyState();
    }

    private void ShowEmptyState()
    {
        var state = ViewModel.ShowsLoading ? null : ViewModel.EmptyState;
        EmptyPanel.Visibility = state == null ? Visibility.Collapsed : Visibility.Visible;
        var listed = state == null ? Visibility.Visible : Visibility.Collapsed;
        if (ViewModel.IsBoard) BoardScroller.Visibility = listed; else ListCard.Visibility = listed;
        if (state == null) return;
        EmptyIcon.Glyph = state.Glyph;
        EmptyTitle.Text = state.Title;
        EmptyDescription.Text = state.Description;
        AutomationProperties.SetName(EmptyPanel, $"{state.Title}. {state.Description}");
        EmptySignIn.Visibility = Show(state.ShowsSignIn);
        EmptyRetry.Visibility = Show(state.ShowsRetry);
        EmptyShowAll.Visibility = Show(state.ShowsShowAll);
        EmptyClearFilters.Visibility = Show(state.ShowsClearFilters);
        EmptyShowClosed.Visibility = Show(state.ShowsShowClosed);
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private async void EmptySignIn_Click(object sender, RoutedEventArgs e) => await App.Services.TdxSession.RetryAsync();

    private async void EmptyRetry_Click(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();

    private void EmptyShowAll_Click(object sender, RoutedEventArgs e) => ViewModel.AssignedToMe = false;

    private void EmptyShowClosed_Click(object sender, RoutedEventArgs e) => ViewModel.ShowClosed = true;

    private async void RefreshAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.RefreshEnabled) await ViewModel.RefreshAsync();
    }

    // MARK: - List header

    /// <summary>One button per column; clicking sorts by it, a second click flips the direction.</summary>
    private void BuildListHeader()
    {
        for (var i = 0; i < HeaderColumns.Length; i++)
        {
            var column = HeaderColumns[i];
            var arrow = new FontIcon { FontSize = 10, Glyph = "", Visibility = Visibility.Collapsed };
            var label = new TextBlock
            {
                Text = TicketListColumns.Header(column),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = TicketBrushes.Text("TextFillColorSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { label, arrow } };
            var button = new Button
            {
                Content = content,
                Style = (Style)Resources["ColumnHeaderButtonStyle"],
                Margin = new Thickness(-6, 0, 0, 0),
            };
            if (column == TicketListColumn.Age) button.HorizontalContentAlignment = HorizontalAlignment.Right;
            button.Click += (_, _) => ViewModel.SortByColumn(column);
            Grid.SetColumn(button, i);
            ListHeader.Children.Add(button);
            _headers[column] = (button, arrow);
        }
        UpdateHeaderArrows();
    }

    private void UpdateHeaderArrows()
    {
        var sorted = TicketListSort.ColumnFor(ViewModel.SortField);
        foreach (var (column, (button, arrow)) in _headers)
        {
            arrow.Visibility = Show(sorted == column);
            arrow.Glyph = ViewModel.SortAscending ? "" : "";
            AutomationProperties.SetName(button, TicketListSort.SpokenHeader(column, ViewModel.SortField, ViewModel.SortAscending));
            ToolTipService.SetToolTip(button, $"Sort by {TicketListColumns.Header(column)}");
        }
    }

    // MARK: - Widgets

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TicketWidgetTile tile }) tile.Action();
    }

    private void DrawCharts()
    {
        foreach (var chart in ViewModel.Charts)
        {
            switch (chart.Category)
            {
                case TicketFilterCategory.Status: DrawDonut(chart); break;
                case TicketFilterCategory.Priority: FillBars(PriorityBars, chart); break;
                case TicketFilterCategory.Age: FillBars(AgeBars, chart); break;
                case TicketFilterCategory.Responsible: FillBars(ResponsibleBars, chart); break;
                case TicketFilterCategory.Group: FillBars(GroupBars, chart); break;
            }
        }
    }

    private bool Faded(TicketWidgetChart chart, string value) => TicketWidgetRules.Faded(ViewModel.Filters, chart.Category, value);

    /// <summary>One arc per status with a small gap between them; the legend rows are filters too.</summary>
    private void DrawDonut(TicketWidgetChart chart)
    {
        DonutCanvas.Children.Clear();
        StatusLegend.Children.Clear();
        var counts = chart.Counts;
        var total = counts.Sum(c => c.Value);
        DonutTotal.Text = total.ToString("N0");
        if (total == 0)
        {
            StatusLegend.Children.Add(Caption(ViewModel.IsLoading ? "Loading…" : "No tickets match", "TextFillColorTertiaryBrush"));
            return;
        }

        const double size = 96, thickness = 12;
        var radius = (size - thickness) / 2;
        var center = new Point(size / 2, size / 2);
        var gap = counts.Count > 1 ? 2.0 / radius : 0;
        var start = -Math.PI / 2;
        foreach (var c in counts)
        {
            var brush = TicketBrushes.Tone(TicketTones.Status(c.Label));
            var opacity = Faded(chart, c.Label) ? 0.3 : 1;
            var sweep = 2 * Math.PI * c.Value / total;
            Shape shape;
            if (counts.Count == 1)
            {
                shape = new Ellipse { Width = size - thickness, Height = size - thickness, Stroke = brush, StrokeThickness = thickness };
                Canvas.SetLeft(shape, thickness / 2);
                Canvas.SetTop(shape, thickness / 2);
            }
            else
            {
                var a0 = start + gap / 2;
                var a1 = start + sweep - gap / 2;
                var figure = new PathFigure { StartPoint = PointAt(center, radius, a0), IsClosed = false };
                figure.Segments.Add(new ArcSegment
                {
                    Point = PointAt(center, radius, a1),
                    Size = new Size(radius, radius),
                    IsLargeArc = a1 - a0 > Math.PI,
                    SweepDirection = SweepDirection.Clockwise,
                });
                var geometry = new PathGeometry();
                geometry.Figures.Add(figure);
                shape = new Path { Data = geometry, Stroke = brush, StrokeThickness = thickness };
            }
            shape.Opacity = opacity;
            ToolTipService.SetToolTip(shape, $"{c.Label}: {c.Value}");
            var label = c.Label;
            shape.Tapped += (_, _) => ViewModel.Filters.Toggle(TicketFilterCategory.Status, label);
            DonutCanvas.Children.Add(shape);
            start += sweep;

            StatusLegend.Children.Add(ValueRow(chart, c, brush, swatch: true));
        }
        AutomationProperties.SetName(DonutCanvas, "By status: " + string.Join(", ", counts.Select(c => $"{c.Label} {c.Value}")));
    }

    private static Point PointAt(Point center, double radius, double angle) =>
        new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));

    /// <summary>Horizontal bars, label left and count right; each row toggles its value.</summary>
    private void FillBars(StackPanel host, TicketWidgetChart chart)
    {
        host.Children.Clear();
        if (chart.Counts.Count == 0)
        {
            host.Children.Add(Caption(ViewModel.IsLoading ? "Loading…" : "No tickets match", "TextFillColorTertiaryBrush"));
            return;
        }
        foreach (var c in chart.Counts.Take(TicketStats.TopCount))
            host.Children.Add(ValueRow(chart, c, TicketBrushes.Tone(TicketTones.For(chart.Category, c.Label)), swatch: false,
                max: chart.Counts.Max(x => x.Value)));
    }

    /// <summary>A value as a button: a legend dot or a bar, its name and its count. Faded when outside the selection.</summary>
    private Button ValueRow(TicketWidgetChart chart, TicketStats.Count c, Brush brush, bool swatch, int max = 0)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = swatch ? GridLength.Auto : new GridLength(1.1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = Caption(c.Label, c.Label == TicketStats.Unassigned ? "TextFillColorTertiaryBrush" : "TextFillColorPrimaryBrush");
        if (swatch)
        {
            row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = brush, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
        }
        else
        {
            row.Children.Add(name);
            var track = new Grid
            {
                Height = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Background = TicketBrushes.Text("TicketToneTrackBrush"),
                CornerRadius = new CornerRadius(4),
            };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(c.Value, 0.0001), GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(max - c.Value, 0), GridUnitType.Star) });
            track.Children.Add(new Border { Background = brush, CornerRadius = new CornerRadius(4) });
            Grid.SetColumn(track, 1);
            row.Children.Add(track);
        }
        var count = Caption(c.Value.ToString("N0"), "TextFillColorSecondaryBrush");
        count.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(count, 2);
        row.Children.Add(count);

        var selected = ViewModel.Filters.IsSelected(chart.Category, c.Label);
        var button = new Button
        {
            Content = row,
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(4, 2, 4, 2),
            MinHeight = 0,
            Opacity = Faded(chart, c.Label) ? 0.45 : 1,
        };
        var help = selected ? "Click to remove this filter." : "Click to filter by it.";
        ToolTipService.SetToolTip(button, $"{c.Label}: {c.Value}. {help}");
        AutomationProperties.SetName(button, $"{chart.Title}, {c.Label}: {c.Value}");
        if (selected) AutomationProperties.SetItemStatus(button, "Selected");
        var label = c.Label;
        button.Click += (_, _) => ViewModel.Filters.Toggle(chart.Category, label);
        return button;
    }

    private static TextBlock Caption(string text, string brushKey) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = TicketBrushes.Text(brushKey),
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxLines = 1,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // MARK: - Filters flyout

    /// <summary>The categories on the left, each with a count of what is picked in it.</summary>
    private void BuildFilterSections()
    {
        foreach (var section in TicketFiltersPopoverRules.Sections)
        {
            var badge = new InfoBadge { Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { Text = section.Title, VerticalAlignment = VerticalAlignment.Center };
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(title);
            Grid.SetColumn(badge, 1);
            row.Children.Add(badge);
            var item = new ListViewItem { Content = row, Tag = section, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(item, section.Title);
            _sectionItems[section] = item;
            FilterSectionsList.Items.Add(item);
        }
        FilterSectionsList.SelectedItem = _sectionItems[_filterSection];
    }

    private void UpdateFilterSectionBadges()
    {
        foreach (var (section, item) in _sectionItems)
        {
            var count = TicketFiltersPopoverRules.SectionCount(section, ViewModel.Filters, ViewModel.ShowClosed, ViewModel.IsDefaultDateRange);
            var row = (Grid)item.Content;
            var badge = (InfoBadge)row.Children[1];
            badge.Value = count;
            badge.Visibility = Show(count > 0);
            ((TextBlock)row.Children[0]).FontWeight = count > 0 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            AutomationProperties.SetName(item, TicketFiltersPopoverRules.SectionName(section.Title, count));
        }
    }

    private void FiltersFlyout_Opening(object sender, object e)
    {
        FilterValueSearchBox.Text = "";
        UpdateFilterSectionBadges();
        SyncDateRange();
        ShowFilterSection(_filterSection);
    }

    private void FilterSections_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilterSectionsList.SelectedItem is not ListViewItem { Tag: TicketFilterSection section } || section == _filterSection) return;
        _filterSection = section;
        FilterValueSearchBox.Text = "";
        ShowFilterSection(section);
    }

    private void FilterValueSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshFilterValues(rebuild: true);

    private void ShowFilterSection(TicketFilterSection section)
    {
        FilterSectionsList.SelectedItem = _sectionItems[section];
        FilterTimePeriodPanel.Visibility = Show(section.IsTimePeriod);
        FilterValuesPanel.Visibility = Show(!section.IsTimePeriod);
        RefreshFilterValues(rebuild: true);
    }

    /// <summary>
    /// The chosen category's values as checkboxes. Rebuilt when the category,
    /// the search or the values change; otherwise only the ticks follow, so
    /// focus stays on the box just toggled.
    /// </summary>
    private void RefreshFilterValues(bool rebuild = false)
    {
        if (_filterSection.Category is not { } category)
        {
            FilterValuesList.Children.Clear();
            return;
        }
        var selected = ViewModel.Filters.Selected(category);
        var available = TicketFilters.AvailableValues(ViewModel.Loaded.ToList(), category, DateTime.UtcNow);
        var values = TicketFiltersPopoverRules.Search(TicketFiltersPopoverRules.Values(available, selected), FilterValueSearchBox.Text);
        var boxes = FilterValuesList.Children.OfType<CheckBox>().ToList();
        if (!rebuild && values.SequenceEqual(boxes.Select(b => (string)b.Tag)))
        {
            foreach (var box in boxes) box.IsChecked = selected.Contains((string)box.Tag);
            return;
        }
        FilterValuesList.Children.Clear();
        foreach (var value in values)
        {
            var box = new CheckBox { Content = value, Tag = value, IsChecked = selected.Contains(value), MinWidth = 0 };
            AutomationProperties.SetName(box, value);
            box.Click += (_, _) => ViewModel.Filters.Toggle(category, value);
            FilterValuesList.Children.Add(box);
        }
        FilterNoValuesText.Visibility = Show(values.Count == 0);
    }

    private void SyncDateRange()
    {
        if (DateRangeBox.ItemsSource is not IEnumerable<DateRangeOption> options) return;
        _syncingDateRange = true;
        DateRangeBox.SelectedItem = options.FirstOrDefault(o => o.Preset == ViewModel.Preset);
        _syncingDateRange = false;
    }

    private async void DateRange_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingDateRange || DateRangeBox.SelectedItem is not DateRangeOption option) return;
        await ViewModel.SetPresetAsync(option.Preset);
    }

    // MARK: - Opening tickets

    private void TicketList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || TicketList.SelectedItem is not TicketListItem row) return;
        // A rebuilt list selecting the open ticket again is not a new pick.
        if (TicketsPageSync.IsSameOpenTicket(row.Id, Detail.Ticket?.Id, _detailOpen)) return;
        _ = OpenTicketAsync(row.Ticket);
    }

    private void BoardCard_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TicketListItem card) _ = OpenTicketAsync(card.Ticket);
    }

    /// <summary>Show a ticket in the detail pane: at once from the list, then fresh from TeamDynamix.</summary>
    private async Task OpenTicketAsync(TdxTicket ticket)
    {
        if (Reader is not { } reader) return;
        ActionInfo.IsOpen = false;
        SetDetailOpen(true);
        await Detail.OpenAsync(reader, ticket, fresh => App.Services.TicketCache.Replace(fresh));
    }

    /// <summary>TeamDynamix, or the fictional fixtures in demo mode; null when neither is there.</summary>
    private TicketReader? Reader => ViewModel.IsDemo ? TicketReader.Demo : ViewModel.Tdx is { } service ? TicketReader.For(service) : null;

    /// <summary>A <c>fleetmate:</c> link: open the ticket even when the filters hide it, fetching one not loaded.</summary>
    private async Task OpenLinkedTicketAsync(int ticketId)
    {
        if (Reader is not { } reader) return;
        _linkAwaitingSignIn = null;
        try
        {
            var ticket = ViewModel.Loaded.FirstOrDefault(t => t.Id == ticketId) ?? await reader.Ticket(ticketId);
            if (ticket != null)
            {
                ViewModel.Reveal(ticketId);
                await OpenTicketAsync(ticket);
                if (ViewModel.Rows.FirstOrDefault(r => r.Id == ticketId) is { } row) TicketList.ScrollIntoView(row);
            }
            else if (!HoldLinkForSignIn(ticketId)) ShowAction($"Ticket #{ticketId} could not be opened.", InfoBarSeverity.Warning, open: true);
        }
        catch (Exception ex)
        {
            if (!HoldLinkForSignIn(ticketId)) ShowAction($"Error opening ticket #{ticketId}: {ex.Message}", InfoBarSeverity.Warning, open: true);
        }
    }

    /// <summary>Hold a linked ticket until sign-in, when that is why it could not be fetched.</summary>
    private bool HoldLinkForSignIn(int ticketId)
    {
        var session = App.Services.TdxSession;
        if (!TicketsPageSync.HoldLinkForSignIn(session.IsEnabled, session.IsSignedIn)) return false;
        _linkAwaitingSignIn = ticketId;
        Log.Information("[tickets] Ticket #{Id} opens once TeamDynamix sign-in finishes", ticketId);
        ShowAction($"Ticket #{ticketId} opens once TeamDynamix sign-in finishes.", InfoBarSeverity.Informational, open: true);
        return true;
    }

    private async void OnSignedIn()
    {
        if (_linkAwaitingSignIn is { } linked)
        {
            await OpenLinkedTicketAsync(linked);
            return;
        }
        if (Detail.Ticket is { } open && _detailOpen) await OpenTicketAsync(open);
    }

    // MARK: - Detail pane

    private void SetDetailOpen(bool open)
    {
        _detailOpen = open;
        DetailsToggle.IsChecked = open;
        DetailCard.Visibility = Show(open);
        FitColumns(ActualWidth);
    }

    private void DetailsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_detailOpen) CloseTicket();
        else if (TicketList.SelectedItem is TicketListItem row) _ = OpenTicketAsync(row.Ticket);
        else if (Detail.Ticket is { } last) _ = OpenTicketAsync(last);
        else DetailsToggle.IsChecked = false;
    }

    private void CloseDetail_Click(object sender, RoutedEventArgs e) => CloseTicket();

    /// <summary>Esc closes the open ticket, unless a flyout or text box has it.</summary>
    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_detailOpen || FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;
        args.Handled = true;
        CloseTicket();
    }

    /// <summary>Close the ticket; the selection clears too, so picking it again opens it again.</summary>
    private void CloseTicket()
    {
        Detail.Close();
        _syncingSelection = true;
        TicketList.SelectedItem = null;
        _syncingSelection = false;
        SetDetailOpen(false);
    }

    private async void RefreshDetail_Click(object sender, RoutedEventArgs e)
    {
        if (Detail.Ticket is { } open) await OpenTicketAsync(open);
    }

    private void FeedFilter_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string tag && Enum.TryParse<TicketFeedFilter>(tag, out var filter)) Detail.FeedFilter = filter;
    }

    private void CopyNumber_Click(object sender, RoutedEventArgs e)
    {
        if (Detail.Ticket is { } open) Copy(open.Id.ToString(), "Ticket number copied");
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (Detail.Ticket is { } open) CopyLink(open.Id);
    }

    private async void OpenInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (Detail.Ticket is { } open) await OpenInBrowserAsync(open.Id);
    }

    private void CopyDescription_Click(object sender, RoutedEventArgs e)
    {
        if (Detail.Description.Length > 0) Copy(Detail.Description, "Description copied");
    }

    /// <summary>The ticket's TDNext page, built from the configured base URL, never from a hardcoded host.</summary>
    private void CopyLink(int ticketId)
    {
        if (App.Services.Config.Tdx?.GetTicketWebUrl(ticketId) is { } url) Copy(url, "Ticket link copied");
        else ShowAction("No TeamDynamix URL is configured.", InfoBarSeverity.Warning, open: true);
    }

    private async Task OpenInBrowserAsync(int ticketId)
    {
        if (App.Services.Config.Tdx?.GetTicketBrowserUrl(ticketId) is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            ShowAction("No TeamDynamix URL is configured.", InfoBarSeverity.Warning, open: true);
            return;
        }
        if (!await Launcher.LaunchUriAsync(uri)) ShowAction("The browser could not be opened.", InfoBarSeverity.Warning, open: true);
    }

    private void Copy(string text, string done)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        ShowAction(done, InfoBarSeverity.Success);
    }

    /// <summary>Report on the open ticket; with <paramref name="open"/>, open the pane to say it even with no ticket.</summary>
    private void ShowAction(string message, InfoBarSeverity severity, bool open = false)
    {
        if (open && !_detailOpen) SetDetailOpen(true);
        ActionInfo.Severity = severity;
        ActionInfo.Message = message;
        ActionInfo.IsOpen = true;
    }

    // MARK: - Folding and the row menu

    /// <summary>Fold or unfold a parent; Alt-click folds or unfolds every parent, as Option-click does on the Mac.</summary>
    private void Fold_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TicketListItem row }) return;
        var alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);
        ViewModel.ToggleFold(row.Id, alt);
    }

    private void Row_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: TicketListItem row } element) return;
        args.Handled = true;
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Open", "", () => _ = OpenTicketAsync(row.Ticket)));
        menu.Items.Add(Item("Open in Browser", "", () => _ = OpenInBrowserAsync(row.Id)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Copy Ticket Number", "", () => Copy(row.Id.ToString(), "Ticket number copied")));
        menu.Items.Add(Item("Copy Link", "", () => CopyLink(row.Id)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Expand All", "", ViewModel.ExpandAll));
        menu.Items.Add(Item("Collapse All", "", ViewModel.CollapseAll));
        if (args.TryGetPosition(element, out var point)) menu.ShowAt(element, point);
        else menu.ShowAt(element);

        static MenuFlyoutItem Item(string text, string glyph, Action action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => action();
            return item;
        }
    }

    // MARK: - Board drag and drop

    private const string TicketDragFormat = "FleetMateTicketId";

    private void BoardCards_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is not TicketListItem card) return;
        e.Data.SetData(TicketDragFormat, card.Id.ToString());
        e.Data.SetText($"#{card.Id} {card.Title}");
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void BoardColumn_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(TicketDragFormat) || sender is not Panel column) return;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = $"Move to {column.Tag}";
        column.Background = TicketBrushes.Text("SubtleFillColorTertiaryBrush");
    }

    private void BoardColumn_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Panel column) column.Background = TicketBrushes.Text("SubtleFillColorSecondaryBrush");
    }

    /// <summary>
    /// Dropping a card on a column changes the grouped field, as the macOS
    /// board does. It writes to a shared service desk, so it asks first.
    /// </summary>
    private async void BoardColumn_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Panel { Tag: string columnKey } column) return;
        column.Background = TicketBrushes.Text("SubtleFillColorSecondaryBrush");
        if (!e.DataView.Contains(TicketDragFormat)) return;
        var deferral = e.GetDeferral();
        string? raw;
        try { raw = await e.DataView.GetDataAsync(TicketDragFormat) as string; }
        finally { deferral.Complete(); }
        if (!int.TryParse(raw, out var ticketId)) return;

        var groupBy = ViewModel.BoardGroupBy;
        var ticket = ViewModel.Loaded.FirstOrDefault(t => t.Id == ticketId);
        if (ticket == null || TicketBoardLayout.DropUpdates(ViewModel.Loaded, ticket, groupBy, columnKey) is not { } updates) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Move Ticket",
            Content = new TextBlock { Text = TicketBoardLayout.DropConfirmation(ticket, groupBy, columnKey), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Move",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (ViewModel.IsDemo || ViewModel.Tdx is not { } service)
        {
            ShowAction("Demo data can't be changed.", InfoBarSeverity.Informational, open: true);
            return;
        }

        var updated = await service.UpdateTicketAsync(ticketId, updates);
        if (updated == null)
        {
            Log.Warning("Board drop: failed to update ticket {Id} ({Field})", ticketId, groupBy);
            ShowAction($"TeamDynamix did not accept the {TicketBoardLayout.FieldLabel(groupBy)} for #{ticketId}. See the log for the response.",
                InfoBarSeverity.Warning, open: true);
            return;
        }
        ViewModel.Replace(updated);
        if (Detail.Ticket?.Id == ticketId) await Detail.OpenAsync(TicketReader.For(service), updated);
    }

    // MARK: - Layout

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) => FitColumns(e.NewSize.Width);

    /// <summary>
    /// The open ticket takes two fifths, at least 400 wide. Below 900 there is
    /// no room for both, so the ticket covers the list until it is closed.
    /// </summary>
    private void FitColumns(double pageWidth)
    {
        var available = Math.Max(0, pageWidth - PageRoot.Padding.Left - PageRoot.Padding.Right);
        var overlay = _detailOpen && available < 900;
        if (!_detailOpen)
        {
            ListColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailColumn.Width = new GridLength(0);
            ContentGrid.ColumnSpacing = 0;
        }
        else if (overlay)
        {
            ListColumn.Width = new GridLength(0);
            DetailColumn.Width = new GridLength(1, GridUnitType.Star);
            ContentGrid.ColumnSpacing = 0;
        }
        else
        {
            ListColumn.Width = new GridLength(3, GridUnitType.Star);
            DetailColumn.Width = new GridLength(Math.Max(400, available * 0.4));
            ContentGrid.ColumnSpacing = 12;
        }
        var listWidth = !_detailOpen ? available : overlay ? 0 : available - DetailColumn.Width.Value - 12;
        Layout.Fit(listWidth);
    }
}

/// <summary>A date-range choice for the Filters flyout's Time Period.</summary>
public sealed record DateRangeOption(TicketDateRangePreset Preset, string Label);
