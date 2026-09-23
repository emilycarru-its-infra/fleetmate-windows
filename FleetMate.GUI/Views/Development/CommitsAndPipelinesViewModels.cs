using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using FleetMate.Core.Models.Projects;

namespace FleetMate.GUI.Views.Development;

/// <summary>Status chip filter for the Pipelines list.</summary>
public enum PipelineStatusFilter { All, Running, Failed, Succeeded }

/// <summary>Filtering rules for Commits and Pipelines, kept out of the view so they can be tested.</summary>
public static class CommitsAndPipelinesFilter
{
    /// <summary>Repositories whose name, owner, or any commit subject/author/sha matches.</summary>
    public static List<RepositoryCommits> Commits(
        IEnumerable<RepositoryCommits> repos, DevelopmentSourceFilter source, string? search)
    {
        var needle = search?.Trim() ?? "";

        return repos
            .Where(r => source switch
            {
                DevelopmentSourceFilter.DevOps => r.Source == PullRequestSource.AzureDevOps,
                DevelopmentSourceFilter.GitHub => r.Source == PullRequestSource.GitHub,
                _ => true,
            })
            .Where(r => needle.Length == 0
                        || Has(r.DisplayName, needle)
                        || r.Commits.Any(c => Has(c.Subject, needle) || Has(c.AuthorName, needle) || c.Id.StartsWith(needle, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.LatestDate)
            .ToList();
    }

    /// <summary>
    /// Runs by source, status chip and search over name, repo, branch, requester
    /// and run number. Failed means the pipeline's LATEST run failed: an older
    /// red run under a newer green one is history, not something to act on.
    /// Running and Succeeded still match every run.
    /// </summary>
    public static List<PipelineRun> Runs(
        IEnumerable<PipelineRun> runs, DevelopmentSourceFilter source, PipelineStatusFilter status, string? search)
    {
        var needle = search?.Trim() ?? "";
        var all = runs.ToList();
        var latest = LatestRunIds(all);

        return all
            .Where(r => source switch
            {
                DevelopmentSourceFilter.DevOps => r.Source == PullRequestSource.AzureDevOps,
                DevelopmentSourceFilter.GitHub => r.Source == PullRequestSource.GitHub,
                _ => true,
            })
            .Where(r => status switch
            {
                PipelineStatusFilter.Running => r.Status.IsActive(),
                PipelineStatusFilter.Failed => IsFailing(r, latest),
                PipelineStatusFilter.Succeeded => r.Status == PipelineRunStatus.Succeeded,
                _ => true,
            })
            .Where(r => needle.Length == 0
                        || Has(r.PipelineName, needle) || Has(r.Repository, needle) || Has(r.Container, needle)
                        || Has(r.Branch, needle) || Has(r.TriggeredBy, needle) || Has(r.RunNumber, needle))
            .OrderByDescending(r => r.SortDate)
            .ToList();
    }

    /// <summary>
    /// One pipeline = source + container + pipeline id (name when there is no
    /// id); its latest run is the one that started last.
    /// </summary>
    public static HashSet<string> LatestRunIds(IEnumerable<PipelineRun> runs) =>
        runs.GroupBy(r => $"{r.Source}|{r.Container}|{(r.PipelineId?.ToString() ?? r.PipelineName)}")
            .Select(g => g.OrderByDescending(r => r.SortDate).First().Id)
            .ToHashSet();

    public static bool IsFailing(PipelineRun run, HashSet<string> latestIds) =>
        run.Status is PipelineRunStatus.Failed or PipelineRunStatus.Partial && latestIds.Contains(run.Id);

    /// <summary>Pipelines whose latest run failed — the Failed chip's count.</summary>
    public static int FailingCount(IReadOnlyCollection<PipelineRun> runs)
    {
        var latest = LatestRunIds(runs);
        return runs.Count(r => IsFailing(r, latest));
    }

    private static bool Has(string? hay, string needle) =>
        hay?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>One repository card in Commits: three commits, then "Show all N".</summary>
public sealed class RepositoryCommitsViewModel : INotifyPropertyChanged
{
    public const int Collapsed = 3;

    private bool _showAll;

    public required RepositoryCommits Repository { get; init; }

    public string Title => Repository.DisplayName;
    public string SourceLabel => Repository.Source.ShortName();
    public string Subtitle =>
        $"{Repository.DefaultBranch ?? "default branch"} · {DevelopmentNotificationRowViewModel.Age(Repository.LatestDate)}";

    public IEnumerable<CommitRowViewModel> VisibleCommits =>
        (_showAll ? Repository.Commits : Repository.Commits.Take(Collapsed))
        .Select(c => new CommitRowViewModel { Commit = c, Repository = Repository });

    public Visibility ShowAllVisibility =>
        Repository.Commits.Count > Collapsed ? Visibility.Visible : Visibility.Collapsed;

    public string ShowAllLabel => _showAll ? "Show fewer" : $"Show all {Repository.Commits.Count}";

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
    public string Byline => $"{Commit.AuthorName ?? "unknown"} · {Commit.ShortSha} · {DevelopmentNotificationRowViewModel.Age(Commit.Date)}";
}

/// <summary>One row in the Pipelines list.</summary>
public sealed class PipelineRunRowViewModel
{
    public required PipelineRun Run { get; init; }

    public string Title => $"{Run.PipelineName} #{Run.RunNumber}";
    public string SourceLabel => Run.Source.ShortName();

    public string Subtitle
    {
        get
        {
            var where = string.IsNullOrEmpty(Run.Repository) || Run.Repository == Run.Container
                ? Run.Container
                : $"{Run.Container}/{Run.Repository}";
            return string.IsNullOrEmpty(Run.Branch) ? where : $"{where} · {Run.Branch}";
        }
    }

    public string Byline =>
        string.Join(" · ", new[]
        {
            Run.TriggeredBy,
            DevelopmentNotificationRowViewModel.Age(Run.StartedAt ?? Run.FinishedAt),
            FormatDuration(Run.Duration),
        }.Where(s => !string.IsNullOrEmpty(s)));

    public string StatusLabel => Run.Status.DisplayName();
    public string StatusGlyph => PipelineStatusVisuals.Glyph(Run.Status);
    public Brush StatusBrush => PipelineStatusVisuals.Brush(Run.Status);

    /// <summary>"42s", "3m 05s", "1h 12m".</summary>
    public static string FormatDuration(TimeSpan? duration)
    {
        if (duration is not { } d || d < TimeSpan.Zero) return "";
        if (d.TotalMinutes < 1) return $"{(int)d.TotalSeconds}s";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m {d.Seconds:00}s";
        return $"{(int)d.TotalHours}h {d.Minutes:00}m";
    }
}

/// <summary>
/// Status colours for runs and log sections. No red anywhere: failed and
/// partial are orange, running yellow, succeeded green, everything else grey.
/// </summary>
public static class PipelineStatusVisuals
{
    public static string Glyph(PipelineRunStatus status) => status switch
    {
        PipelineRunStatus.Succeeded => "",
        PipelineRunStatus.Failed => "",
        PipelineRunStatus.Partial => "",
        PipelineRunStatus.Running => "",
        PipelineRunStatus.Queued => "",
        PipelineRunStatus.Cancelled => "",
        _ => "",
    };

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
        var rows = log.Sections.Select(s => new PipelineLogSectionViewModel
        {
            Section = s,
            IsExpanded = s.Status is PipelineRunStatus.Failed or PipelineRunStatus.Partial,
        }).ToList();

        if (rows.Count > 0 && rows.All(r => !r.IsExpanded)) rows[^1].IsExpanded = true;
        return rows;
    }
}
