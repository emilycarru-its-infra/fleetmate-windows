using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using ModernWpf.Controls.Primitives;

namespace FleetMate.GUI.Views.Devices;

/// <summary>
/// Windows Autopilot actions for the selected devices, and hardware hash
/// import. Every action works on Autopilot identity IDs, never Intune IDs,
/// and each one asks before it changes anything.
/// </summary>
public partial class IntunePage
{
    private List<AutopilotDevice> SelectedIdentities() =>
        SelectedRows().Where(r => r.Autopilot != null).Select(r => r.Autopilot!).ToList();

    private async void OnAutopilotSetGroupTagClicked(object sender, RoutedEventArgs e)
    {
        var tag = AutopilotGroupTagBox.Text.Trim();
        var identities = SelectedIdentities();
        var what = tag.Length == 0 ? "clear the group tag on" : $"set group tag \"{tag}\" on";
        if (!Confirm($"Do you want to {what} {identities.Count} Autopilot identit{(identities.Count == 1 ? "y" : "ies")}?", "Set Group Tag")) return;
        await RunAutopilotAsync("Setting group tag", identities, id => _graphService!.SetAutopilotGroupTagAsync(id.Id, tag));
    }

    private async void OnAutopilotAssignUserClicked(object sender, RoutedEventArgs e)
    {
        var upn = AutopilotUserBox.Text.Trim();
        if (!upn.Contains('@'))
        {
            ShowActionMessage("Enter the user's principal name (user@domain).", isError: true);
            return;
        }
        var identities = SelectedIdentities();
        if (!Confirm($"Assign {upn} to {identities.Count} Autopilot identit{(identities.Count == 1 ? "y" : "ies")}?", "Assign User")) return;
        await RunAutopilotAsync("Assigning user", identities, id => _graphService!.AssignAutopilotUserAsync(id.Id, upn));
    }

    private async void OnAutopilotUnassignUserClicked(object sender, RoutedEventArgs e)
    {
        var identities = SelectedIdentities();
        if (!Confirm($"Remove the assigned user from {identities.Count} Autopilot identit{(identities.Count == 1 ? "y" : "ies")}?", "Unassign User")) return;
        await RunAutopilotAsync("Unassigning user", identities, id => _graphService!.UnassignAutopilotUserAsync(id.Id));
    }

    private async void OnAutopilotSyncClicked(object sender, RoutedEventArgs e)
    {
        if (_graphService == null) return;
        if (!Confirm("Sync Intune with the Autopilot service? This applies to the whole tenant.", "Sync Autopilot")) return;
        ShowActionMessage("Syncing Autopilot...", isLoading: true);
        var result = await _graphService.SyncAutopilotAsync();
        ShowActionMessage(result.Success ? "Autopilot sync requested." : $"Autopilot sync failed: {result.Message}", isError: !result.Success);
    }

    private async void OnAutopilotDeleteClicked(object sender, RoutedEventArgs e)
    {
        var identities = SelectedIdentities();
        if (!Confirm($"Delete {identities.Count} Autopilot identit{(identities.Count == 1 ? "y" : "ies")}? " +
                     "The devices are untouched but stop receiving an Autopilot profile. This cannot be undone.",
                     "Delete Autopilot Identity", MessageBoxImage.Warning)) return;
        await RunAutopilotAsync("Deleting identity", identities,
            id => _graphService!.DeleteAutopilotIdentityAsync(id.Id, confirmed: true));
    }

    private async Task RunAutopilotAsync(string verb, List<AutopilotDevice> identities,
        Func<AutopilotDevice, Task<GraphService.DeviceActionResult>> action)
    {
        if (_graphService == null || identities.Count == 0) return;
        ShowActionMessage($"{verb} on {identities.Count} device(s)...", isLoading: true);
        var results = new List<GraphService.DeviceActionResult>();
        foreach (var identity in identities) results.Add(await action(identity));

        var failed = results.Where(r => !r.Success).ToList();
        ShowActionMessage(failed.Count == 0
                ? $"{verb}: done on {results.Count} device(s)."
                : $"{verb}: {results.Count - failed.Count} done, {failed.Count} failed — {failed[0].Message}",
            isError: failed.Count > 0);

        _autopilot = new();
        await LoadAutopilotAsync();
    }

