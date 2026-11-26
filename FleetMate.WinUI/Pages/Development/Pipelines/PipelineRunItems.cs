using CommunityToolkit.Mvvm.ComponentModel;
using FleetMate.Core.Development;
using FleetMate.Core.Models.Projects;

namespace FleetMate.WinUI.Pages.Development.Pipelines;

/// <summary>One row in the Pipelines list. Its byline carries an active run's running time, refreshed in place.</summary>
public sealed partial class PipelineRunItem(PipelineRun run, bool awaitingApproval) : ObservableObject
{
    public PipelineRun Run { get; } = run;
    public bool AwaitingApproval { get; } = awaitingApproval;
    public PipelineRunStatus Status => Run.Status;

    public string Title => DevelopmentText.RunTitle(Run);
    public string SourceLabel => Run.Source.ShortName();
    public string Subtitle => DevelopmentText.RunSubtitle(Run);
    public string Byline => DevelopmentText.RunByline(Run);
    public string StatusLabel => DevelopmentText.RunStatusLabel(Run, AwaitingApproval);

    public void RefreshTiming() => OnPropertyChanged(nameof(Byline));

    /// <summary>What a screen reader says for the row.</summary>
    public override string ToString() => $"{Title}, {StatusLabel}, {Subtitle}, {Byline}";
}

/// <summary>One collapsible log section in the run pane; failed sections start open.</summary>
public sealed partial class LogSectionItem : ObservableObject
{
    public LogSectionItem(PipelineRunLog.Section section, bool expanded)
    {
        Section = section;
        IsExpanded = expanded;
    }

    public PipelineRunLog.Section Section { get; }
    public string Name => Section.Name;
    public string Text => Section.Text;
    public PipelineRunStatus Status => Section.Status;
    public string LogName => $"{Section.Name} log";

    private bool _isExpanded;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public static List<LogSectionItem> From(PipelineRunLog log)
    {
        var open = PipelineRunDetailText.ExpandedSections(log);
        return log.Sections.Select((s, i) => new LogSectionItem(s, open[i])).ToList();
    }
}
