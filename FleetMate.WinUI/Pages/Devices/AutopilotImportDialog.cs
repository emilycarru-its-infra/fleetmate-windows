using FleetMate.Core.Models.Devices;
using FleetMate.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FleetMate.WinUI.Pages.Devices;

/// <summary>
/// Review a hardware hash CSV, optionally give every device one group tag,
/// upload it, and follow each device's import state until Intune finishes
/// (at most ten minutes). The file is parsed by FleetMate.Core's
/// <see cref="AutopilotHashCsv"/>, as in FleetMate.GUI.
/// </summary>
public sealed partial class AutopilotImportDialog : ContentDialog
{
    private readonly GraphService _graph;
    private readonly AutopilotHashCsv _csv;
    private readonly TextBox _groupTag = new() { Header = "Group tag for every device (optional; otherwise each keeps the file's)" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };

    public bool Imported { get; private set; }

    public AutopilotImportDialog(GraphService graph, AutopilotHashCsv csv, string fileName)
    {
        _graph = graph;
        _csv = csv;
        Title = "Import Hardware Hashes";
        PrimaryButtonText = "Import";
        CloseButtonText = "Close";
        DefaultButton = ContentDialogButton.Primary;
        IsPrimaryButtonEnabled = csv.Entries.Count is > 0 and <= AutopilotHashCsv.MaxEntries;
        AutomationProperties.SetName(_status, "Import status");
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

        var root = new StackPanel { Spacing = 12, MinWidth = 440 };
        root.Children.Add(new TextBlock
        {
            Text = $"{fileName}: {csv.Entries.Count} device{(csv.Entries.Count == 1 ? "" : "s")} to register",
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        });
        if (csv.Issues.Count > 0)
        {
            root.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Warning,
                Message = string.Join("\n", csv.Issues.Take(8).Select(i => i.Line > 0 ? $"Line {i.Line}: {i.Message}" : i.Message))
                          + (csv.Issues.Count > 8 ? $"\n…and {csv.Issues.Count - 8} more" : ""),
            });
        }
        root.Children.Add(_groupTag);
        root.Children.Add(_status);
        Content = root;

        PrimaryButtonClick += async (_, args) =>
        {
            // Stay open: the import reports its progress here.
            args.Cancel = true;
            await ImportAsync();
        };
    }

    private async Task ImportAsync()
    {
        IsPrimaryButtonEnabled = false;
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
            _status.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            IsPrimaryButtonEnabled = true;
            _groupTag.IsEnabled = true;
        }
    }
}