    private static bool Confirm(string message, string title, MessageBoxImage image = MessageBoxImage.Question) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, image) == MessageBoxResult.Yes;

    // ── Hardware hash import ─────────────────────────────────────────────

    private async void OnImportHashesClicked(object sender, RoutedEventArgs e)
    {
        if (_graphService == null) return;
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import hardware hashes",
            Filter = "Hardware hash CSV (*.csv)|*.csv|All files (*.*)|*.*"
        };
        if (picker.ShowDialog() != true) return;

        AutopilotHashCsv csv;
        try
        {
            csv = AutopilotHashCsv.Parse(await System.IO.File.ReadAllBytesAsync(picker.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Import Hashes", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new AutopilotImportWindow(_graphService, csv, System.IO.Path.GetFileName(picker.FileName))
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
        if (dialog.Imported)
        {
            _autopilot = new();
            await LoadAutopilotAsync();
        }
    }
}

/// <summary>
/// Review a hardware hash CSV, optionally give every device one group tag,
/// upload it, and follow each device's import state until Intune finishes.
/// </summary>
public sealed class AutopilotImportWindow : Window
{
    private readonly GraphService _graph;
    private readonly AutopilotHashCsv _csv;
    private readonly TextBox _groupTag = new() { Margin = new Thickness(0, 4, 0, 12) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _import = new() { Content = "Import", MinWidth = 90 };

    public bool Imported { get; private set; }

    public AutopilotImportWindow(GraphService graph, AutopilotHashCsv csv, string fileName)
    {
        _graph = graph;
        _csv = csv;
        Title = "Import Hardware Hashes";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ModernWpf.ThemeManager.SetRequestedTheme(this, ModernWpf.ThemeManager.GetActualTheme(Application.Current.MainWindow!));
        SetResourceReference(BackgroundProperty, "SystemControlBackgroundAltHighBrush");

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock
        {
            Text = $"{fileName}: {csv.Entries.Count} device{(csv.Entries.Count == 1 ? "" : "s")} to register",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        if (csv.Issues.Count > 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = string.Join("\n", csv.Issues.Take(8).Select(i => i.Line > 0 ? $"Line {i.Line}: {i.Message}" : i.Message))
                       + (csv.Issues.Count > 8 ? $"\n…and {csv.Issues.Count - 8} more" : ""),
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.DarkOrange,
                Margin = new Thickness(0, 8, 0, 0)
            });
        }
        root.Children.Add(new TextBlock { Text = "Group tag for every device (optional; otherwise each keeps the file's)", Margin = new Thickness(0, 12, 0, 0) });
        root.Children.Add(_groupTag);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = "Close", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        close.Click += (_, _) => Close();
        _import.IsEnabled = csv.Entries.Count is > 0 and <= AutopilotHashCsv.MaxEntries;
        _import.Click += async (_, _) => await ImportAsync();
        buttons.Children.Add(close);
        buttons.Children.Add(_import);
        root.Children.Add(buttons);
        root.Children.Add(_status);
        Content = root;
    }

    private async Task ImportAsync()
    {
        _import.IsEnabled = false;
        _groupTag.IsEnabled = false;
        var tag = _groupTag.Text.Trim();
        _status.Text = "Uploading...";
        try
        {
            var pending = await _graph.ImportAutopilotHashesAsync(_csv.Entries.Select(e => e.WithGroupTag(tag)));
            Imported = true;

            // Intune registers imported hashes asynchronously; follow each to the end.
            var deadline = DateTime.UtcNow.AddMinutes(10);
            while (pending.Any(p => !p.IsFinished) && DateTime.UtcNow < deadline)
            {
                _status.Text = $"Registering: {pending.Count(p => p.IsFinished)} of {pending.Count} finished...";
                await Task.Delay(TimeSpan.FromSeconds(10));
                for (var i = 0; i < pending.Count; i++)
                    if (!pending[i].IsFinished && await _graph.GetImportedAutopilotIdentityAsync(pending[i].Id) is { } fresh)
                        pending[i] = fresh;
            }

            var failed = pending.Where(p => p.IsFinished && !p.Succeeded).ToList();
            var unfinished = pending.Count(p => !p.IsFinished);
            _status.Text = $"{pending.Count(p => p.Succeeded)} registered"
                + (failed.Count > 0 ? $", {failed.Count} failed: " + string.Join("; ", failed.Take(5).Select(f => $"{f.SerialNumber} ({f.FailureReason})")) : "")
                + (unfinished > 0 ? $", {unfinished} still registering — check back later." : ".");
        }
        catch (Exception ex)
        {
            _status.Text = $"Import failed: {ex.Message}";
            _import.IsEnabled = true;
            _groupTag.IsEnabled = true;
        }
    }
}
