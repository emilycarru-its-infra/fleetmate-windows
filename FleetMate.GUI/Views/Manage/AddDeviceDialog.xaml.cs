using System.IO;
using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Models.Manage;
using FleetMate.Core.Services.Manage;
using FleetMate.GUI.ViewModels.Manage;
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
            ImportText.Text = ExtractDeviceLines(File.ReadAllText(dialog.FileName));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not read the file: {ex.Message}";
        }
    }

    /// <summary>
    /// A roster-shaped CSV yields hostname (or serial when there is no hostname);
    /// anything else is passed through as lines.
    /// </summary>
    internal static string ExtractDeviceLines(string content)
    {
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 2) return content;
        var header = RosterLoader.ParseLine(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
        var iHost = header.IndexOf("hostname");
        var iSerial = header.IndexOf("serial");
        var iAlloc = header.IndexOf("allocation");
        if (iHost < 0 && iSerial < 0) return content;

        var result = new List<string>();
        foreach (var line in lines.Skip(1))
        {
            var f = RosterLoader.ParseLine(line);
            string Get(int i) => i >= 0 && i < f.Count ? f[i].Trim() : "";
            var host = Get(iHost);
            if (host.Length == 0 && iHost < 0) host = Get(iAlloc);
            var value = host.Length > 0 ? host : Get(iSerial);
            if (value.Length > 0) result.Add(value);
        }
        return string.Join(Environment.NewLine, result);
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
            var entries = ManageViewModel.ParseDeviceLines(ImportText.Text);
            if (entries.Count == 0) { StatusText.Text = "Nothing to import."; return; }
            foreach (var (hostname, ip) in entries)
            {
                var roster = hostname.Length > 0
                    ? _vm.Roster.Source.FirstOrDefault(c => c.Hostname.Equals(hostname, StringComparison.OrdinalIgnoreCase) || c.Serial.Equals(hostname, StringComparison.OrdinalIgnoreCase))
                    : null;
                var name = roster != null && roster.HasHostname ? roster.Hostname : hostname;
                if (_vm.AddDevice(group, name, ip, roster?.Serial ?? "")) added++; else skipped++;
            }
        }

        ResultGroup = group;
        StatusText.Text = $"Added {added} to \"{group.Name}\"" + (skipped > 0 ? $", {skipped} already there or invalid" : "");
    }
}
