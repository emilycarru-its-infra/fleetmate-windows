namespace FleetMate.Core.Services.Projects;

public partial class AzureDevOpsService
{
    public bool IsAttachmentUrl(Uri uri) => RecordAttachments.IsDevOpsAttachment(uri, new Uri(_config.BaseUrl));
    public async Task<StagedRecordAttachment> DownloadRecordAttachmentAsync(RecordAttachment attachment, CancellationToken ct = default)
    {
        var origin = new Uri(_config.BaseUrl);
        if (!Uri.TryCreate(attachment.Url, UriKind.Absolute, out var uri) || !IsAttachmentUrl(uri))
            throw new InvalidOperationException("This is not an attachment from the configured work item service.");
        var token = await GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("Sign in to preview this attachment.");
        return await RecordAttachments.DownloadAsync(attachment, origin, token,
            next => IsAttachmentUrl(next) || RecordAttachments.IsDevOpsAsset(next), ct);
    }
}
