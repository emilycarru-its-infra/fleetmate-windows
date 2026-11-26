using System.IO;
using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Models.Manage;
using FleetMate.Core.Services.Manage;
using FleetMate.Core.Manage;
using Microsoft.Win32;

namespace FleetMate.GUI.Views.Manage;

/// <summary>
/// Adds machines to a custom group three ways: pick them from the roster,
/// type a hostname or address, or paste/import a list. The target can be
/// the group being viewed, any existing group, or a new one named here.
/// </summary>
public partial class AddDeviceDialog : Window
{
    private const string NewGroupTag = "__new__";
    private readonly ManageViewModel _vm;
    private readonly IReadOnlyList<RosterComputer> _preselected;

    /// <summary>The group devices were added to, so the page can select it.</summary>
    public CustomGroup? ResultGroup { get; private set; }

    public AddDeviceDialog(ManageViewModel vm, CustomGroup? target, IReadOnlyList<RosterComputer> preselected)
    {
        InitializeComponent();
        _vm = vm;
        _preselected = preselected;

        foreach (var g in _vm.CustomGroups)
            GroupCombo.Items.Add(new ComboBoxItem { Content = g.Name, Tag = g });
        GroupCombo.Items.Add(new ComboBoxItem { Content = "New group...", Tag = NewGroupTag });
        GroupCombo.SelectedIndex = target != null ? _vm.CustomGroups.IndexOf(target) : GroupCombo.Items.Count - 1;

        BrowseList.ItemsSource = _vm.Roster.Source;
        BrowseCount.Text = $"{_vm.Roster.Source.Count} machines";

        if (_preselected.Count > 0)
        {
            foreach (var c in _preselected) BrowseList.SelectedItems.Add(c);
            StatusText.Text = $"{_preselected.Count} machines picked from the list";
        }
    }

    private CustomGroup? ResolveTargetGroup()
    {
        if (GroupCombo.SelectedItem is not ComboBoxItem item) return null;
        if (item.Tag is CustomGroup g) return g;
        var name = NewGroupBox.Text.Trim();
        if (name.Length == 0)
        {
            StatusText.Text = "Give the new group a name first.";
            NewGroupBox.Focus();
            return null;
        }
        var created = _vm.CreateGroup(name);
        GroupCombo.Items.Insert(GroupCombo.Items.Count - 1, new ComboBoxItem { Content = created.Name, Tag = created });
        GroupCombo.SelectedIndex = GroupCombo.Items.Count - 2;
        return created;
    }

    private void OnGroupComboChanged(object sender, SelectionChangedEventArgs e)
    {
        var isNew = GroupCombo.SelectedItem is ComboBoxItem { Tag: NewGroupTag };
        NewGroupBox.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (BrowsePanel == null) return;
        BrowsePanel.Visibility = BrowseMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ManualPanel.Visibility = ManualMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ImportPanel.Visibility = ImportMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AddToViewButton.Visibility = ManualMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AddToViewButton.IsEnabled = _vm.HasSelection;
    }

    /// <summary>Put the machine on screen without saving it to a group (macOS parity).</summary>
    private void OnAddToCurrentView(object sender, RoutedEventArgs e)
    {
        var host = ManualHost.Text.Trim();
        var ip = ManualIp.Text.Trim();
        if (ip.Length == 0)
        {
            StatusText.Text = "Enter an address to add it to the current view.";
            return;
        }
        if (_vm.AddAdhocComputer(host, ip)) { DialogResult = true; Close(); }
        else StatusText.Text = _vm.HasSelection ? "That machine is already on screen." : "Select a lab or group first.";
    }

    private void OnBrowseSearchChanged(object sender, TextChangedEventArgs e)
    {
        var q = BrowseSearch.Text.Trim();
        var items = q.Length == 0 ? _vm.Roster.Source : _vm.Roster.Source.Where(c => ManageViewModel.Matches(c, q)).ToList();
        BrowseList.ItemsSource = items;
        BrowseCount.Text = $"{items.Count} machines";
    }

    private void OnBrowseSelectAll(object sender, RoutedEventArgs e) => BrowseList.SelectAll();

    private void OnImportFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Lists (*.csv;*.txt)|*.csv;*.txt|All files (*.*)|*.*", Title = "Import device list" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ImportText.Text = ManageText.ExtractDeviceLines(File.ReadAllText(dialog.FileName));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not read the file: {ex.Message}";
        }
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var group = ResolveTargetGroup();
        if (group == null) return;

        var added = 0;
        var skipped = 0;
        if (BrowseMode.IsChecked == true)
        {
            var picked = BrowseList.SelectedItems.Cast<RosterComputer>().ToList();
            if (picked.Count == 0) { StatusText.Text = "Pick at least one machine."; return; }
            added = _vm.AddRosterDevices(group, picked);
            skipped = picked.Count - added;
        }
        else if (ManualMode.IsChecked == true)
        {
            var host = ManualHost.Text.Trim();
            var ip = ManualIp.Text.Trim();
            if (host.Length == 0 && ip.Length == 0) { StatusText.Text = "Enter a hostname or an IP address."; return; }
            if (ip.Length > 0 && !System.Net.IPAddress.TryParse(ip, out _)) { StatusText.Text = "That is not a valid IP address."; return; }
            var bySerial = host.Length > 0 ? _vm.Roster.Source.FirstOrDefault(c => c.Hostname.Equals(host, StringComparison.OrdinalIgnoreCase)) : null;
            if (_vm.AddDevice(group, host, ip, bySerial?.Serial ?? "")) added = 1; else skipped = 1;
            ManualHost.Clear();
            ManualIp.Clear();
        }
        else
        {
            if (ManageViewModel.ParseDeviceLines(ImportText.Text).Count == 0) { StatusText.Text = "Nothing to import."; return; }
            (added, skipped) = ManageText.ImportDevices(_vm, group, ImportText.Text);
        }

        ResultGroup = group;
        StatusText.Text = ManageText.Added(added, skipped, group.Name);
    }
}
