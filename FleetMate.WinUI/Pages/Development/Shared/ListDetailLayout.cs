using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FleetMate.WinUI.Pages.Development.Shared;

/// <summary>
/// A list beside its detail, as on Mac: a fixed list column (400, never under
/// 280) and the detail filling the rest. Below <see cref="NarrowWidth"/> the
/// two stack: the list fills the segment, picking a row shows the detail in its
/// place, and the detail's Back button returns to the list.
/// </summary>
public sealed class ListDetailLayout
{
    public const double NarrowWidth = 720;
    public const double ListWidth = 400;
    public const double ListMinWidth = 280;

    private readonly ColumnDefinition _listColumn;
    private readonly ColumnDefinition _detailColumn;
    private readonly FrameworkElement _list;
    private readonly FrameworkElement _detail;
    private readonly Button _back;
    private bool _detailOpen;
    private readonly Thickness _listPadding;
    private readonly Thickness _detailPadding;
    private readonly Thickness _detailBorder;

    public ListDetailLayout(Grid root, ColumnDefinition listColumn, ColumnDefinition detailColumn,
        FrameworkElement list, FrameworkElement detail, Button back)
    {
        _listColumn = listColumn;
        _detailColumn = detailColumn;
        _list = list;
        _detail = detail;
        _back = back;
        // The divider and gutters between the panes go when they stack.
        _listPadding = (list as Grid)?.Padding ?? default;
        _detailPadding = (detail as Grid)?.Padding ?? default;
        _detailBorder = (detail as Grid)?.BorderThickness ?? default;
        back.Click += (_, _) => ShowList();
        root.SizeChanged += (_, e) => Apply(e.NewSize.Width);
    }

    public bool IsNarrow { get; private set; }

    /// <summary>A row was picked: in the narrow layout the detail takes the list's place.</summary>
    public void ShowDetail()
    {
        _detailOpen = true;
        Apply(null);
        if (IsNarrow) _back.Focus(FocusState.Programmatic);
    }

    public void ShowList()
    {
        var wasOpen = _detailOpen && IsNarrow;
        _detailOpen = false;
        Apply(null);
        if (wasOpen) _list.Focus(FocusState.Programmatic);
    }

    private double _width;

    private void Apply(double? width)
    {
        if (width is { } w) _width = w;
        IsNarrow = _width > 0 && _width < NarrowWidth;
        if (IsNarrow)
        {
            _listColumn.MinWidth = 0;
            _listColumn.Width = _detailOpen ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            _detailColumn.Width = _detailOpen ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            _list.Visibility = DevelopmentUi.Show(!_detailOpen);
            _detail.Visibility = DevelopmentUi.Show(_detailOpen);
        }
        else
        {
            _listColumn.MinWidth = ListMinWidth;
            _listColumn.Width = new GridLength(ListWidth);
            _detailColumn.Width = new GridLength(1, GridUnitType.Star);
            _list.Visibility = _detail.Visibility = Visibility.Visible;
        }
        if (_list is Grid listGrid) listGrid.Padding = IsNarrow ? default : _listPadding;
        if (_detail is Grid detailGrid)
        {
            detailGrid.Padding = IsNarrow ? default : _detailPadding;
            detailGrid.BorderThickness = IsNarrow ? default : _detailBorder;
        }
        _back.Visibility = DevelopmentUi.Show(IsNarrow && _detailOpen);
    }
}
