using CommunityToolkit.Mvvm.ComponentModel;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using Microsoft.UI.Xaml;

namespace FleetMate.WinUI.Pages.Tickets;

/// <summary>
/// The open ticket in the detail pane: its fields, description and
/// activity, read fresh from TeamDynamix when it opens. A slow answer for a
/// ticket no longer open never paints over the one that is
/// (<see cref="TicketDetailLoadState"/>).
/// </summary>
public sealed partial class TicketDetailViewModel : ObservableObject
{
    private readonly TicketDetailLoadState _loads = new();
    private List<TdxFeedEntry> _feed = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen), nameof(IdLabel), nameof(Title), nameof(Eyebrow), nameof(Status), nameof(Priority),
        nameof(Classification), nameof(Form), nameof(Service), nameof(Type), nameof(Requestor), nameof(RequestorEmail),
        nameof(Group), nameof(Responsible), nameof(Created), nameof(Modified), nameof(Description), nameof(DescriptionVisibility),
        nameof(NoDescriptionVisibility), nameof(Sla), nameof(SlaVisibility), nameof(Parent), nameof(ParentVisibility),
        nameof(StatusBrush), nameof(ResponsibleForeground), nameof(SpokenTitle))]
    public partial TdxTicket? Ticket { get; private set; }

    public bool IsOpen => Ticket != null;

    public string IdLabel => Ticket is { } t ? $"#{t.Id}" : "";
    public string Title => Ticket?.Title ?? "Untitled";
    public string Eyebrow => Ticket is { } t
        ? string.Join(" · ", new[] { $"#{t.Id}", t.TypeName, t.ClassificationName ?? TdxClassification.Name(t.Classification) }
            .Where(s => !string.IsNullOrWhiteSpace(s)))
        : "";
    public string SpokenTitle => Ticket is { } t ? $"Ticket {t.Id}, {Title}" : "No ticket open";
    public string Status => Ticket?.StatusName ?? "-";
    public string Priority => Ticket?.PriorityName ?? "-";
    public string Classification => Ticket is { } t ? t.ClassificationName ?? TdxClassification.Name(t.Classification) ?? "-" : "-";
    public string Form => Ticket?.FormName ?? "-";
    public string Service => Ticket?.ServiceName ?? "-";
    public string Type => Ticket?.TypeName ?? "-";
    public string Requestor => Ticket?.RequestorName ?? "-";
    public string RequestorEmail => string.IsNullOrWhiteSpace(Ticket?.RequestorEmail) ? "-" : Ticket!.RequestorEmail!;
    public string Group => string.IsNullOrWhiteSpace(Ticket?.ResponsibleGroupName) ? "-" : Ticket!.ResponsibleGroupName!;
    public string Responsible => string.IsNullOrWhiteSpace(Ticket?.ResponsibleFullName) ? TicketBoardLayout.Unassigned : Ticket!.ResponsibleFullName!;
    public string Created => TicketFeedView.FormatDate(Ticket?.CreatedDate);
    public string Modified => TicketFeedView.FormatDate(Ticket?.ModifiedDate);
    public string Sla => Ticket is { } t ? (t.IsSlaViolated ? $"{t.SlaName ?? "SLA"} · Violated" : t.SlaName ?? "None") : "-";
    public Visibility SlaVisibility => Ticket?.IsSlaViolated == true ? Visibility.Visible : Visibility.Collapsed;
    public string Parent => Ticket is { ParentId: > 0 } t ? $"#{t.ParentId} {t.ParentTitle}".Trim() : "";
    public Visibility ParentVisibility => Ticket is { ParentId: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public string Description => TicketFeedView.PlainText(Ticket?.Description);
    public Visibility DescriptionVisibility => Description.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoDescriptionVisibility => Description.Length > 0 ? Visibility.Collapsed : Visibility.Visible;

    public Microsoft.UI.Xaml.Media.Brush StatusBrush => TicketBrushes.Tone(TicketTones.Status(Status));
    public Microsoft.UI.Xaml.Media.Brush ResponsibleForeground => TicketBrushes.Text(
        string.IsNullOrWhiteSpace(Ticket?.ResponsibleFullName) ? "TextFillColorTertiaryBrush" : "TextFillColorPrimaryBrush");

    [ObservableProperty]
    public partial TicketFeedFilter FeedFilter { get; set; } = TicketFeedFilter.Comments;

    partial void OnFeedFilterChanged(TicketFeedFilter value) => ShowFeed();

    [ObservableProperty]
    public partial IReadOnlyList<TicketFeedItem> Feed { get; private set; } = Array.Empty<TicketFeedItem>();

    [ObservableProperty]
    public partial string FeedHeader { get; private set; } = "Activity";

    [ObservableProperty]
    public partial bool IsLoadingFeed { get; private set; }

    /// <summary>Show <paramref name="ticket"/> at once from the list, then read it and its activity fresh.</summary>
    public async Task OpenAsync(TicketReader reader, TdxTicket ticket, Action<TdxTicket>? fresh = null)
    {
        var load = _loads.Begin(ticket.Id);
        _reader = reader;
        SwitchTo(ticket);
        Ticket = ticket;
        _feed = new();
        ShowFeed();
        FeedHeader = "Loading activity…";
        IsLoadingFeed = true;
        try
        {
            var latest = await reader.Ticket(ticket.Id);
            if (!_loads.IsCurrent(load)) return;
            if (latest != null)
            {
                Ticket = latest;
                fresh?.Invoke(latest);
            }
            Followed(Ticket!);
            var feed = await reader.Feed(ticket.Id);
            if (!_loads.IsCurrent(load)) return;
            _feed = feed;
            ShowFeed();
        }
        catch (Exception ex)
        {
            if (!_loads.IsCurrent(load)) return;
            FeedHeader = "Activity unavailable — refresh to retry";
            LoadFailed?.Invoke($"Error loading details: {ex.Message}");
        }
        finally
        {
            if (_loads.IsCurrent(load)) IsLoadingFeed = false;
        }
    }

    /// <summary>A detail read failed; the page says so.</summary>
    public event Action<string>? LoadFailed;

    /// <summary>Close the ticket; a read still in flight for it no longer paints.</summary>
    public void Close()
    {
        SwitchTo(null);
        _loads.Invalidate();
        Ticket = null;
        _feed = new();
        IsLoadingFeed = false;
        ShowFeed();
    }

    private void ShowFeed()
    {
        var shown = TicketFeedView.Filter(_feed, FeedFilter);
        FeedHeader = TicketFeedView.Header(shown.Count);
        var items = shown.Take(TicketFeedView.MaxShown).Select(e => new TicketFeedItem(e)).ToList();
        foreach (var reply in items.SelectMany(i => i.Replies)) reply.CanDelete = CanDelete(reply);
        Feed = items;
    }
}

