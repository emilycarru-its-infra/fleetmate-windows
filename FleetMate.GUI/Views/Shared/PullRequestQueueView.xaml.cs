using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;
using Serilog;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// The signed-in user's pull request queue across Azure DevOps and GitHub.
///
/// The loaded queue lives on <see cref="App"/> rather than in this control, so
/// switching tabs does not throw it away and refetch. That was the Mac's fix for
/// the same problem and it applies here for the same reason: a WPF page is
/// rebuilt on navigation, and state held in the view dies with it.
/// </summary>
public partial class PullRequestQueueView : UserControl
{
    private string _sourceFilter = "all";
    private bool _isLoading;

    public PullRequestQueueView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Render whatever is already cached first so a tab switch is instant,
        // then only go to the network if there is nothing to show.
        if (App.Current is App app && app.PullRequestQueue is { } cached)
        {
            Render(cached);
            return;
        }

        await LoadAsync();
    }

    /// <summary>Fetch both providers concurrently and cache the result on the app.</summary>
    public async Task LoadAsync(bool forceRefresh = false)
    {
        if (_isLoading) return;
        if (App.Current is not App app) return;

        if (!forceRefresh && app.PullRequestQueue is { } cached)
        {
            Render(cached);
            return;
        }

        _isLoading = true;
        QueueProgress.Visibility = Visibility.Visible;

        try
        {
            var config = app.Config;
            var tasks = new List<Task<PullRequestQueue>>();

            if (!string.IsNullOrWhiteSpace(config.AzureDevOps?.Organization))
            {
                tasks.Add(Task.Run(async () =>
                {
                    using var devops = new AzureDevOpsService(config.AzureDevOps!);
                    return await devops.GetMyPullRequestsAsync();
                }));
            }

            if (config.Tasks?.Providers?.GitHub is { } gh)
            {
                tasks.Add(Task.Run(async () =>
                {
                    using var github = new GitHubPullRequestService(gh);
                    return await github.GetMyPullRequestsAsync();
                }));
            }

            var queue = new PullRequestQueue();
            foreach (var result in await Task.WhenAll(tasks)) queue.Merge(result);

            app.PullRequestQueue = queue;
            Render(queue);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[prs] Failed to load the pull request queue");
        }
        finally
        {
            _isLoading = false;
            QueueProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void Render(PullRequestQueue queue)
    {
        var created = Filter(queue.Section(PullRequestRelation.CreatedByMe));
        var assigned = Filter(queue.Section(PullRequestRelation.AssignedToMe));

        CreatedList.ItemsSource = created;
        AssignedList.ItemsSource = assigned;

        CreatedHeader.Visibility = created.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AssignedHeader.Visibility = assigned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        QueueCountText.Text = queue.IsEmpty ? "" : $"({created.Count + assigned.Count})";

        // Not being signed in to GitHub is a normal state, not a fault. An
        // orange banner for it cries wolf on a queue that is working exactly as
        // it should for the providers you have actually configured.
        QueueErrors.ItemsSource = queue.Errors
            .Where(e => !IsExpectedSignedOut(e))
            .Select(e => $"{e.Source.DisplayName()} could not be reached — {e.Message}")
            .ToList();

        if (created.Count == 0 && assigned.Count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            // Distinguish "nothing matches this filter" from "nothing at all",
            // so a chip left on doesn't read as an empty queue.
            EmptyState.Text = queue.IsEmpty
                ? "No open pull requests."
                : $"No pull requests from {_sourceFilter}.";
        }
        else
        {
            EmptyState.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// True when a provider failure just means "you have not signed in here",
    /// which is a configuration state rather than something going wrong.
    /// </summary>
    internal static bool IsExpectedSignedOut(PullRequestQueueError error)
    {
        if (error.Source != PullRequestSource.GitHub) return false;

        var message = error.Message ?? "";

        // Matched against the messages the stack actually produces rather than
        // plausible-looking ones: "No authentication token available" is what
        // the client throws when the token source comes back empty, and "Bad
        // credentials" is GitHub's own reply to a stale token.
        return message.Contains("authentication token", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not authenticated", StringComparison.OrdinalIgnoreCase)
            || message.Contains("bad credentials", StringComparison.OrdinalIgnoreCase)
            || message.Contains("gh auth login", StringComparison.OrdinalIgnoreCase);
    }

    private List<PullRequestRowViewModel> Filter(IReadOnlyList<UnifiedPullRequest> prs) =>
        prs.Where(Matches)
           .Select(pr => new PullRequestRowViewModel { PullRequest = pr })
           .ToList();

    private bool Matches(UnifiedPullRequest pr) => _sourceFilter switch
    {
        "devops" => pr.Source == PullRequestSource.AzureDevOps,
        "github" => pr.Source == PullRequestSource.GitHub,
        _ => true,
    };

    // MARK: - Events

    private void OnSourceChipClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;

        _sourceFilter = clicked.Tag as string ?? "all";

        // Chips are mutually exclusive, so re-clicking the active one must not
        // leave every chip off and the list silently unfiltered.
        AllChip.IsChecked = _sourceFilter == "all";
        DevOpsChip.IsChecked = _sourceFilter == "devops";
        GitHubChip.IsChecked = _sourceFilter == "github";

        if (App.Current is App { PullRequestQueue: { } queue }) Render(queue);
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await LoadAsync(forceRefresh: true);

    private async void OnRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PullRequestRowViewModel row }) return;

        // Opens in-app rather than in the browser. The window keeps an
        // open-in-browser escape hatch for anything it cannot show.
        var viewer = new PullRequestDetailWindow(row.PullRequest, Window.GetWindow(this));
        viewer.ShowDialog();

        // Completing or abandoning from the sheet leaves the queue stale.
        if (viewer.QueueNeedsRefresh) await LoadAsync(forceRefresh: true);
    }

    private async void OnCompleteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PullRequestRowViewModel row }) return;

        // Completing merges into a target branch and is not undoable from here,
        // so it confirms even though it was asked for. The prompt names the
        // branches and repository: a queue full of similar titles is exactly
        // where the wrong row gets clicked.
        var confirmed = Confirm(
            "Complete pull request?",
            $"{row.Title}\n\n{row.BranchLabel}\nin {row.RepositoryLabel}\n\n" +
            "This merges the pull request into its target branch.");

        if (!confirmed) return;

        await RunActionAsync(row, "Complete", (service, pr) =>
            service.CompletePullRequestAsync(pr.Repository, pr.Number, pr.Container));
    }

    private async void OnAbandonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PullRequestRowViewModel row }) return;

        var confirmed = Confirm(
            "Abandon pull request?",
            $"{row.Title}\n\n{row.BranchLabel}\nin {row.RepositoryLabel}\n\n" +
            "This closes the pull request without merging and notifies its reviewers.");

        if (!confirmed) return;

        await RunActionAsync(row, "Abandon", (service, pr) =>
            service.AbandonPullRequestAsync(pr.Repository, pr.Number, pr.Container));
    }

    private async Task RunActionAsync(
        PullRequestRowViewModel row,
        string action,
        Func<AzureDevOpsService, UnifiedPullRequest, Task<PullRequestActionResult>> perform)
    {
        if (App.Current is not App app || app.Config.AzureDevOps is not { } adoConfig) return;

        QueueProgress.Visibility = Visibility.Visible;
        try
        {
            using var service = new AzureDevOpsService(adoConfig);
            var result = await perform(service, row.PullRequest);

            if (!result.Success)
            {
                MessageBox.Show(
                    $"{action} failed:\n\n{result.Error}",
                    $"{action} pull request", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Reload so the acted-on PR drops out rather than lingering in a
            // state the server no longer agrees with.
            await LoadAsync(forceRefresh: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[prs] {Action} failed for {Repo}#{Number}",
                action, row.PullRequest.Repository, row.PullRequest.Number);
            MessageBox.Show($"{action} failed:\n\n{ex.Message}",
                $"{action} pull request", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            QueueProgress.Visibility = Visibility.Collapsed;
        }
    }

    private static bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)
        == MessageBoxResult.OK;
}
