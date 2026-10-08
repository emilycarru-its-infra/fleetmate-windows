using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Services.Tickets;
using Microsoft.Win32;

namespace FleetMate.GUI.Views.Tickets;

/// <summary>
/// Create a TDX ticket, the same fields FleetMate for Mac offers: title,
/// description, classification, type, status, priority, requestor and
/// responsible, with the notification choices. Files picked here are attached
/// once the ticket exists. Source, account and group come from the TDX
/// defaults in settings.
/// </summary>
public partial class CreateTicketDialog : Window
{
    private readonly TdxService _tdx;
    private readonly List<string> _files = new();
    private TdxPerson? _requestor;
    private TdxPerson? _responsible;
    private CancellationTokenSource? _searchCts;

    public TdxTicket? CreatedTicket { get; private set; }

    /// <summary>Files that did not attach; the ticket exists regardless.</summary>
    public List<string> FailedAttachments { get; } = new();

    public CreateTicketDialog(TdxService tdx, TdxPerson? me)
    {
        InitializeComponent();
        _tdx = tdx;
        ClassificationCombo.ItemsSource = TdxClassification.All;
        ClassificationCombo.SelectedValue = TdxClassification.ServiceRequest;
        SetPerson("Requestor", me);
        SetPerson("Responsible", me);
        Loaded += async (_, _) =>
        {
            TitleBox.Focus();
            await LoadReferenceDataAsync();
        };
    }

    private async Task LoadReferenceDataAsync()
    {
        StatusText.Text = "Loading TeamDynamix choices…";
        var types = await _tdx.GetTypesAsync();
        var statuses = await _tdx.GetStatusesAsync();
        var priorities = await _tdx.GetPrioritiesAsync();
        TypeCombo.ItemsSource = types.OrderBy(t => t.Value).ToList();
        StatusCombo.ItemsSource = statuses.OrderBy(s => s.Value).ToList();
        PriorityCombo.ItemsSource = priorities.OrderBy(p => p.Value).ToList();
        if (types.Count == 1) TypeCombo.SelectedIndex = 0;
        StatusText.Text = types.Count == 0
            ? "TeamDynamix sent no ticket types. Settings' default type is used."
            : "";
    }

    private async void OnPersonSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox box) return;
        var role = (string)box.Tag;
        var results = role == "Requestor" ? RequestorResults : ResponsibleResults;
        var text = box.Text.Trim();

        _searchCts?.Cancel();
        if (text.Length < 2)
        {
            results.Visibility = Visibility.Collapsed;
            return;
        }

        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token);
            var people = await _tdx.SearchPeopleAsync(text, 10);
            if (cts.IsCancellationRequested) return;
            results.ItemsSource = people;
            results.Visibility = people.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (TaskCanceledException)
        {
        }
    }

    private void OnPersonPicked(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list || list.SelectedItem is not TdxPerson person) return;
        SetPerson((string)list.Tag, person);
        list.Visibility = Visibility.Collapsed;
    }

    private void SetPerson(string role, TdxPerson? person)
    {
        var label = person == null
            ? "Nobody picked"
            : string.IsNullOrWhiteSpace(person.PrimaryEmail) ? person.DisplayName : $"{person.DisplayName} · {person.PrimaryEmail}";
        if (role == "Requestor")
        {
            _requestor = person;
            RequestorPicked.Text = label;
        }
        else
        {
            _responsible = person;
            ResponsiblePicked.Text = label;
        }
    }

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Multiselect = true, Title = "Attach files" };
        if (picker.ShowDialog(this) != true) return;
        foreach (var file in picker.FileNames.Where(f => !_files.Contains(f)))
            _files.Add(file);
        RefreshAttachments();
    }

    private void OnAttachmentKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || AttachmentList.SelectedIndex < 0) return;
        _files.RemoveAt(AttachmentList.SelectedIndex);
        RefreshAttachments();
    }

    private void RefreshAttachments() =>
        AttachmentList.ItemsSource = _files.Select(Path.GetFileName).ToList();

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        if (title.Length == 0)
        {
            StatusText.Text = "A title is required.";
            return;
        }
        if (_requestor?.Uid is not { } requestorUid)
        {
            StatusText.Text = "Pick a requestor.";
            return;
        }

        CreateButton.IsEnabled = false;
        StatusText.Text = "Creating…";

        var description = DescriptionBox.Text.Trim();
        var request = new CreateTicketRequest
        {
            Title = title,
            Description = description.Length == 0 ? null : HtmlParagraphs(description),
            IsRichHtml = description.Length == 0 ? null : true,
            Classification = ClassificationCombo.SelectedValue as int?,
            TypeId = TypeCombo.SelectedValue as int? ?? 0,
            StatusId = StatusCombo.SelectedValue as int?,
            PriorityId = PriorityCombo.SelectedValue as int?,
            RequestorUid = requestorUid,
            ResponsibleUid = _responsible?.Uid,
        };

        var created = await _tdx.CreateTicketAsync(
            request,
            notifyRequestor: NotifyRequestorBox.IsChecked == true,
            notifyResponsible: NotifyResponsibleBox.IsChecked == true && _responsible != null);
        if (created == null)
        {
            StatusText.Text = "TeamDynamix did not create the ticket. See the log for its answer.";
            CreateButton.IsEnabled = true;
            return;
        }

        foreach (var file in _files)
        {
            StatusText.Text = $"Attaching {Path.GetFileName(file)}…";
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(file);
            }
            catch (IOException)
            {
                FailedAttachments.Add(Path.GetFileName(file));
                continue;
            }
            if (!await _tdx.UploadAttachmentAsync(created.Id, Path.GetFileName(file), bytes))
                FailedAttachments.Add(Path.GetFileName(file));
        }

        CreatedTicket = created;
        DialogResult = true;
        Close();
    }

    /// <summary>TDX stores descriptions as HTML; plain newlines collapse without this.</summary>
    internal static string HtmlParagraphs(string text) =>
        string.Concat(text.Replace("\r\n", "\n").Split('\n')
            .Select(line => $"<p>{(line.Length == 0 ? "&nbsp;" : WebUtility.HtmlEncode(line))}</p>"));
}
