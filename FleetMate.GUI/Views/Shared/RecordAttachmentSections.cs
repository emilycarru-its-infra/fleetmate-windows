using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Config;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;

namespace FleetMate.GUI.Views.Shared;

public static class RecordAttachmentSections
{
    public static List<RecordAttachment> WorkItem(WorkItemDetail item, AzureDevOpsService service) =>
        item.Relations.Where(r => r.Kind == WorkItemRelationKind.Attachment)
            .Select(r => new RecordAttachment(r.Name ?? "Attachment", r.Url, r.Url))
            .Concat(new[] { item.Description, item.ReproSteps, item.AcceptanceCriteria }
                .SelectMany(html => RecordAttachments.Discover(html, service.IsAttachmentUrl)))
            .Where(a => Uri.TryCreate(a.Url, UriKind.Absolute, out var uri) && RecordAttachments.IsWeb(uri))
            .DistinctBy(a => new Uri(a.Url).GetLeftPart(UriPartial.Path)).Take(100).ToList();

    public static void Add(Panel panel, IEnumerable<RecordAttachment> attachments,
        Func<RecordAttachment, CancellationToken, Task<StagedRecordAttachment>> download, bool heading = true)
    {
        var list = attachments.ToList();
        if (list.Count == 0) return;
        if (heading) panel.Children.Add(new TextBlock { Text = "Attachments", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
        panel.Children.Add(new RecordAttachmentList(list, download));
    }

    public static async Task<StagedRecordAttachment> DownloadGitHubAsync(string owner, string repo, int number,
        RecordAttachment requested, CancellationToken ct)
    {
        using var service = new GitHubProjectsService((Application.Current as App)?.Config?.GitHubProviderOrDefault() ?? new GitHubProviderConfig());
        // Resolve fresh signed asset URLs only after the current account can read the issue.
        var issue = await service.GetIssueDetailAsync(owner, repo, number, ct);
        var current = RecordAttachments.GitHub(issue.Body, issue.BodyHtml, issue.HtmlUrl).FirstOrDefault(a => Matches(a, requested));
        if (current == null)
        {
            var comments = await service.ListIssueCommentsAsync(owner, repo, number, ct);
            current = comments.SelectMany(c => RecordAttachments.GitHub(c.Body, c.BodyHtml, issue.HtmlUrl)).FirstOrDefault(a => Matches(a, requested));
        }
        if (current == null) throw new InvalidOperationException("The attachment is no longer available to the signed-in account.");
        return await service.DownloadRecordAttachmentAsync(current, ct);
    }

    private static bool Matches(RecordAttachment current, RecordAttachment requested) =>
        current.OriginalUrl == requested.OriginalUrl
        || new Uri(current.Url).GetLeftPart(UriPartial.Path) == new Uri(requested.Url).GetLeftPart(UriPartial.Path);
}
