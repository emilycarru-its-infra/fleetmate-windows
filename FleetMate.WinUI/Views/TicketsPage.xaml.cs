using System.Net;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FleetMate.Core.Models.Tickets;
using FleetMate.WinUI.ViewModels;

namespace FleetMate.WinUI.Views;

public sealed partial class TicketsPage : Page
{
    private List<TicketRowViewModel> _all = new();
    private TdxTicket? _currentTicket;

    public TicketsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        var tdx = App.Current.TdxService;
        if (tdx == null)
        {
            CountText.Text = "TeamDynamix is not configured — add credentials in Settings.";
            _all = new();
            ApplyFilter();
            return;
        }

        // Show a sign-in affordance when TDX uses browser SSO and isn't authenticated yet.
        SignInButton.Visibility = tdx.RequiresSsoLogin ? Visibility.Visible : Visibility.Collapsed;

        RefreshButton.IsEnabled = false;
        LoadingRing.IsActive = true;
        TicketList.ItemsSource = null;
        try
        {
            var tickets = await tdx.SearchTicketsAsync(new TicketSearchRequest { MaxResults = 500 }, 500);
            _all = tickets.Select(t => new TicketRowViewModel(t)).ToList();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            CountText.Text = $"Failed to load tickets: {ex.Message}";
            _all = new();
            ApplyFilter();
        }
        finally
        {
            LoadingRing.IsActive = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text?.Trim() ?? "";
        var rows = string.IsNullOrEmpty(q) ? _all : _all.Where(r => r.Matches(q)).ToList();
        rows = rows.OrderByDescending(r => r.Ticket.ModifiedDate ?? r.Ticket.CreatedDate).ToList();
        TicketList.ItemsSource = rows;
        CountText.Text = _all.Count == 0 ? "No tickets."
            : rows.Count == _all.Count ? $"{_all.Count} tickets"
            : $"{rows.Count} of {_all.Count} tickets";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        var tdx = App.Current.TdxService;
        var baseUrl = App.Current.Config.Tdx?.BaseUrl;
        if (tdx == null || string.IsNullOrEmpty(baseUrl)) return;

        SignInButton.IsEnabled = false;
        try
        {
            var window = new TdxSsoLoginWindow(baseUrl);
            var result = await window.ShowAndAuthenticateAsync();
            if (result is { Success: true, Token: not null })
            {
                tdx.SetSsoToken(result.Token, result.Expiry, result.UserEmail, result.UserName);
                SignInButton.Visibility = Visibility.Collapsed;
                await LoadAsync();
            }
        }
        finally
        {
            SignInButton.IsEnabled = true;
        }
    }

    private async void TicketList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TicketList.SelectedItem is not TicketRowViewModel row)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            EmptyDetail.Visibility = Visibility.Visible;
            _currentTicket = null;
            return;
        }

        var t = row.Ticket;
        _currentTicket = t;
        EmptyDetail.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        DetailId.Text = $"{row.IdText}  ·  {t.TypeName ?? "Ticket"}";
        DetailTitle.Text = row.Title;

        DetailRows.Children.Clear();
        AddRow("Status", row.Status);
        AddRow("Priority", row.Priority);
        AddRow("Classification", t.ClassificationName ?? "—");
        AddRow("Account", t.AccountName ?? "—");
        AddRow("Requestor", Contact(t.RequestorName, t.RequestorEmail));
        AddRow("Responsible", Contact(row.Responsible == "—" ? null : row.Responsible, t.ResponsibleEmail));
        AddRow("Group", t.ResponsibleGroupName ?? "—");
        AddRow("SLA", t.SlaName ?? "—");
        AddRow("SLA violated", t.IsSlaViolated ? "Yes" : "No");
        AddRow("Created", t.CreatedDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        AddRow("Modified", row.Modified);

        DetailDescription.Text = TextUtil.StripHtml(t.Description);

        await LoadFeedAsync(t.Id);
    }

    private async Task LoadFeedAsync(int ticketId)
    {
        FeedList.ItemsSource = null;
        FeedEmpty.Visibility = Visibility.Collapsed;
        var tdx = App.Current.TdxService;
        if (tdx == null) return;

        FeedRing.IsActive = true;
        try
        {
            var feed = await tdx.GetTicketFeedAsync(ticketId);
            if (_currentTicket?.Id != ticketId) return; // stale
            if (feed.Count == 0) { FeedEmpty.Visibility = Visibility.Visible; return; }
            FeedList.ItemsSource = feed
                .OrderByDescending(f => f.CreatedDate)
                .Select(f => new FeedItem(
                    f.CreatedFullName ?? "(system)",
                    f.CreatedDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    TextUtil.StripHtml(f.Body),
                    f.IsPrivate))
                .ToList();
        }
        catch (Exception ex)
        {
            FeedEmpty.Text = $"Failed to load activity: {ex.Message}";
            FeedEmpty.Visibility = Visibility.Visible;
        }
        finally { FeedRing.IsActive = false; }
    }

    private async void AddComment_Click(object sender, RoutedEventArgs e)
    {
        var tdx = App.Current.TdxService;
        if (_currentTicket is not { } t || tdx == null) return;

        var box = new TextBox { PlaceholderText = "Comment…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100 };
        var isPrivate = new CheckBox { Content = "Private" };
        var notify = new CheckBox { Content = "Notify requestor & responsible" };
        var dialog = new ContentDialog
        {
            Title = $"Comment on #{t.Id}",
            Content = new StackPanel { Spacing = 10, Width = 380, Children = { box, isPrivate, notify } },
            PrimaryButtonText = "Post",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(box.Text)) return;

        List<Guid>? notifyUids = null;
        if (notify.IsChecked == true)
        {
            notifyUids = new[] { t.RequestorUid, t.ResponsibleUid }
                .Where(g => g is { } gg && gg != Guid.Empty).Select(g => g!.Value).ToList();
            if (notifyUids.Count == 0) notifyUids = null;
        }

        AddCommentButton.IsEnabled = false;
        try
        {
            var ok = await tdx.AddCommentAsync(t.Id, box.Text.Trim(), isPrivate.IsChecked == true, notifyUids);
            if (ok) await LoadFeedAsync(t.Id);
            else await MessageAsync("Comment failed", "Could not post the comment.");
        }
        finally { AddCommentButton.IsEnabled = true; }
    }

    private async Task MessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private sealed record FeedItem(string Author, string When, string Body, bool IsPrivate)
    {
        public Visibility PrivateVisibility => IsPrivate ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddRow(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var l = new TextBlock { Text = label, FontSize = 12, Opacity = 0.65 };
        l.SetValue(Grid.ColumnProperty, 0);

        var v = new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        v.SetValue(Grid.ColumnProperty, 1);

        grid.Children.Add(l);
        grid.Children.Add(v);
        DetailRows.Children.Add(grid);
    }

    private static string Contact(string? name, string? email) =>
        string.IsNullOrEmpty(name) ? "—"
        : string.IsNullOrEmpty(email) ? name
        : $"{name} ({email})";
}
