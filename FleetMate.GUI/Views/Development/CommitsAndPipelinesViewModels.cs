using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using FleetMate.Core.Models.Projects;
using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Development;

/// <summary>One repository card in Commits: three commits, then "Show all N".</summary>
public sealed class RepositoryCommitsViewModel : INotifyPropertyChanged
{
    public const int Collapsed = DevelopmentText.CollapsedCommits;

    private bool _showAll;

    public required RepositoryCommits Repository { get; init; }

    public string Title => Repository.DisplayName;
    public string SourceLabel => Repository.Source.ShortName();
    public string Subtitle => DevelopmentText.RepositoryCommitsSubtitle(Repository);

    public IEnumerable<CommitRowViewModel> VisibleCommits =>
        (_showAll ? Repository.Commits : Repository.Commits.Take(Collapsed))
        .Select(c => new CommitRowViewModel { Commit = c, Repository = Repository });

    public Visibility ShowAllVisibility =>
        Repository.Commits.Count > Collapsed ? Visibility.Visible : Visibility.Collapsed;

    public string ShowAllLabel => DevelopmentText.ShowAllLabel(Repository, _showAll);

    public void ToggleShowAll()
    {
        _showAll = !_showAll;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VisibleCommits)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowAllLabel)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class CommitRowViewModel
{
    public required PullRequestCommit Commit { get; init; }
    public required RepositoryCommits Repository { get; init; }

    public string Subject => Commit.Subject;
    public string Byline => DevelopmentText.CommitByline(Commit);
}

/// <summary>One row in the Pipelines list.</summary>
public sealed class PipelineRunRowViewModel : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs TimingChanged = new(nameof(Byline));
    public event PropertyChangedEventHandler? PropertyChanged;
    public void RefreshTiming() => PropertyChanged?.Invoke(this, TimingChanged);

    public required PipelineRun Run { get; init; }

    public string Title => DevelopmentText.RunTitle(Run);
    public string SourceLabel => Run.Source.ShortName();

    public string Subtitle => DevelopmentText.RunSubtitle(Run);

    public string Byline => DevelopmentText.RunByline(Run);

    /// <summary>True while a pending approval holds this run.</summary>
    public bool AwaitingApproval { get; init; }
    public Visibility ApprovalVisibility => AwaitingApproval ? Visibility.Visible : Visibility.Collapsed;

    public string StatusLabel => DevelopmentText.RunStatusLabel(Run, AwaitingApproval);
    public string StatusGlyph => PipelineStatusVisuals.Glyph(Run.Status);
    public Brush StatusBrush => PipelineStatusVisuals.Brush(Run.Status);

    public static string FormatDuration(TimeSpan? duration) => DevelopmentText.FormatDuration(duration);
}

/// <summary>
/// Status colours for runs and log sections. No red anywhere: failed and
/// partial are orange, running yellow, succeeded green, everything else grey.
/// </summary>
public static class PipelineStatusVisuals
{
    public const string WaitingForApproval = DevelopmentText.WaitingForApproval;

    /// <summary>The approval badge and banner colour: orange, never red.</summary>
    public static readonly Color ApprovalColor = Color.FromRgb(0xE0, 0x7A, 0x1F);

    public static string Glyph(PipelineRunStatus status) => DevelopmentText.StatusGlyph(status);

    public static Brush Brush(PipelineRunStatus status) => status switch
    {
        PipelineRunStatus.Succeeded => new SolidColorBrush(Color.FromRgb(0x2D, 0xA4, 0x4E)),
        PipelineRunStatus.Failed or PipelineRunStatus.Partial => new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x1F)),
        PipelineRunStatus.Running => new SolidColorBrush(Color.FromRgb(0xD9, 0x9E, 0x0B)),
        _ => Brushes.Gray,
    };
}

/// <summary>One collapsible log section in the run viewer.</summary>
public sealed class PipelineLogSectionViewModel
{
    public required PipelineRunLog.Section Section { get; init; }
    public bool IsExpanded { get; set; }

    public string Name => Section.Name;
    public string Text => Section.Text;
    public string StatusGlyph => PipelineStatusVisuals.Glyph(Section.Status);
    public Brush StatusBrush => PipelineStatusVisuals.Brush(Section.Status);

    /// <summary>Failed sections open; on a run with no failure, only the last one.</summary>
    public static List<PipelineLogSectionViewModel> From(PipelineRunLog log)
    {
        var open = PipelineRunDetailText.ExpandedSections(log);
        return log.Sections.Select((s, i) => new PipelineLogSectionViewModel { Section = s, IsExpanded = open[i] }).ToList();
    }
}
