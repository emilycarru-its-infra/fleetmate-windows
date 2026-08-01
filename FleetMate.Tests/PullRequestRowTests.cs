using System.Windows;
using FleetMate.Core.Models.Projects;
using FleetMate.GUI.Views.Shared;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The queue row's display logic. Most of it is conditional on more than one
/// field, which is exactly the kind of rule that looks obviously right and is
/// wrong at the boundary.
/// </summary>
public class PullRequestRowViewModelTests
{
    private static PullRequestRowViewModel Row(
        PullRequestSource source = PullRequestSource.AzureDevOps,
        PullRequestState state = PullRequestState.Open,
        DateTime? created = null,
        DateTime? updated = null,
        int comments = 0,
        bool conflicts = false,
        params string[] reviewers) => new()
    {
        PullRequest = new UnifiedPullRequest
        {
            Source = source,
            Number = 42,
            Title = "Add the widget",
            AuthorName = "Ada Lovelace",
            Container = "acme",
            Repository = "fleet",
            SourceBranch = "feature/widget",
            TargetBranch = "main",
            State = state,
            CreatedAt = created,
            UpdatedAt = updated,
            CommentCount = comments,
            HasConflicts = conflicts,
            Reviewers = reviewers.Select(r => new PullRequestReviewer { DisplayName = r }).ToList(),
        },
    };

    [Fact]
    public void BylineNamesTheAuthorAndReference()
    {
        var byline = Row(created: DateTime.UtcNow.AddDays(-3)).Byline;

        Assert.StartsWith("Ada Lovelace · !42 · ", byline);
    }

    [Fact]
    public void BylineSaysCreatedWhenNothingHasTouchedIt()
    {
        var created = DateTime.UtcNow.AddDays(-3);
        // Same second, as both providers stamp a brand-new PR.
        var row = Row(created: created, updated: created);

        Assert.Contains("Created", row.Byline);
        Assert.DoesNotContain("Updated", row.Byline);
    }

    [Fact]
    public void BylineSaysUpdatedOnceItHasBeenTouched()
    {
        var row = Row(created: DateTime.UtcNow.AddDays(-9), updated: DateTime.UtcNow.AddDays(-1));

        Assert.Contains("Updated", row.Byline);
    }

    [Theory]
    [InlineData(0.5, "just now")]
    [InlineData(30, "30m ago")]
    [InlineData(150, "2h ago")]
    [InlineData(2880, "2d ago")]
    public void AgeReadsInTheQueuesOwnUnits(double minutesAgo, string expected)
    {
        var row = Row(created: DateTime.UtcNow.AddMinutes(-minutesAgo));

        Assert.Contains(expected, row.Byline);
    }

    [Fact]
    public void AgeDegradesGracefullyWithNoTimestamps()
    {
        // A row with no dates must still render rather than throwing in a template.
        Assert.Contains("recently", Row().Byline);
    }

    [Theory]
    [InlineData(PullRequestState.Open, false, "Open")]
    [InlineData(PullRequestState.Draft, false, "Draft")]
    [InlineData(PullRequestState.Merged, false, "Merged")]
    [InlineData(PullRequestState.Closed, false, "Closed")]
    // Conflicts outrank "open": it is the thing the row needs to say.
    [InlineData(PullRequestState.Open, true, "Conflicts")]
    public void StateLabelReflectsStateAndConflicts(
        PullRequestState state, bool conflicts, string expected)
    {
        Assert.Equal(expected, Row(state: state, conflicts: conflicts).StateLabel);
    }

    [Fact]
    public void ActionsShowOnlyForLiveAzureDevOpsPullRequests()
    {
        // Offering Complete on a merged PR is a button that always errors.
        Assert.Equal(Visibility.Visible,
            Row(PullRequestSource.AzureDevOps, PullRequestState.Open).ActionVisibility);
        Assert.Equal(Visibility.Visible,
            Row(PullRequestSource.AzureDevOps, PullRequestState.Draft).ActionVisibility);
        Assert.Equal(Visibility.Collapsed,
            Row(PullRequestSource.AzureDevOps, PullRequestState.Merged).ActionVisibility);
        Assert.Equal(Visibility.Collapsed,
            Row(PullRequestSource.AzureDevOps, PullRequestState.Closed).ActionVisibility);
    }

    [Fact]
    public void GitHubRowsNeverOfferTheseActions()
    {
        // Complete and Abandon are Azure DevOps operations; GitHub has neither.
        Assert.Equal(Visibility.Collapsed,
            Row(PullRequestSource.GitHub, PullRequestState.Open).ActionVisibility);
    }

    [Fact]
    public void CommentsAreHiddenWhenThereAreNone()
    {
        Assert.Equal(Visibility.Collapsed, Row(comments: 0).CommentVisibility);
        Assert.Equal("", Row(comments: 0).CommentLabel);

        Assert.Equal(Visibility.Visible, Row(comments: 5).CommentVisibility);
        Assert.Equal("5", Row(comments: 5).CommentLabel);
    }

    [Fact]
    public void ReviewersRenderAsInitials()
    {
        var row = Row(reviewers: new[] { "Ada Lovelace", "Grace Hopper" });

        Assert.Equal("AL · GH", row.ReviewerLabel);
        Assert.Equal(Visibility.Visible, row.ReviewerVisibility);
    }

    [Fact]
    public void ReviewersAreHiddenWhenNobodyIsOnIt()
    {
        Assert.Equal(Visibility.Collapsed, Row().ReviewerVisibility);
    }

    [Fact]
    public void LabelsIdentifyTheRepositoryAndBranches()
    {
        var row = Row();

        Assert.Equal("acme/fleet", row.RepositoryLabel);
        Assert.Equal("feature/widget → main", row.BranchLabel);
        Assert.Equal("DevOps", row.SourceLabel);
    }

    [Fact]
    public void SourceLabelIsCompactEnoughForAChip()
    {
        // "Azure DevOps" is too wide for the row badge.
        Assert.Equal("DevOps", Row(PullRequestSource.AzureDevOps).SourceLabel);
        Assert.Equal("GitHub", Row(PullRequestSource.GitHub).SourceLabel);
    }
}
