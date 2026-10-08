using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FleetMate.Core.Services.Activity;
using Microsoft.Win32;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// What FleetMate asked each service to do, and each HTTP request made for it.
/// Copy and export mask serials, UDIDs and hardware addresses so the result can
/// go into a bug report. Opened with Ctrl+Shift+L or from Recent activity.
/// </summary>
public partial class ActivityLogWindow : Window
{
    private static ActivityLogWindow? _open;

    private readonly DispatcherTimer _coalesce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private IReadOnlyList<ActivityAction> _actions = Array.Empty<ActivityAction>();
    private Guid? _selected;

    /// <summary>Show the one Activity Log window, opening it if needed.</summary>
    public static void ShowSingle(Window? owner)
    {
        if (_open == null)
        {
            _open = new ActivityLogWindow { Owner = owner };
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }
        else
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
        }
    }

    public ActivityLogWindow()
    {
        InitializeComponent();
        _coalesce.Tick += (_, _) => { _coalesce.Stop(); Reload(); };
        ActivityLog.Shared.Changed += OnLogChanged;
        Closed += (_, _) => ActivityLog.Shared.Changed -= OnLogChanged;
        Reload();
    }

    private void OnLogChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => { if (!_coalesce.IsEnabled) _coalesce.Start(); });

    private IEnumerable<ActivityAction> Filtered =>
        ActivityLog.Filter(_actions, SearchBox.Text);

    private void Reload()
    {
        _actions = ActivityLog.Shared.Snapshot();
        var rows = Filtered.Reverse().Select(ActionRow.From).ToList();
        ActionsGrid.ItemsSource = rows;
        var keep = rows.FirstOrDefault(r => r.Id == _selected);
        if (keep != null) ActionsGrid.SelectedItem = keep;
        ShowRequests(keep?.Action);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Reload();

    private void OnActionSelected(object sender, SelectionChangedEventArgs e)
    {
        var row = ActionsGrid.SelectedItem as ActionRow;
        _selected = row?.Id;
        ShowRequests(row?.Action);
    }

    private void ShowRequests(ActivityAction? action)
    {
        RequestsGrid.ItemsSource = action?.Requests.Select(RequestRow.From).ToList();
        RequestsPlaceholder.Text = _actions.Count == 0 ? "No activity yet." : "Select an action to see its requests.";
        RequestsPlaceholder.Visibility = action == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCopyMasked(object sender, RoutedEventArgs e) =>
        Clipboard.SetText(ActivityMasker.Export(Filtered));

    private void OnExportMasked(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = "FleetMate Activity Log.txt",
            InitialDirectory = KnownFolders.Downloads,
            Filter = "Text files (*.txt)|*.txt",
        };
        if (dialog.ShowDialog(this) == true)
            File.WriteAllText(dialog.FileName, ActivityMasker.Export(Filtered));
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        ActivityLog.Shared.Clear();
        _selected = null;
        Reload();
    }

    private sealed record ActionRow(Guid Id, ActivityAction Action, string Time, string Service, string Title,
                                    double TitleOpacity, string Serials, int RequestCount, string Result, bool NeedsAttention)
    {
        public static ActionRow From(ActivityAction a) => new(
            a.Id, a, a.StartedAt.ToLocalTime().ToString("HH:mm:ss"), a.Service, a.Title,
            a.IsBackground ? 0.7 : 1.0, string.Join(", ", a.Serials), a.Requests.Count, a.Result,
            a.Result is not ("OK" or "Running"));
    }

    private sealed record RequestRow(string Method, string Host, string Path, string Status, bool NeedsAttention, string Duration)
    {
        public static RequestRow From(ActivityRequest r) => new(
            r.Method, r.Host, r.Path, r.StatusText, !r.Succeeded,
            $"{Math.Round(r.Duration.TotalMilliseconds)} ms");
    }
}
