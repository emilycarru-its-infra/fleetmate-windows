using System.Diagnostics;
using System.IO;
using System.Windows;
using FleetMate.Core.Models.Tickets;
using Microsoft.Win32;
using Serilog;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>One attachment row in the ticket detail pane.</summary>
public sealed class TicketAttachmentRow
{
    public required TdxAttachment Attachment { get; init; }

    public string Name => TicketAttachments.DisplayName(Attachment);
    public string Subtitle => TicketAttachments.Subtitle(Attachment);
    public Visibility OpenVisibility => TicketAttachments.CanOpen(Attachment) ? Visibility.Visible : Visibility.Collapsed;
    public string OpenTooltip => $"Open {Name}";
    public string SaveTooltip => $"Save {Name}";
}

/// <summary>
/// Ticket detail › Attachments, as on macOS: each file can be saved, and the
/// ones that only display (images, documents, text) can be opened directly.
/// Files that could run are save-only, because an attachment comes from
/// whoever filed the ticket.
/// </summary>
public partial class TicketsPage
{
    private bool _attachmentBusy;

    private void ShowAttachments(TdxTicket ticket)
    {
        var rows = (ticket.Attachments ?? new List<TdxAttachment>())
            .OrderByDescending(a => a.CreatedDate)
            .Select(a => new TicketAttachmentRow { Attachment = a })
            .ToList();

        AttachmentsList.ItemsSource = rows;
        AttachmentsHeaderText.Text = rows.Count == 1 ? "Attachments · 1 file" : $"Attachments · {rows.Count} files";
        AttachmentsSection.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnOpenAttachmentClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TicketAttachmentRow row }) return;
        if (!TicketAttachments.CanOpen(row.Attachment)) return;

        var path = await StageAsync(row);
        if (path == null) return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tickets] Could not open attachment {Name}", row.Name);
            ShowActionMessage($"Could not open {row.Name}: {ex.Message}", isError: true);
        }
    }

    private async void OnSaveAttachmentClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TicketAttachmentRow row }) return;

        var dialog = new SaveFileDialog
        {
            FileName = TicketAttachments.SafeFileName(row.Attachment),
            Title = $"Save {row.Name}",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var path = await StageAsync(row);
        if (path == null) return;

        try
        {
            File.Copy(path, dialog.FileName, overwrite: true);
            ShowActionMessage($"Saved {row.Name}", isError: false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tickets] Could not save attachment {Name}", row.Name);
            ShowActionMessage($"Could not save {row.Name}: {ex.Message}", isError: true);
        }
    }

    /// <summary>Download once into a temporary file; null after telling the operator why.</summary>
    private async Task<string?> StageAsync(TicketAttachmentRow row)
    {
        if (_tdxService == null || _attachmentBusy) return null;
        _attachmentBusy = true;
        try
        {
            var path = await _tdxService.StageAttachmentAsync(row.Attachment);
            if (path == null) ShowActionMessage($"Could not download {row.Name}.", isError: true);
            return path;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tickets] Could not download attachment {Name}", row.Name);
            ShowActionMessage($"Could not download {row.Name}: {ex.Message}", isError: true);
            return null;
        }
        finally
        {
            _attachmentBusy = false;
        }
    }
}
