using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using FleetMate.Core.Services.Manage;
using FleetMate.GUI.ViewModels.Manage;

namespace FleetMate.GUI.Views.Manage;

/// <summary>Pick which online machines get an SSH tab.</summary>
public partial class SshTabPickerDialog : Window
{
    public partial class Item : ObservableObject
    {
        [ObservableProperty] private bool _checked;
        public string Label { get; init; } = "";
        public string Ip { get; init; } = "";
        public MachineRowViewModel Row { get; init; } = null!;
    }

    private readonly List<Item> _items;

    public IReadOnlyList<SshSession> Chosen { get; private set; } = Array.Empty<SshSession>();

    public SshTabPickerDialog(IEnumerable<MachineRowViewModel> rows, bool terminalAvailable)
    {
        InitializeComponent();
        _items = rows
            .Where(r => r.IsOnline)
            .OrderBy(r => r.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .Select(r => new Item { Checked = r.IsSelected, Label = r.FriendlyName, Ip = r.Ip, Row = r })
            .ToList();
        if (_items.Count > 0 && _items.All(i => !i.Checked)) foreach (var i in _items) i.Checked = true;
        foreach (var i in _items) i.PropertyChanged += (_, _) => UpdateCount();
        HostList.ItemsSource = _items;
        if (!terminalAvailable) HintText.Text = "Windows Terminal is not installed; each machine opens in its own console window.";
        UpdateCount();
    }

    private void UpdateCount()
    {
        var n = _items.Count(i => i.Checked);
        CountText.Text = n == 1 ? "1 machine" : $"{n} machines";
        OpenButton.IsEnabled = n > 0;
    }

    private void OnAll(object sender, RoutedEventArgs e) { foreach (var i in _items) i.Checked = true; }
    private void OnNone(object sender, RoutedEventArgs e) { foreach (var i in _items) i.Checked = false; }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        Chosen = _items.Where(i => i.Checked).Select(i => new SshSession(i.Ip, SessionTitle(i.Row))).ToList();
        DialogResult = Chosen.Count > 0;
    }

    public static string SessionTitle(MachineRowViewModel row) =>
        row.Computer.HasHostname && row.Computer.Hostname != row.FriendlyName
            ? $"{row.FriendlyName} ({row.Computer.Hostname})"
            : row.FriendlyName;
}