/// <summary>Where the detail pane reads a ticket and its activity: TeamDynamix, or the demo fixtures.</summary>
public sealed record TicketReader(Func<int, Task<TdxTicket?>> Ticket, Func<int, Task<List<TdxFeedEntry>>> Feed)
{
    public static TicketReader For(TdxService service) => new(service.GetTicketAsync, id => service.GetTicketFeedAsync(id));

    public static TicketReader Demo { get; } = new(
        id => Task.FromResult(DemoTickets.Tickets.FirstOrDefault(t => t.Id == id)),
        id => Task.FromResult(DemoTickets.Tickets.FirstOrDefault(t => t.Id == id) is { } t
            ? DemoTickets.FeedFor(t).ToList()
            : new List<TdxFeedEntry>()));
}

/// <summary>One activity entry, with its replies under it.</summary>
public sealed class TicketFeedItem
{
    public TicketFeedItem(TdxFeedEntry entry, bool isReply = false)
    {
        Entry = entry;
        IsReply = isReply;
        Replies = isReply ? Array.Empty<TicketFeedItem>() : entry.ReplyList.Select(r => new TicketFeedItem(r, isReply: true) { Thread = this }).ToList();
    }

    /// <summary>The comment a reply belongs to; null for a comment.</summary>
    public TicketFeedItem? Thread { get; private init; }

    /// <summary>The signed-in person's own reply, which they may delete.</summary>
    public bool CanDelete { get; set; }

    public Visibility DeleteVisibility => CanDelete ? Visibility.Visible : Visibility.Collapsed;

    public TdxFeedEntry Entry { get; }
    public bool IsReply { get; }
    public IReadOnlyList<TicketFeedItem> Replies { get; }
    public string Author => Entry.CreatedFullName ?? "Unknown";
    public string Initials => string.Concat(Author.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => p[0]));
    public string When => TicketFeedView.FormatDate(Entry.CreatedDate);
    public string Body => TicketFeedView.PlainText(Entry.Body);
    public Visibility BodyVisibility => Body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Notified => TicketFeedView.NotifiedLine(Entry.NotifiedList);
    public Visibility NotifiedVisibility => Notified.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PrivateVisibility => Entry.IsPrivate ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RepliesVisibility => Replies.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Thickness Indent => IsReply ? new Thickness(40, 0, 0, 0) : new Thickness(0);

    public string SpokenName => $"{(IsReply ? "Reply" : "Comment")} by {Author}, {When}{(Entry.IsPrivate ? ", private" : "")}. {Body}";
}
