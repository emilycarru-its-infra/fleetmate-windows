using FleetMate.Core.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FleetMate.WinUI.Pages.Development.Shared;

/// <summary>
/// One diff line. Inline it shows both line numbers and the marker; as one half
/// of a side-by-side row (<see cref="Side"/> set) only that side's number, with
/// the marker in front of the content. A half with no line is shaded.
/// </summary>
public sealed partial class DiffLineCell : UserControl
{
    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(
        nameof(Line), typeof(DiffLine), typeof(DiffLineCell), new PropertyMetadata(null, (d, _) => ((DiffLineCell)d).Render()));

    public static readonly DependencyProperty SideProperty = DependencyProperty.Register(
        nameof(Side), typeof(DiffCellSide), typeof(DiffLineCell), new PropertyMetadata(DiffCellSide.Inline, (d, _) => ((DiffLineCell)d).Render()));

    public DiffLineCell()
    {
        InitializeComponent();
        Render();
    }

    public DiffLine? Line
    {
        get => (DiffLine?)GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    public DiffCellSide Side
    {
        get => (DiffCellSide)GetValue(SideProperty);
        set => SetValue(SideProperty, value);
    }

    private void Render()
    {
        var line = Line;
        var inline = Side == DiffCellSide.Inline;
        NewColumn.Width = new GridLength(inline ? 44 : 0);
        MarkerColumn.Width = new GridLength(inline ? 16 : 0);
        NewNumber.Visibility = MarkerText.Visibility = DevelopmentUi.Show(inline);

        if (inline)
        {
            OldNumber.Text = DiffText.Number(line?.OldLine);
            NewNumber.Text = DiffText.Number(line?.NewLine);
            MarkerText.Text = line is null ? "" : DiffText.Marker(line.Kind);
            ContentText.Text = line?.Content ?? "";
            AutomationProperties.SetName(this, line is null ? "No line" : DiffText.Accessible(line));
        }
        else
        {
            var number = DiffText.SideNumber(line, Side == DiffCellSide.Left);
            OldNumber.Text = number;
            ContentText.Text = DiffText.SideContent(line);
            AutomationProperties.SetName(this, DiffText.Accessible(line, number));
        }

        var state = line?.Kind switch
        {
            null => "Empty",
            DiffLineKind.Addition => "Addition",
            DiffLineKind.Deletion => "Deletion",
            DiffLineKind.NoNewline => "NoNewline",
            _ => "Context",
        };
        VisualStateManager.GoToState(this, state, false);
    }
}

/// <summary>Where a <see cref="DiffLineCell"/> sits: an inline row, or the old or new half of a split row.</summary>
public enum DiffCellSide { Inline, Left, Right }
