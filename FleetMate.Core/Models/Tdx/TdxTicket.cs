using System.Text.Json.Serialization;

namespace FleetMate.Models.Tdx;

/// <summary>
/// TeamDynamix ticket
/// </summary>
public class TdxTicket
{
    [JsonPropertyName("ID")]
    public int Id { get; set; }

    [JsonPropertyName("ParentID")]
    public int? ParentId { get; set; }

    [JsonPropertyName("ParentTitle")]
    public string? ParentTitle { get; set; }

    [JsonPropertyName("TypeID")]
    public int TypeId { get; set; }

    [JsonPropertyName("TypeName")]
    public string? TypeName { get; set; }

    [JsonPropertyName("Classification")]
    public int Classification { get; set; }

    [JsonPropertyName("ClassificationName")]
    public string? ClassificationName { get; set; }

    [JsonPropertyName("Title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    [JsonPropertyName("Uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("AccountID")]
    public int? AccountId { get; set; }

    [JsonPropertyName("AccountName")]
    public string? AccountName { get; set; }

    [JsonPropertyName("SourceID")]
    public int? SourceId { get; set; }

    [JsonPropertyName("SourceName")]
    public string? SourceName { get; set; }

    [JsonPropertyName("StatusID")]
    public int StatusId { get; set; }

    [JsonPropertyName("StatusName")]
    public string? StatusName { get; set; }

    [JsonPropertyName("StatusClass")]
    public string? StatusClass { get; set; }

    [JsonPropertyName("ImpactID")]
    public int? ImpactId { get; set; }

    [JsonPropertyName("ImpactName")]
    public string? ImpactName { get; set; }

    [JsonPropertyName("UrgencyID")]
    public int? UrgencyId { get; set; }

    [JsonPropertyName("UrgencyName")]
    public string? UrgencyName { get; set; }

    [JsonPropertyName("PriorityID")]
    public int? PriorityId { get; set; }

    [JsonPropertyName("PriorityName")]
    public string? PriorityName { get; set; }

    [JsonPropertyName("PriorityOrder")]
    public double? PriorityOrder { get; set; }

    [JsonPropertyName("SlaID")]
    public int? SlaId { get; set; }

    [JsonPropertyName("SlaName")]
    public string? SlaName { get; set; }

    [JsonPropertyName("IsSlaViolated")]
    public bool IsSlaViolated { get; set; }

    [JsonPropertyName("IsSlaRespondByViolated")]
    public bool IsSlaRespondByViolated { get; set; }

    [JsonPropertyName("IsSlaResolveByViolated")]
    public bool IsSlaResolveByViolated { get; set; }

    [JsonPropertyName("RespondByDate")]
    public DateTime? RespondByDate { get; set; }

    [JsonPropertyName("ResolveByDate")]
    public DateTime? ResolveByDate { get; set; }

    [JsonPropertyName("SlaBeginDate")]
    public DateTime? SlaBeginDate { get; set; }

    [JsonPropertyName("IsOnHold")]
    public bool IsOnHold { get; set; }

    [JsonPropertyName("GoesOffHoldDate")]
    public DateTime? GoesOffHoldDate { get; set; }

    [JsonPropertyName("RequestorUid")]
    public Guid? RequestorUid { get; set; }

    [JsonPropertyName("RequestorName")]
    public string? RequestorName { get; set; }

    [JsonPropertyName("RequestorFirstName")]
    public string? RequestorFirstName { get; set; }

    [JsonPropertyName("RequestorLastName")]
    public string? RequestorLastName { get; set; }

    [JsonPropertyName("RequestorEmail")]
    public string? RequestorEmail { get; set; }

    [JsonPropertyName("RequestorPhone")]
    public string? RequestorPhone { get; set; }

    [JsonPropertyName("ActualMinutes")]
    public double? ActualMinutes { get; set; }

    [JsonPropertyName("EstimatedMinutes")]
    public double? EstimatedMinutes { get; set; }

    [JsonPropertyName("DaysOld")]
    public int DaysOld { get; set; }

    [JsonPropertyName("CreatedDate")]
    public DateTime CreatedDate { get; set; }

    [JsonPropertyName("CreatedUid")]
    public Guid? CreatedUid { get; set; }

    [JsonPropertyName("CreatedFullName")]
    public string? CreatedFullName { get; set; }

    [JsonPropertyName("CreatedEmail")]
    public string? CreatedEmail { get; set; }

    [JsonPropertyName("ModifiedDate")]
    public DateTime? ModifiedDate { get; set; }

    [JsonPropertyName("ModifiedUid")]
    public Guid? ModifiedUid { get; set; }

    [JsonPropertyName("ModifiedFullName")]
    public string? ModifiedFullName { get; set; }

    [JsonPropertyName("StartDate")]
    public DateTime? StartDate { get; set; }

    [JsonPropertyName("EndDate")]
    public DateTime? EndDate { get; set; }

    [JsonPropertyName("ResponsibleUid")]
    public Guid? ResponsibleUid { get; set; }

    [JsonPropertyName("ResponsibleFullName")]
    public string? ResponsibleFullName { get; set; }

    [JsonPropertyName("ResponsibleEmail")]
    public string? ResponsibleEmail { get; set; }

    [JsonPropertyName("ResponsibleGroupID")]
    public int? ResponsibleGroupId { get; set; }

    [JsonPropertyName("ResponsibleGroupName")]
    public string? ResponsibleGroupName { get; set; }

    [JsonPropertyName("RespondedDate")]
    public DateTime? RespondedDate { get; set; }

    [JsonPropertyName("RespondedUid")]
    public Guid? RespondedUid { get; set; }

    [JsonPropertyName("RespondedFullName")]
    public string? RespondedFullName { get; set; }

    [JsonPropertyName("CompletedDate")]
    public DateTime? CompletedDate { get; set; }

    [JsonPropertyName("CompletedUid")]
    public Guid? CompletedUid { get; set; }

    [JsonPropertyName("CompletedFullName")]
    public string? CompletedFullName { get; set; }

    [JsonPropertyName("ReviewerUid")]
    public Guid? ReviewerUid { get; set; }

    [JsonPropertyName("ReviewerFullName")]
    public string? ReviewerFullName { get; set; }

    [JsonPropertyName("ReviewerEmail")]
    public string? ReviewerEmail { get; set; }

    [JsonPropertyName("ReviewingGroupID")]
    public int? ReviewingGroupId { get; set; }

    [JsonPropertyName("ReviewingGroupName")]
    public string? ReviewingGroupName { get; set; }

    [JsonPropertyName("ServiceID")]
    public int? ServiceId { get; set; }

    [JsonPropertyName("ServiceName")]
    public string? ServiceName { get; set; }

    [JsonPropertyName("ServiceOfferingID")]
    public int? ServiceOfferingId { get; set; }

    [JsonPropertyName("ServiceOfferingName")]
    public string? ServiceOfferingName { get; set; }

    [JsonPropertyName("ServiceCategoryID")]
    public int? ServiceCategoryId { get; set; }

    [JsonPropertyName("ServiceCategoryName")]
    public string? ServiceCategoryName { get; set; }

    [JsonPropertyName("ArticleID")]
    public int? ArticleId { get; set; }

    [JsonPropertyName("ArticleSubject")]
    public string? ArticleSubject { get; set; }

    [JsonPropertyName("ArticleStatus")]
    public string? ArticleStatus { get; set; }

    [JsonPropertyName("ArticleCategoryPathNames")]
    public string? ArticleCategoryPathNames { get; set; }

    [JsonPropertyName("AppID")]
    public int AppId { get; set; }

    [JsonPropertyName("AppName")]
    public string? AppName { get; set; }

    [JsonPropertyName("FormID")]
    public int? FormId { get; set; }

    [JsonPropertyName("FormName")]
    public string? FormName { get; set; }

    [JsonPropertyName("LocationID")]
    public int? LocationId { get; set; }

    [JsonPropertyName("LocationName")]
    public string? LocationName { get; set; }

    [JsonPropertyName("LocationRoomID")]
    public int? LocationRoomId { get; set; }

    [JsonPropertyName("LocationRoomName")]
    public string? LocationRoomName { get; set; }

    [JsonPropertyName("Attributes")]
    public List<TdxAttribute>? Attributes { get; set; }

    [JsonPropertyName("Attachments")]
    public List<TdxAttachment>? Attachments { get; set; }

    [JsonPropertyName("Tasks")]
    public List<TdxTask>? Tasks { get; set; }

    [JsonPropertyName("NotifyRequestor")]
    public bool NotifyRequestor { get; set; }

    [JsonPropertyName("RefCode")]
    public string? RefCode { get; set; }

    /// <summary>
    /// Check if ticket is in an active/open state
    /// </summary>
    [JsonIgnore]
    public bool IsOpen => StatusClass?.Equals("None", StringComparison.OrdinalIgnoreCase) == true ||
                          StatusClass?.Equals("New", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Check if ticket is closed/resolved
    /// </summary>
    [JsonIgnore]
    public bool IsClosed => StatusClass?.Equals("Closed", StringComparison.OrdinalIgnoreCase) == true ||
                            StatusClass?.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>
/// Custom attribute on a ticket
/// </summary>
public class TdxAttribute
{
    [JsonPropertyName("ID")]
    public int Id { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Order")]
    public int Order { get; set; }

    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    [JsonPropertyName("SectionID")]
    public int? SectionId { get; set; }

    [JsonPropertyName("SectionName")]
    public string? SectionName { get; set; }

    [JsonPropertyName("FieldType")]
    public string? FieldType { get; set; }

    [JsonPropertyName("DataType")]
    public string? DataType { get; set; }

    [JsonPropertyName("Value")]
    public object? Value { get; set; }

    [JsonPropertyName("ValueText")]
    public string? ValueText { get; set; }

    [JsonPropertyName("ChoicesText")]
    public string? ChoicesText { get; set; }
}

/// <summary>
/// Attachment on a ticket
/// </summary>
public class TdxAttachment
{
    [JsonPropertyName("ID")]
    public Guid Id { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("ContentType")]
    public string? ContentType { get; set; }

    [JsonPropertyName("Size")]
    public long Size { get; set; }

    [JsonPropertyName("CreatedDate")]
    public DateTime CreatedDate { get; set; }

    [JsonPropertyName("CreatedUid")]
    public Guid? CreatedUid { get; set; }

    [JsonPropertyName("CreatedFullName")]
    public string? CreatedFullName { get; set; }

    [JsonPropertyName("Uri")]
    public string? Uri { get; set; }
}

/// <summary>
/// Task on a ticket
/// </summary>
public class TdxTask
{
    [JsonPropertyName("ID")]
    public int Id { get; set; }

    [JsonPropertyName("Title")]
    public string? Title { get; set; }

    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    [JsonPropertyName("StartDate")]
    public DateTime? StartDate { get; set; }

    [JsonPropertyName("EndDate")]
    public DateTime? EndDate { get; set; }

    [JsonPropertyName("CompleteWithinMinutes")]
    public int? CompleteWithinMinutes { get; set; }

    [JsonPropertyName("EstimatedMinutes")]
    public double? EstimatedMinutes { get; set; }

    [JsonPropertyName("ActualMinutes")]
    public double? ActualMinutes { get; set; }

    [JsonPropertyName("Order")]
    public int Order { get; set; }

    [JsonPropertyName("PercentComplete")]
    public int PercentComplete { get; set; }

    [JsonPropertyName("IsCompleted")]
    public bool IsCompleted { get; set; }

    [JsonPropertyName("IsRequired")]
    public bool IsRequired { get; set; }

    [JsonPropertyName("ResponsibleUid")]
    public Guid? ResponsibleUid { get; set; }

    [JsonPropertyName("ResponsibleFullName")]
    public string? ResponsibleFullName { get; set; }

    [JsonPropertyName("ResponsibleGroupID")]
    public int? ResponsibleGroupId { get; set; }

    [JsonPropertyName("ResponsibleGroupName")]
    public string? ResponsibleGroupName { get; set; }

    [JsonPropertyName("CompletedDate")]
    public DateTime? CompletedDate { get; set; }

    [JsonPropertyName("CompletedUid")]
    public Guid? CompletedUid { get; set; }

    [JsonPropertyName("CompletedFullName")]
    public string? CompletedFullName { get; set; }
}

/// <summary>
/// Request to create a new ticket
/// </summary>
public class CreateTicketRequest
{
    [JsonPropertyName("TypeID")]
    public int TypeId { get; set; }

    [JsonPropertyName("Title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    [JsonPropertyName("AccountID")]
    public int? AccountId { get; set; }

    [JsonPropertyName("SourceID")]
    public int? SourceId { get; set; }

    [JsonPropertyName("StatusID")]
    public int? StatusId { get; set; }

    [JsonPropertyName("PriorityID")]
    public int? PriorityId { get; set; }

    [JsonPropertyName("UrgencyID")]
    public int? UrgencyId { get; set; }

    [JsonPropertyName("ImpactID")]
    public int? ImpactId { get; set; }

    [JsonPropertyName("RequestorUid")]
    public Guid? RequestorUid { get; set; }

    [JsonPropertyName("RequestorEmail")]
    public string? RequestorEmail { get; set; }

    [JsonPropertyName("ResponsibleUid")]
    public Guid? ResponsibleUid { get; set; }

    [JsonPropertyName("ResponsibleGroupID")]
    public int? ResponsibleGroupId { get; set; }

    [JsonPropertyName("FormID")]
    public int? FormId { get; set; }

    [JsonPropertyName("ServiceID")]
    public int? ServiceId { get; set; }

    [JsonPropertyName("Attributes")]
    public List<TdxAttribute>? Attributes { get; set; }
}

/// <summary>
/// Ticket search parameters
/// </summary>
public class TicketSearchRequest
{
    [JsonPropertyName("StatusIDs")]
    public List<int>? StatusIds { get; set; }

    [JsonPropertyName("StatusClassNames")]
    public List<string>? StatusClassNames { get; set; }

    [JsonPropertyName("PriorityIDs")]
    public List<int>? PriorityIds { get; set; }

    [JsonPropertyName("TypeIDs")]
    public List<int>? TypeIds { get; set; }

    [JsonPropertyName("AccountIDs")]
    public List<int>? AccountIds { get; set; }

    [JsonPropertyName("RequestorUids")]
    public List<Guid>? RequestorUids { get; set; }

    [JsonPropertyName("ResponsibleUids")]
    public List<Guid>? ResponsibleUids { get; set; }

    [JsonPropertyName("ResponsibleGroupIDs")]
    public List<int>? ResponsibleGroupIds { get; set; }

    [JsonPropertyName("SearchText")]
    public string? SearchText { get; set; }

    [JsonPropertyName("CreatedDateFrom")]
    public DateTime? CreatedDateFrom { get; set; }

    [JsonPropertyName("CreatedDateTo")]
    public DateTime? CreatedDateTo { get; set; }

    [JsonPropertyName("ModifiedDateFrom")]
    public DateTime? ModifiedDateFrom { get; set; }

    [JsonPropertyName("ModifiedDateTo")]
    public DateTime? ModifiedDateTo { get; set; }

    [JsonPropertyName("MaxResults")]
    public int? MaxResults { get; set; }

    [JsonPropertyName("IsOnHold")]
    public bool? IsOnHold { get; set; }

    [JsonPropertyName("IsClosed")]
    public bool? IsClosed { get; set; }
}

/// <summary>
/// Feed entry (comment/update) on a ticket
/// </summary>
public class TdxFeedEntry
{
    [JsonPropertyName("ID")]
    public int Id { get; set; }

    [JsonPropertyName("Body")]
    public string? Body { get; set; }

    [JsonPropertyName("CreatedDate")]
    public DateTime CreatedDate { get; set; }

    [JsonPropertyName("CreatedUid")]
    public Guid? CreatedUid { get; set; }

    [JsonPropertyName("CreatedFullName")]
    public string? CreatedFullName { get; set; }

    [JsonPropertyName("IsPrivate")]
    public bool IsPrivate { get; set; }

    [JsonPropertyName("IsRichHtml")]
    public bool IsRichHtml { get; set; }
}

/// <summary>
/// Request to add a feed entry (comment)
/// </summary>
public class CreateFeedEntryRequest
{
    [JsonPropertyName("Comments")]
    public string Comments { get; set; } = string.Empty;

    [JsonPropertyName("IsPrivate")]
    public bool IsPrivate { get; set; }

    [JsonPropertyName("IsRichHtml")]
    public bool IsRichHtml { get; set; }

    [JsonPropertyName("Notify")]
    public List<Guid>? Notify { get; set; }
}
