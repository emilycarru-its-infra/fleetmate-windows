using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FleetMate.Core.Models.Tickets;
using Microsoft.Win32;

namespace FleetMate.GUI.Views.Tickets;

// Threaded replies in the activity feed, as FleetMate for Mac has them since
// TeamDynamix 12.3 added POST /api/feed/{id}/replies: the arrow on any card
// opens a composer at the foot of that comment's thread, and the card's menu
// offers Reply in Thread, Quote in New Comment and Copy, and Delete Reply… on
// the signed-in person's own replies. The composer takes files as the comment
// composer does: Attach…, a drop, or a paste.
public partial class TicketsPage
{
    /// <summary>Unsent replies, by the comment they answer. Feed entry ids are unique across tickets.</summary>
    private readonly Dictionary<int, string> _replyDrafts = new();

    /// <summary>Files waiting to go up with a reply, by the comment it answers.</summary>
    private readonly Dictionary<int, ObservableCollection<PendingCommentAttachment>> _replyAttachmentDrafts = new();

    /// <summary>The comment whose thread has the composer open, if any.</summary>
    private int? _replyingToEntryId;

    /// <summary>The feed items on screen, top-level comments only; replies hang off them.</summary>
    private IEnumerable<FeedDisplayItem> ShownThreads =>
        FeedItemsControl.ItemsSource as IEnumerable<FeedDisplayItem> ?? Enumerable.Empty<FeedDisplayItem>();

    /// <summary>A feed entry and its loaded replies, ready to draw.</summary>
    private FeedDisplayItem ToDisplayItem(TdxFeedEntry entry)
    {
        var me = _me?.Uid;
        var item = Display(entry, threadId: entry.Id);
        item.Replies = entry.ReplyList.Select(reply => Display(reply, threadId: entry.Id)).ToList();
        foreach (var reply in item.Replies) reply.CanDelete = TdxFeedReplyRequest.CanDelete(reply.CreatedUid, me);
        item.ReplyDraft = _replyDrafts.GetValueOrDefault(entry.Id) ?? "";
        if (!_replyAttachmentDrafts.TryGetValue(entry.Id, out var files))
            _replyAttachmentDrafts[entry.Id] = files = new ObservableCollection<PendingCommentAttachment>();
        item.ReplyAttachments = files;
        item.IsComposing = _replyingToEntryId == entry.Id;
        item.ReplyDraftChanged = OnReplyDraftChanged;
        return item;

        static FeedDisplayItem Display(TdxFeedEntry e, int threadId) => new()
        {
            Id = e.Id,
            ThreadId = threadId,
            CreatedUid = e.CreatedUid,
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
        if (_backgroundFeedRenderPending) UpdateFeedPanel();
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
    /// the feed stays put. Files go first, as with a comment and as on the Mac:
    /// TDX cannot attach a file to a reply, so they go to the ticket's
    /// Attachments, private when the comment answered is, and the text posts
    /// only once every file has landed. The draft is kept when TeamDynamix
    /// refuses it; text typed while the request ran is kept too.
    /// </summary>
    private async Task PostReplyAsync(FeedDisplayItem thread)
    {
        if (_tdxService == null || _selectedTicket is not { } ticket || !thread.CanPostReply) return;
        var ticketId = ticket.Id;
        var submitted = thread.ReplyDraft;
        var hasText = !string.IsNullOrWhiteSpace(submitted);
        var files = thread.ReplyAttachments.ToList();

        thread.IsPostingReply = true;
        try
        {
            if (files.Count > 0)
            {
                ShowActionMessage(files.Count == 1 ? $"Uploading {files[0].Name}…" : $"Uploading {files.Count} files…", isLoading: true);
                var tdx = _tdxService;
                var failed = await CommentAttachments.UploadAllAsync(files, async file =>
                {
                    var bytes = await File.ReadAllBytesAsync(file.Path);
                    await tdx.UploadAttachmentOrThrowAsync(ticketId, file.Name, bytes, isPrivate: thread.IsPrivate);
                });
                // Files that landed leave the draft, so Post again retries only the rest.
                foreach (var file in files.Where(f => failed.All(x => x.File != f))) thread.ReplyAttachments.Remove(file);
                if (files.Count > failed.Count) await RefreshAttachmentsAsync(ticketId);
                if (failed.Count > 0)
                {
                    ShowActionMessage(CommentAttachments.FailureMessage(failed, hasText, "reply"), isError: true);
                    return;
                }
            }

            if (hasText)
            {
                ShowActionMessage("Posting reply…", isLoading: true);
                await _tdxService.ReplyToFeedEntryAsync(thread.Id, submitted);
            }
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

        if (hasText) FeedReplyDrafts.Posted(_replyDrafts, thread.Id, submitted);
        if (!_replyDrafts.ContainsKey(thread.Id) && thread.ReplyAttachments.Count == 0)
        {
            thread.ReplyDraft = "";
            CloseReplyComposer(thread.Id);
        }
        ShowActionMessage(!hasText
            ? (files.Count == 1 ? "Attached 1 file to the ticket" : $"Attached {files.Count} files to the ticket")
            : files.Count > 0 ? "Reply posted with its files on the ticket" : "Reply posted");
        if (hasText) await RefreshThreadAsync(ticketId, thread.Id);
    }

    // ── Reply attachments ─────────────────────────────────────────

    private void AttachReplyFiles(FeedDisplayItem thread, IEnumerable<string> paths)
    {
        if (_selectedTicket == null) return;
        var draft = thread.ReplyAttachments.ToList();
        if (CommentAttachments.Add(draft, paths) == 0) return;
        foreach (var file in draft.Skip(thread.ReplyAttachments.Count)) thread.ReplyAttachments.Add(file);
    }

    /// <summary>Attach…: pick files to go up with the reply.</summary>
    private void OnAttachReplyFilesClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FeedDisplayItem thread }) return;
        var dialog = new OpenFileDialog
        {
            Title = "Attach files to the ticket with this reply",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) AttachReplyFiles(thread, dialog.FileNames);
        if (FeedItemsControl.ItemContainerGenerator.ContainerFromItem(thread) is DependencyObject container
            && FindReplyBox(container, thread) is { } box)
            Keyboard.Focus(box);
    }

    private void OnRemoveReplyAttachmentClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PendingCommentAttachment file }) return;
        foreach (var thread in ShownThreads)
            if (thread.ReplyAttachments.Remove(file)) return;
    }

    private void OnReplyDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = _selectedTicket != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnReplyDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FeedDisplayItem thread }) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        e.Handled = true;
        AttachReplyFiles(thread, paths);
    }

    private void OnReplyPreviewCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || !ClipboardHasAttachable()) return;
        e.CanExecute = true;
        e.Handled = true;
    }

    /// <summary>Ctrl+V of copied files, or of an image with no text, attaches them, as in the comment composer.</summary>
    private void OnReplyPreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || sender is not FrameworkElement { Tag: FeedDisplayItem thread }) return;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                e.Handled = true;
                AttachReplyFiles(thread, Clipboard.GetFileDropList().Cast<string>());
            }
            else if (Clipboard.ContainsImage() && !Clipboard.ContainsText() && Clipboard.GetImage() is { } image)
            {
                e.Handled = true;
                if (SavePastedImage(image) is { } path) AttachReplyFiles(thread, new[] { path });
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[tickets] Could not paste into the reply");
            ShowActionMessage($"Could not paste: {ex.Message}", isError: true);
        }
    }

    // ── Delete Reply ──────────────────────────────────────────────

    /// <summary>
    /// Delete Reply… on one of the signed-in person's own replies, confirmed
    /// first, as on the Mac: TeamDynamix replaces it with a deletion notice and
    /// it cannot be restored. Only that thread is re-read afterwards.
    /// </summary>
    private async void OnDeleteReplyClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FeedDisplayItem reply } || !reply.IsReply || !reply.CanDelete) return;
        if (_tdxService == null || _selectedTicket is not { } ticket) return;

        var owner = Window.GetWindow(this);
        const string prompt = "Delete this reply?\n\nTeamDynamix replaces the reply with a deletion notice. It can't be restored.";
        var confirm = owner != null
            ? MessageBox.Show(owner, prompt, "Delete Reply", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(prompt, "Delete Reply", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            ShowActionMessage("Deleting reply…", isLoading: true);
            await _tdxService.DeleteFeedReplyAsync(reply.ThreadId, reply.Id);
        }
        catch (TdxFeedReplyException ex)
        {
            ShowActionMessage($"Couldn't delete the reply: {ex.Message}", isError: true);
            return;
        }
        ShowActionMessage("Reply deleted");
        await RefreshThreadAsync(ticket.Id, reply.ThreadId);
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

    /// <summary>Who wrote the entry, to tell the signed-in person's own replies.</summary>
    public Guid? CreatedUid { get; set; }

    /// <summary>True on the signed-in person's own replies, which Delete Reply… is offered on.</summary>
    public bool CanDelete { get; set; }

    public Visibility DeleteReplyVisibility => IsReply && CanDelete ? Visibility.Visible : Visibility.Collapsed;

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
            OnPropertyChanged(nameof(CanAttachReply));
            OnPropertyChanged(nameof(PostingVisibility));
        }
    }

    private ObservableCollection<PendingCommentAttachment> _replyAttachments = new();

    /// <summary>Files waiting to go up with the reply, kept per comment with the draft.</summary>
    public ObservableCollection<PendingCommentAttachment> ReplyAttachments
    {
        get => _replyAttachments;
        set
        {
            if (ReferenceEquals(_replyAttachments, value)) return;
            _replyAttachments.CollectionChanged -= OnReplyAttachmentsChanged;
            _replyAttachments = value ?? new ObservableCollection<PendingCommentAttachment>();
            _replyAttachments.CollectionChanged += OnReplyAttachmentsChanged;
            OnReplyAttachmentsChanged(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    private void OnReplyAttachmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ReplyAttachments));
        OnPropertyChanged(nameof(CanPostReply));
        OnPropertyChanged(nameof(ReplyAttachmentsVisibility));
        OnPropertyChanged(nameof(PostReplyLabel));
        OnPropertyChanged(nameof(PostReplyToolTip));
    }

    /// <summary>Attach… waits while the reply posts, as on the Mac.</summary>
    public bool CanAttachReply => !IsPostingReply;

    public Visibility ReplyAttachmentsVisibility => ReplyAttachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The Mac's label: "Post Reply", or "Post Reply with Attachments" when files go too.</summary>
    public string PostReplyLabel => ReplyAttachments.Count > 0 ? "Post Reply with Attachments" : "Post Reply";

    public string PostReplyToolTip => ReplyAttachments.Count > 0
        ? "Upload the files to the ticket, then post the reply (Ctrl+Enter)"
        : "Post this reply in the thread (Ctrl+Enter)";

    public bool CanPostReply => !IsPostingReply && (!string.IsNullOrWhiteSpace(ReplyDraft) || ReplyAttachments.Count > 0);

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
