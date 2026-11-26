using System.Windows;
using System.Windows.Media;
using FleetMate.Core.Models.Projects;

namespace FleetMate.GUI.Views.Shared;

/// <summary>One chip in the checks strip.</summary>
public sealed class PullRequestCheckViewModel
{
    public required PullRequestCheck Check { get; init; }

    public string Name => PullRequestPresentation.CheckName(Check);
    public string? DetailsUrl => Check.DetailsUrl;
    public string ToolTip => PullRequestPresentation.CheckToolTip(Check);

    public string Glyph => PullRequestPresentation.CheckGlyph(Check.State);

    public Brush Brush => Check.State switch
    {
        PullRequestCheckState.Success => new SolidColorBrush(Color.FromRgb(0x2D, 0xA4, 0x4E)),
        PullRequestCheckState.Failure => new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x1F)),
        PullRequestCheckState.Pending => new SolidColorBrush(Color.FromRgb(0x3A, 0x6E, 0xA5)),
        _ => Brushes.Gray,
    };

    public static string Summary(IReadOnlyCollection<PullRequestCheck> checks) => PullRequestPresentation.ChecksSummary(checks);

    public static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
