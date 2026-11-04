using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using FleetMate.Core.Models.Tickets;
using Microsoft.Win32;
using Serilog;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>
/// Files attached from the comment composer: the Attach… button, files
/// dropped on the composer, and Ctrl+V of copied files or of an image with no
/// text. TDX cannot link a file to one feed entry, so on Post every file is
/// uploaded to the ticket first, landing in its Attachments list as the web UI
/// does, and the comment is posted only once all of them have landed.
/// </summary>
public partial class TicketsPage
{
    /// <summary>Files waiting to go up, per ticket, kept with the draft.</summary>
    private readonly Dictionary<int, ObservableCollection<PendingCommentAttachment>> _attachmentDrafts = new();

    private ObservableCollection<PendingCommentAttachment>? _composeAttachments;

    private ObservableCollection<PendingCommentAttachment> ComposeAttachments
    {
        get
        {
            if (_composeAttachments != null) return _composeAttachments;
            var list = new ObservableCollection<PendingCommentAttachment>();
            if (_selectedTicket is { } ticket) _attachmentDrafts[ticket.Id] = list;
            BindComposeAttachments(list);
            return list;
        }
    }

    private bool HasComposeAttachments => _composeAttachments is { Count: > 0 };

    /// <summary>Show <paramref name="ticketId"/>'s pending files in the composer.</summary>
    private void RestoreAttachmentDraft(int ticketId) =>
        BindComposeAttachments(_attachmentDrafts.GetValueOrDefault(ticketId));

    private void BindComposeAttachments(ObservableCollection<PendingCommentAttachment>? list)
    {
        if (_composeAttachments != null) _composeAttachments.CollectionChanged -= OnComposeAttachmentsChanged;
        _composeAttachments = list;
        if (list != null) list.CollectionChanged += OnComposeAttachmentsChanged;
        CommentAttachmentsList.ItemsSource = list;
        OnComposeAttachmentsChanged(this, EventArgs.Empty);
    }

    private void OnComposeAttachmentsChanged(object? sender, EventArgs e)
    {
        CommentAttachmentsList.Visibility = HasComposeAttachments ? Visibility.Visible : Visibility.Collapsed;
        UpdateAddCommentButton();
    }

    private void AttachFiles(IEnumerable<string> paths)
    {
        if (_selectedTicket == null) return;
        var list = ComposeAttachments;
        var draft = list.ToList();
        if (CommentAttachments.Add(draft, paths) == 0) return;
        foreach (var file in draft.Skip(list.Count)) list.Add(file);
        if (!_composeOpen) SetComposeOpen(true);
    }

    private void OnAttachFilesClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Attach files to the ticket",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) AttachFiles(dialog.FileNames);
    }

    private void OnRemovePendingAttachmentClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PendingCommentAttachment file }) _composeAttachments?.Remove(file);
    }

    private void OnShowPendingAttachmentClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PendingCommentAttachment file }) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + file.Path } });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tickets] Could not show {Name} in File Explorer", file.Name);
        }
    }

    // ── Drag and drop ─────────────────────────────────────────────
    // The text box handles its own drops, so file drops are taken in the
    // preview pass; text drops still reach it.

    private void OnComposeDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = _selectedTicket != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnComposeDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        e.Handled = true;
        AttachFiles(paths);
    }

    // ── Paste ─────────────────────────────────────────────────────
    // Ctrl+V of a File Explorer copy attaches the files. An image with no text
    // (a screenshot) is saved as a PNG and attached. Text pastes as text.

    private void OnCommentPreviewCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || !ClipboardHasAttachable()) return;
        e.CanExecute = true;
        e.Handled = true;
    }

    private void OnCommentPreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste) return;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                e.Handled = true;
                AttachFiles(Clipboard.GetFileDropList().Cast<string>());
            }
            else if (Clipboard.ContainsImage() && !Clipboard.ContainsText() && Clipboard.GetImage() is { } image)
            {
                e.Handled = true;
                if (SavePastedImage(image) is { } path) AttachFiles(new[] { path });
            }
        }
        catch (Exception ex)
        {
            // The clipboard is shared with every app and can be locked or hold
            // data that will not decode; a failed paste must not lose the draft.
            Log.Warning(ex, "[tickets] Could not paste into the comment");
            ShowActionMessage($"Could not paste: {ex.Message}", isError: true);
        }
    }

    private static bool ClipboardHasAttachable()
    {
        try
        {
            return Clipboard.ContainsFileDropList() || (Clipboard.ContainsImage() && !Clipboard.ContainsText());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Write a pasted image to its own temporary folder, named like a screenshot.</summary>
    private static string? SavePastedImage(BitmapSource image)
    {
        var folder = Path.Combine(Path.GetTempPath(), "FleetMate-Pasted", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, CommentAttachments.PastedImageName(DateTime.Now));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(path)) encoder.Save(stream);
        return path;
    }

    // ── Upload ────────────────────────────────────────────────────

    /// <summary>
    /// Upload the composer's files to <paramref name="ticketId"/>. Files that
    /// landed leave the draft; the rest stay, so Post retries only those.
    /// Returns false, after saying why, when any file did not land.
    /// </summary>
    private async Task<bool> UploadComposeAttachmentsAsync(int ticketId, bool isPrivate, bool hasText)
    {
        if (_tdxService == null || _composeAttachments is not { Count: > 0 } list) return true;

        var files = list.ToList();
        ShowActionMessage(files.Count == 1 ? $"Uploading {files[0].Name}…" : $"Uploading {files.Count} files…", isLoading: true);
        var tdx = _tdxService;
        var failed = await CommentAttachments.UploadAllAsync(files, async file =>
        {
            var bytes = await File.ReadAllBytesAsync(file.Path);
            await tdx.UploadAttachmentOrThrowAsync(ticketId, file.Name, bytes, isPrivate: isPrivate);
        });

        foreach (var file in files.Where(f => failed.All(x => x.File != f))) list.Remove(file);
        if (files.Count > failed.Count) await RefreshAttachmentsAsync(ticketId);

        if (failed.Count == 0) return true;
        ShowActionMessage(CommentAttachments.FailureMessage(failed, hasText), isError: true);
        return false;
    }

    /// <summary>Re-read the ticket so its Attachments list shows what was just uploaded.</summary>
    private async Task RefreshAttachmentsAsync(int ticketId)
    {
        if (_tdxService == null) return;
        try
        {
            var fresh = await _tdxService.GetTicketAsync(ticketId);
            if (fresh != null && _selectedTicket?.Id == ticketId) ShowAttachments(fresh);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tickets] Could not refresh attachments for {Id}", ticketId);
        }
    }
}
