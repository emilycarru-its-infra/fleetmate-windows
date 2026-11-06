using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FleetMate.Core.Models.Tickets;

namespace FleetMate.GUI.Views.Tickets;

// Threaded replies in the activity feed, as FleetMate for Mac has them since
// TeamDynamix 12.3 added POST /api/feed/{id}/replies: the arrow on any card
// opens a composer at the foot of that comment's thread, and the card's menu
// offers Reply in Thread, Quote in New Comment and Copy.
public partial class TicketsPage
{
    /// <summary>Unsent replies, by the comment they answer. Feed entry ids are unique across tickets.</summary>
    private readonly Dictionary<int, string> _replyDrafts = new();

    /// <summary>The comment whose thread has the composer open, if any.</summary>
    private int? _replyingToEntryId;

    /// <summary>The feed items on screen, top-level comments only; replies hang off them.</summary>
    private IEnumerable<FeedDisplayItem> ShownThreads =>
        FeedItemsControl.ItemsSource as IEnumerable<FeedDisplayItem> ?? Enumerable.Empty<FeedDisplayItem>();

    /// <summary>A feed entry and its loaded replies, ready to draw.</summary>
    private FeedDisplayItem ToDisplayItem(TdxFeedEntry entry)
    {
        var item = Display(entry, threadId: entry.Id);
        item.Replies = entry.ReplyList.Select(reply => Display(reply, threadId: entry.Id)).ToList();
        item.ReplyDraft = _replyDrafts.GetValueOrDefault(entry.Id) ?? "";
        item.IsComposing = _replyingToEntryId == entry.Id;
        item.ReplyDraftChanged = OnReplyDraftChanged;
        return item;

        static FeedDisplayItem Display(TdxFeedEntry e, int threadId) => new()
        {
            Id = e.Id,
            ThreadId = threadId,
            CreatedFullName = e.CreatedFullName ?? "Unknown",
            FormattedDate = FormatDate(e.CreatedDate),
            StrippedBody = DecodeHtml(e.Body ?? ""),
            IsPrivate = e.IsPrivate,
            // Only when TDX says who was notified. Replies and most system
            // entries carry no list, and an empty one says nothing.
            NotificationRecipients = FeedDisplayItem.NotifiedLine(e.NotifiedList),
        };
    }

    private void OnReplyDraftChanged(FeedDisplayItem thread) =>
        FeedReplyDrafts.Keep(_replyDrafts, thread.Id, thread.ReplyDraft);

    /// <summary>The reply arrow or Reply in Thread: open the composer in that comment's thread.</summary>
    private void OnReplyInThreadClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FeedDisplayItem item }) return;
        // TDX threads are one level deep, so a reply to a reply joins its parent's thread.
        OpenReplyComposer(item.ThreadId);
    }

    private void OpenReplyComposer(int threadId)
    {
        _replyingToEntryId = threadId;
        FeedDisplayItem? opened = null;
        foreach (var thread in ShownThreads)
        {
            thread.IsComposing = thread.Id == threadId;
            if (thread.IsComposing) opened = thread;
        }
        if (opened == null) return;

        // Put the cursor in the composer once it has been laid out.
        Dispatcher.BeginInvoke(() =>
        {
            if (FeedItemsControl.ItemContainerGenerator.ContainerFromItem(opened) is not DependencyObject container) return;
            if (FindReplyBox(container, opened) is not { } box) return;
            Keyboard.Focus(box);
            box.CaretIndex = box.Text.Length;
            box.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private static TextBox? FindReplyBox(DependencyObject node, FeedDisplayItem thread)
    {
        if (node is TextBox { Tag: FeedDisplayItem tagged } box && ReferenceEquals(tagged, thread)) return box;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (FindReplyBox(VisualTreeHelper.GetChild(node, i), thread) is { } found) return found;
        }
        return null;
    }

    private void CloseReplyComposer(int threadId)
    {
        if (_replyingToEntryId == threadId) _replyingToEntryId = null;
        if (ShownThreads.FirstOrDefault(t => t.Id == threadId) is { } thread) thread.IsComposing = false;
    }

    private void OnCancelReplyClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FeedDisplayItem thread }) CloseReplyComposer(thread.Id);
    }

    private async void OnReplyKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: FeedDisplayItem thread }) return;
        if (e.Key == Key.Escape)
        {
            // Closes the composer and keeps the draft, as Cancel does.
            e.Handled = true;
            CloseReplyComposer(thread.Id);
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await PostReplyAsync(thread);
        }
    }

    private async void OnPostReplyClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FeedDisplayItem thread }) await PostReplyAsync(thread);
    }

    /// <summary>
    /// Post the thread's reply, then re-read only that thread, so the rest of
    /// the feed stays put. The draft is kept when TeamDynamix refuses it; text
    /// typed while the request ran is kept too.
    /// </summary>
    private async Task PostReplyAsync(FeedDisplayItem thread)
    {
        if (_tdxService == null || _selectedTicket is not { } ticket || !thread.CanPostReply) return;
        var ticketId = ticket.Id;
        var submitted = thread.ReplyDraft;

        thread.IsPostingReply = true;
        try
        {
            ShowActionMessage("Posting reply…", isLoading: true);
            await _tdxService.ReplyToFeedEntryAsync(thread.Id, submitted);
        }
        catch (TdxFeedReplyException ex)
        {
            ShowActionMessage($"Reply failed: {ex.Message}", isError: true);
            return;
        }
        finally
        {
            thread.IsPostingReply = false;
        }

        FeedReplyDrafts.Posted(_replyDrafts, thread.Id, submitted);
        if (!_replyDrafts.ContainsKey(thread.Id))
        {
            thread.ReplyDraft = "";
            CloseReplyComposer(thread.Id);
        }
        ShowActionMessage("Reply posted");
        await RefreshThreadAsync(ticketId, thread.Id);
    }

    /// <summary>Re-read one thread; the single-entry read carries its replies.</summary>
    private async Task RefreshThreadAsync(int ticketId, int threadId)
    {
        if (_tdxService == null) return;
        var fresh = await _tdxService.GetFeedEntryAsync(threadId);
        if (fresh == null || _selectedTicket?.Id != ticketId) return;
        var index = _ticketFeed.FindIndex(f => f.Id == threadId);
        if (index < 0) return;
        _ticketFeed[index] = fresh;
        UpdateFeedPanel();
    }

    private void OnCopyFeedEntryClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FeedDisplayItem item }) return;
        CopyToClipboard($"{item.CreatedFullName} ({item.FormattedDate}):\n{item.StrippedBody}", "Comment copied");
    }
}

