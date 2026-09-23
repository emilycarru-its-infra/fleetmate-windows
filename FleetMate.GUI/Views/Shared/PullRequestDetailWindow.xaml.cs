using System.Windows;
using FleetMate.Core.Models.Projects;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// Pop-out host for <see cref="PullRequestDetailView"/>, opened from the
/// dashboard queue. The Code section shows the same view inline instead.
/// </summary>
public partial class PullRequestDetailWindow : Window
{
    private readonly UnifiedPullRequest _pullRequest;

    /// <summary>Set when an action changed the PR, so the caller can refresh.</summary>
    public bool QueueNeedsRefresh { get; private set; }

    public PullRequestDetailWindow(UnifiedPullRequest pullRequest, Window? owner = null)
    {
        _pullRequest = pullRequest;

        InitializeComponent();

        Owner = owner;
        Title = $"{pullRequest.Reference} · {pullRequest.Title}";

        // Size to the host rather than a fixed guess — a diff is unreadable in a
        // small window and silly in a maximised one.
        if (owner != null)
        {
            Width = Math.Max(720, owner.ActualWidth * 0.8);
            Height = Math.Max(520, owner.ActualHeight * 0.8);
        }
        else
        {
            Width = 1000;
            Height = 720;
        }

        // The PR is no longer in the state this window shows, so close and let
        // the queue reload rather than leaving a stale sheet open.
        DetailView.StateChanged += (_, _) =>
        {
            QueueNeedsRefresh = true;
            Close();
        };

        Loaded += async (_, _) => await DetailView.ShowAsync(_pullRequest);
    }
}
