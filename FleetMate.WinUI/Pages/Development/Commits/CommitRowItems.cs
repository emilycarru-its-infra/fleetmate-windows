using System.Collections.ObjectModel;
using FleetMate.Core.Development;
using FleetMate.Core.Models.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FleetMate.WinUI.Pages.Development.Commits;

/// <summary>
/// One repository card in Commits: its header, three commits, then a
/// "Show all N" row that expands the card in place (and "Show fewer" back).
/// </summary>
public sealed class RepositoryCommitsGroup : ObservableCollection<object>
{
    private bool _showAll;

    public RepositoryCommitsGroup(RepositoryCommits repository, bool showAll)
    {
        Repository = repository;
        _showAll = showAll;
        Fill();
    }

    public RepositoryCommits Repository { get; }
    public string Title => Repository.DisplayName;
    public string SourceLabel => Repository.Source.ShortName();
    public string Subtitle => DevelopmentText.RepositoryCommitsSubtitle(Repository);
    public bool ShowsAll => _showAll;

    public void ToggleShowAll()
    {
        _showAll = !_showAll;
        Fill();
    }

    private void Fill()
    {
        Clear();
        var commits = _showAll ? Repository.Commits : Repository.Commits.Take(DevelopmentText.CollapsedCommits);
        foreach (var commit in commits) Add(new CommitRowItem(commit, Repository));
        if (Repository.Commits.Count > DevelopmentText.CollapsedCommits) Add(new ShowAllItem(this, DevelopmentText.ShowAllLabel(Repository, _showAll)));
    }

    public override string ToString() => $"{Title}, {SourceLabel}, {Subtitle}";
}

public sealed class CommitRowItem(PullRequestCommit commit, RepositoryCommits repository)
{
    public PullRequestCommit Commit { get; } = commit;
    public RepositoryCommits Repository { get; } = repository;
    public string Subject => Commit.Subject;
    public string Byline => DevelopmentText.CommitByline(Commit);

    /// <summary>What a screen reader says for the row.</summary>
    public override string ToString() => $"{Subject}, {Byline}";
}

/// <summary>The "Show all N" / "Show fewer" row at the foot of a repository card.</summary>
public sealed class ShowAllItem(RepositoryCommitsGroup group, string label)
{
    public RepositoryCommitsGroup Group { get; } = group;
    public string Label { get; } = label;
    public string Glyph => Group.ShowsAll ? "" : "";

    public override string ToString() => $"{Label} in {Group.Title}";
}

/// <summary>Commit rows and the Show all row look different.</summary>
public sealed partial class CommitListTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Commit { get; set; }
    public DataTemplate? ShowAll { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is ShowAllItem ? ShowAll : Commit;
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