/// <summary>One comment or reply in the activity feed, as drawn.</summary>
public class FeedDisplayItem : INotifyPropertyChanged
{
    public int Id { get; set; }

    /// <summary>The top-level comment whose thread this belongs to: its own id, or its parent's for a reply.</summary>
    public int ThreadId { get; set; }

    public bool IsReply => ThreadId != Id;

    public string CreatedFullName { get; set; } = "";
    public string FormattedDate { get; set; } = "";
    public string StrippedBody { get; set; } = "";
    public bool IsPrivate { get; set; }
    public string NotificationRecipients { get; set; } = "";

    public List<FeedDisplayItem> Replies { get; set; } = new();

    /// <summary>"Notified: …", or nothing when TDX sent no names.</summary>
    public static string NotifiedLine(string? notifiedList) =>
        string.IsNullOrWhiteSpace(notifiedList) ? "" : $"Notified: {System.Net.WebUtility.HtmlDecode(notifiedList.Trim())}";

    public Visibility NotificationVisibility =>
        string.IsNullOrEmpty(NotificationRecipients) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility PrivateVisibility => IsPrivate ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>What a screen reader hears for the card.</summary>
    public string AccessibleName =>
        $"{(IsReply ? "Reply" : "Comment")} from {CreatedFullName}, {FormattedDate}{(IsPrivate ? ", private" : "")}";

    // ── Reply composer (top-level comments only) ──────────────────

    private bool _isComposing;
    private bool _isPostingReply;
    private string _replyDraft = "";

    /// <summary>Called when the reply text changes, to keep the draft per comment.</summary>
    public Action<FeedDisplayItem>? ReplyDraftChanged { get; set; }

    public bool IsComposing
    {
        get => _isComposing;
        set
        {
            if (_isComposing == value) return;
            _isComposing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ComposerVisibility));
            OnPropertyChanged(nameof(ThreadVisibility));
        }
    }

    public string ReplyDraft
    {
        get => _replyDraft;
        set
        {
            value ??= "";
            if (_replyDraft == value) return;
            _replyDraft = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanPostReply));
            ReplyDraftChanged?.Invoke(this);
        }
    }

    public bool IsPostingReply
    {
        get => _isPostingReply;
        set
        {
            if (_isPostingReply == value) return;
            _isPostingReply = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanPostReply));
            OnPropertyChanged(nameof(PostingVisibility));
        }
    }

    public bool CanPostReply => !IsPostingReply && !string.IsNullOrWhiteSpace(ReplyDraft);

    public Visibility ComposerVisibility => IsComposing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PostingVisibility => IsPostingReply ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ThreadVisibility => IsComposing || Replies.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string ReplyLabel => string.IsNullOrWhiteSpace(CreatedFullName) ? "Reply" : $"Reply to {CreatedFullName}";

    /// <summary>The Mac's note under the composer: what a reply can and cannot do.</summary>
    public string ReplyHint => IsPrivate
        ? "Joins this private comment's thread. Replies can't notify anyone or set their own visibility; use Quote in New Comment for that."
        : "Joins this comment's thread. Replies can't notify anyone or set their own visibility; use Quote in New Comment for that.";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
