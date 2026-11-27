using FleetMate.Core.Manage;
using FleetMate.Core.Models.Manage;
using FleetMate.Core.Services.Manage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace FleetMate.WinUI.Pages.Manage;

/// <summary>A yes-or-no question with a verb on the button, the item named in the body.</summary>
internal sealed class ConfirmDialog(string title, string message, string verb) : ManageDialog
{
    public override string Title => title;
    public override string PrimaryText => verb;

    protected override FrameworkElement Build() => new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };

    public static async Task<bool> AskAsync(XamlRoot root, string title, string message, string verb) =>
        await new ConfirmDialog(title, message, verb).ShowAsync(root) == ContentDialogResult.Primary;
}

/// <summary>A one-line prompt, for naming and renaming custom groups.</summary>
internal sealed class TextPromptDialog(string title, string prompt, string initial) : ManageDialog
{
    private readonly TextBox _box = new() { Text = initial, MinWidth = 320 };

    public override string Title => title;
    public override string PrimaryText => "OK";
    public string Value => _box.Text.Trim();

    protected override FrameworkElement Build()
    {
        _box.Header = prompt;
        _box.Loaded += (_, _) => { _box.Focus(FocusState.Programmatic); _box.SelectAll(); };
        _box.TextChanged += (_, _) => PrimaryEnabled = Value.Length > 0;
        PrimaryEnabled = Value.Length > 0;
        return _box;
    }

    /// <summary>The entered text, or null when cancelled or empty.</summary>
    public static async Task<string?> AskAsync(XamlRoot root, string title, string prompt, string initial)
    {
        var dialog = new TextPromptDialog(title, prompt, initial);
        return await dialog.ShowAsync(root) == ContentDialogResult.Primary && dialog.Value.Length > 0 ? dialog.Value : null;
    }
}

/// <summary>Recall a previous command: load it into the runner, or rerun it as it was. Clear asks again afterwards.</summary>
internal sealed class HistoryDialog(IReadOnlyList<CommandHistoryEntry> history) : ManageDialog
{
    public enum Outcome { None, Load, Rerun, Clear }

    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single, Height = 320, MinWidth = 460 };

    public override string Title => "Command history";
    public override string PrimaryText => "Load";
    public override string? SecondaryText => "Run again";
    public override string CloseText => "Cancel";

    public Outcome Result { get; private set; }
    public CommandHistoryEntry? Chosen => _list.SelectedItem as CommandHistoryEntry;

    protected override FrameworkElement Build()
    {
        AutomationProperties.SetName(_list, "Command history");
        _list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel Padding="0,6">
                <TextBlock Text="{Binding Label}" Style="{StaticResource BodyStrongTextBlockStyle}" />
                <TextBlock Text="{Binding Command}" FontFamily="Consolas" FontSize="12" TextWrapping="Wrap" MaxLines="2"
                           Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
              </StackPanel>
            </DataTemplate>
            """);
        _list.ItemsSource = history.Select(h => new HistoryRow(h)).ToList();
        _list.DoubleTapped += (_, _) => { if (Chosen != null) { Result = Outcome.Load; Dialog?.Hide(); } };
        if (history.Count > 0) _list.SelectedIndex = 0;
        PrimaryEnabled = history.Count > 0;

        var clear = SmallButton("Clear history", () => { Result = Outcome.Clear; Dialog?.Hide(); });
        clear.IsEnabled = history.Count > 0;
        var empty = Caption("No commands run yet.");
        empty.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        return new StackPanel { Spacing = 8, Children = { empty, _list, clear } };
    }

    protected override bool OnPrimary() { Result = Outcome.Load; return Chosen != null; }
    protected override bool OnSecondary() { Result = Outcome.Rerun; return Chosen != null; }
}

/// <summary>A history entry for the list: <see cref="ListView"/> binds through it so the labels stay WinRT-bindable.</summary>
[Microsoft.UI.Xaml.Data.Bindable]
internal sealed partial class HistoryRow(CommandHistoryEntry entry) : CommandHistoryEntry(entry.Label, entry.Command);

/// <summary>
/// Asks for each &lt;PLACEHOLDER&gt; of a template command. Secret-like
/// placeholders use a masked box and stay redacted in the preview; the real
/// command exists only once Run is pressed.
/// </summary>
internal sealed class PlaceholderDialog(PlaceholderTemplate template) : ManageDialog
{
    private readonly Dictionary<string, Control> _inputs = new();
    private readonly TextBlock _preview = new() { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    public override string Title => template.Label;
    public override string PrimaryText => "Run";
    public string? ResolvedCommand { get; private set; }

    protected override FrameworkElement Build()
    {
        var fields = new StackPanel { Spacing = 10, MinWidth = 420 };
        foreach (var placeholder in template.Placeholders)
        {
            Control input;
            if (PlaceholderTemplate.IsSensitive(placeholder))
            {
                var box = new PasswordBox { Header = PlaceholderTemplate.FieldLabel(placeholder) };
                box.PasswordChanged += (_, _) => Update();
                input = box;
            }
            else
            {
                var box = new TextBox { Header = PlaceholderTemplate.FieldLabel(placeholder) };
                box.TextChanged += (_, _) => Update();
                input = box;
            }
            _inputs[placeholder] = input;
            fields.Children.Add(input);
        }
        fields.Children.Add(Caption("Command"));
        fields.Children.Add(new Border { Style = (Style)Application.Current.Resources["CardStyle"], Padding = new Thickness(10), Child = _preview });
        fields.Loaded += (_, _) => _inputs.Values.FirstOrDefault()?.Focus(FocusState.Programmatic);
        Update();
        return fields;
    }

    private Dictionary<string, string> Values() => _inputs.ToDictionary(kv => kv.Key, kv => kv.Value switch
    {
        PasswordBox p => p.Password,
        TextBox t => t.Text,
        _ => "",
    });

    private void Update()
    {
        var values = Values();
        _preview.Text = template.Resolve(values, redactSensitive: true);
        PrimaryEnabled = template.IsComplete(values);
    }

    protected override bool OnPrimary()
    {
        var values = Values();
        if (!template.IsComplete(values)) return false;
        ResolvedCommand = template.Resolve(values);
        return true;
    }
}

/// <summary>Pick which online machines get an SSH tab; all are ticked when none was checked.</summary>
internal sealed class SshTabPickerDialog : ManageDialog
{
    private readonly List<CheckBox> _boxes;
    private readonly List<MachineRowViewModel> _rows;
    private readonly bool _terminalAvailable;
    private readonly TextBlock _count = Caption("");

    public SshTabPickerDialog(IEnumerable<MachineRowViewModel> rows, bool terminalAvailable)
    {
        _terminalAvailable = terminalAvailable;
        _rows = rows.Where(r => r.IsOnline).OrderBy(r => r.FriendlyName, StringComparer.OrdinalIgnoreCase).ToList();
        var anyChecked = _rows.Any(r => r.IsSelected);
        _boxes = _rows.Select(r => new CheckBox { Content = $"{r.FriendlyName}  ·  {r.Ip}", IsChecked = !anyChecked || r.IsSelected }).ToList();
    }

    public override string Title => "Open SSH tabs";
    public override string PrimaryText => "Open";
    public IReadOnlyList<SshSession> Chosen { get; private set; } = Array.Empty<SshSession>();

    protected override FrameworkElement Build()
    {
        var list = new StackPanel { Spacing = 2 };
        foreach (var box in _boxes)
        {
            box.Checked += (_, _) => Update();
            box.Unchecked += (_, _) => Update();
            list.Children.Add(box);
        }
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                SmallButton("All", () => _boxes.ForEach(b => b.IsChecked = true)),
                SmallButton("None", () => _boxes.ForEach(b => b.IsChecked = false)),
                _count,
            },
        };
        _count.VerticalAlignment = VerticalAlignment.Center;
        _count.Margin = new Thickness(8, 0, 0, 0);
        var panel = new StackPanel { Spacing = 10, MinWidth = 400 };
        panel.Children.Add(tools);
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 340 });
        if (!_terminalAvailable) panel.Children.Add(Caption("Windows Terminal is not installed; each machine opens in its own console window."));
        Update();
        return panel;
    }

    private void Update()
    {
        var n = _boxes.Count(b => b.IsChecked == true);
        _count.Text = ManageText.Machines(n);
        PrimaryEnabled = n > 0;
    }

    protected override bool OnPrimary()
    {
        Chosen = _rows.Where((_, i) => _boxes[i].IsChecked == true)
            .Select(r => new SshSession(r.Ip, ManageViewModel.SessionTitle(r))).ToList();
        return Chosen.Count > 0;
    }
}

/// <summary>Add or edit one library command: its category (or a new one), label, command and trust level.</summary>
internal sealed class CommandEditorDialog : ManageDialog
{
    public enum Outcome { Cancelled, Saved, Deleted }

    private const string NewCategory = "New category…";
    private readonly IReadOnlyList<CommandCategory> _categories;
    private readonly ManagedCommand? _existing;
    private readonly ComboBox _category = new() { Header = "Category", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _newCategory = new() { Header = "New category name", Visibility = Visibility.Collapsed };
    private readonly TextBox _label = new() { Header = "Label" };
    private readonly TextBox _command = new()
    {
        Header = "Command",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 96,
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
    };
    private readonly ComboBox _trust = new() { Header = "Trust level", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _hint = Caption("");
    private bool _trustTouched;
    private bool _settingTrust;

    public CommandEditorDialog(IReadOnlyList<CommandCategory> categories, CommandCategory? category, ManagedCommand? existing, string? initialCommand = null)
    {
        _categories = categories;
        _existing = existing;
        foreach (var c in categories) _category.Items.Add(c.Name);
        _category.Items.Add(NewCategory);
        var index = category != null ? categories.ToList().IndexOf(category) : -1;
        _category.SelectedIndex = index >= 0 ? index : categories.Count > 0 ? 0 : _category.Items.Count - 1;
        foreach (var level in Enum.GetValues<CommandTrustLevel>()) _trust.Items.Add(level.Label());

        if (existing != null)
        {
            _label.Text = existing.Label;
            _command.Text = existing.Command;
            SelectTrust(existing.TrustLevel);
            _trustTouched = true;
        }
        else
        {
            _command.Text = initialCommand ?? "";
            SelectTrust(TrustInference.Infer(_command.Text));
        }
    }

    public override string Title => _existing == null ? "Add command" : "Edit command";
    public override string PrimaryText => _existing == null ? "Add" : "Save";
    public override string? SecondaryText => _existing == null ? null : "Delete…";

    public Outcome Result { get; private set; }
    public CommandCategory? ResultCategory { get; private set; }
    public string ResultNewCategoryName { get; private set; } = "";
    public string ResultLabel { get; private set; } = "";
    public string ResultCommand { get; private set; } = "";
    public CommandTrustLevel ResultTrust { get; private set; }

    protected override FrameworkElement Build()
    {
        _category.SelectionChanged += (_, _) =>
            _newCategory.Visibility = Equals(_category.SelectedItem, NewCategory) ? Visibility.Visible : Visibility.Collapsed;
        _newCategory.Visibility = Equals(_category.SelectedItem, NewCategory) ? Visibility.Visible : Visibility.Collapsed;
        _trust.SelectionChanged += (_, _) => { if (!_settingTrust) _trustTouched = true; UpdateHint(); };
        _command.TextChanged += (_, _) =>
        {
            // Follow the inferred trust until the operator picks one.
            if (!_trustTouched && _existing == null) SelectTrust(TrustInference.Infer(_command.Text));
            UpdateHint();
        };
        _label.Loaded += (_, _) => _label.Focus(FocusState.Programmatic);
        UpdateHint();
        return new StackPanel { Spacing = 12, MinWidth = 460, Children = { _category, _newCategory, _label, _command, _trust, _hint } };
    }

    private void SelectTrust(CommandTrustLevel level)
    {
        _settingTrust = true;
        _trust.SelectedIndex = (int)level;
        _settingTrust = false;
    }

    private void UpdateHint()
    {
        var inferred = TrustInference.Infer(_command.Text);
        var chosen = (CommandTrustLevel)Math.Max(0, _trust.SelectedIndex);
        _hint.Text = _command.Text.Trim().Length > 0 && inferred > chosen
            ? $"The command reads as {inferred.Label().ToLowerInvariant()}; it will be confirmed at that level."
            : "";
    }

    protected override bool OnPrimary()
    {
        var label = _label.Text.Trim();
        var command = _command.Text.Trim();
        if (label.Length == 0) { _hint.Text = "Give the command a label."; _label.Focus(FocusState.Programmatic); return false; }
        if (command.Length == 0) { _hint.Text = "Enter the command to run."; _command.Focus(FocusState.Programmatic); return false; }
        if (_category.SelectedIndex >= 0 && _category.SelectedIndex < _categories.Count)
            ResultCategory = _categories[_category.SelectedIndex];
        else
        {
            var name = _newCategory.Text.Trim();
            if (name.Length == 0) { _hint.Text = "Name the new category."; _newCategory.Focus(FocusState.Programmatic); return false; }
            ResultNewCategoryName = name;
        }
        ResultLabel = label;
        ResultCommand = command;
        ResultTrust = (CommandTrustLevel)Math.Max(0, _trust.SelectedIndex);
        Result = Outcome.Saved;
        return true;
    }

    /// <summary>Delete closes the editor; the page then asks before removing the command.</summary>
    protected override bool OnSecondary()
    {
        Result = Outcome.Deleted;
        return true;
    }
}

/// <summary>
/// Adds machines to a custom group three ways: pick them from the roster,
/// type a hostname or address, or paste or import a list. The target is the
/// group being viewed, another group, or a new one named here. Add keeps the
/// dialog open for more; Done closes it.
/// </summary>
internal sealed class AddDeviceDialog : ManageDialog
{
    private const string NewGroup = "New group…";
    private readonly ManageViewModel _vm;
    private readonly IReadOnlyList<RosterComputer> _preselected;
    private readonly Func<Task<string?>> _pickFile;
    private readonly ComboBox _group = new() { Header = "Add to", MinWidth = 220 };
    private readonly TextBox _newGroup = new() { Header = "New group name", MinWidth = 220 };
    private readonly SelectorBar _mode = new();
    private readonly SelectorBarItem _browseItem = new() { Text = "From the roster", IsSelected = true };
    private readonly SelectorBarItem _manualItem = new() { Text = "By name or address" };
    private readonly SelectorBarItem _importItem = new() { Text = "Paste or import" };
    private readonly TextBox _browseSearch = new() { PlaceholderText = "Filter hostnames, people, serials" };
    private readonly ListView _browse = new() { SelectionMode = ListViewSelectionMode.Multiple, Height = 260 };
    private readonly TextBlock _browseCount = Caption("");
    private readonly TextBox _host = new() { Header = "Hostname" };
    private readonly TextBox _ip = new() { Header = "IP address" };
    private readonly TextBox _import = new()
    {
        Header = "One machine per line: hostname, hostname,ip, or an address",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = 200,
        FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
    };
    private readonly TextBlock _status = Caption("");
    private StackPanel? _browsePanel, _manualPanel, _importPanel;

    public AddDeviceDialog(ManageViewModel vm, CustomGroup? target, IReadOnlyList<RosterComputer> preselected, Func<Task<string?>> pickFile)
    {
        _vm = vm;
        _preselected = preselected;
        _pickFile = pickFile;
        foreach (var g in vm.CustomGroups) _group.Items.Add(g.Name);
        _group.Items.Add(NewGroup);
        _group.SelectedIndex = target != null ? vm.CustomGroups.IndexOf(target) : _group.Items.Count - 1;
    }

    public override string Title => "Add devices";
    public override string PrimaryText => "Add";
    public override string? SecondaryText => _manualItem.IsSelected ? "Add to current view only" : null;
    public override string CloseText => "Done";

    /// <summary>The group devices were added to, so the page can select it.</summary>
    public CustomGroup? ResultGroup { get; private set; }

    /// <summary>Show the manual mode, for the QA capture.</summary>
    public void ShowManual() => _mode.SelectedItem = _manualItem;

    protected override FrameworkElement Build()
    {
        _mode.Items.Add(_browseItem);
        _mode.Items.Add(_manualItem);
        _mode.Items.Add(_importItem);
        _mode.SelectionChanged += (_, _) => UpdateMode();

        _browse.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel Padding="0,4">
                <TextBlock Text="{Binding Title}" />
                <TextBlock Text="{Binding Detail}" Style="{StaticResource CaptionTextBlockStyle}" Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
              </StackPanel>
            </DataTemplate>
            """);
        AutomationProperties.SetName(_browse, "Roster machines");
        _browseSearch.TextChanged += (_, _) => FillBrowse();
        FillBrowse();
        foreach (var row in _browse.Items.OfType<ComputerRow>().Where(r => _preselected.Any(p => p.Serial == r.Computer.Serial)))
            _browse.SelectedItems.Add(row);
        if (_preselected.Count > 0) _status.Text = $"{_preselected.Count} machines picked from the list";

        _browsePanel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _browseSearch,
                new Grid { Children = { _browseCount, WithAlignment(SmallButton("Select all", () => _browse.SelectAll()), HorizontalAlignment.Right) } },
                _browse,
            },
        };
        _manualPanel = new StackPanel { Spacing = 10, Children = { _host, _ip } };
        _importPanel = new StackPanel
        {
            Spacing = 8,
            Children = { _import, SmallButton("Import a file…", async () => await ImportFileAsync()) },
        };

        _group.SelectionChanged += (_, _) => _newGroup.Visibility = IsNewGroup ? Visibility.Visible : Visibility.Collapsed;
        _newGroup.Visibility = IsNewGroup ? Visibility.Visible : Visibility.Collapsed;
        var target = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _group, _newGroup } };
        UpdateMode();
        return new StackPanel
        {
            Spacing = 12,
            Width = 520,
            Children = { target, _mode, _browsePanel, _manualPanel, _importPanel, _status },
        };
    }

    private static FrameworkElement WithAlignment(FrameworkElement element, HorizontalAlignment alignment)
    {
        element.HorizontalAlignment = alignment;
        return element;
    }

    private bool IsNewGroup => Equals(_group.SelectedItem, NewGroup);

    private void UpdateMode()
    {
        if (_browsePanel == null) return;
        _browsePanel.Visibility = _browseItem.IsSelected ? Visibility.Visible : Visibility.Collapsed;
        _manualPanel!.Visibility = _manualItem.IsSelected ? Visibility.Visible : Visibility.Collapsed;
        _importPanel!.Visibility = _importItem.IsSelected ? Visibility.Visible : Visibility.Collapsed;
        if (Dialog != null)
        {
            Dialog.SecondaryButtonText = SecondaryText ?? "";
            Dialog.IsSecondaryButtonEnabled = _vm.HasSelection;
        }
    }

    private void FillBrowse()
    {
        var q = _browseSearch.Text.Trim();
        var items = (q.Length == 0 ? _vm.Roster.Source : _vm.Roster.Source.Where(c => ManageViewModel.Matches(c, q))).Select(c => new ComputerRow(c)).ToList();
        _browse.ItemsSource = items;
        _browseCount.Text = ManageText.Machines(items.Count);
    }

    private async Task ImportFileAsync()
    {
        if (await _pickFile() is not { } path) return;
        try { _import.Text = ManageText.ExtractDeviceLines(await File.ReadAllTextAsync(path)); }
        catch (Exception ex) { _status.Text = $"Could not read the file: {ex.Message}"; }
    }

    private CustomGroup? ResolveTargetGroup()
    {
        if (!IsNewGroup && _group.SelectedIndex >= 0 && _group.SelectedIndex < _vm.CustomGroups.Count) return _vm.CustomGroups[_group.SelectedIndex];
        var name = _newGroup.Text.Trim();
        if (name.Length == 0)
        {
            _status.Text = "Give the new group a name first.";
            _newGroup.Focus(FocusState.Programmatic);
            return null;
        }
        var created = _vm.CreateGroup(name);
        _group.Items.Insert(_group.Items.Count - 1, created.Name);
        _group.SelectedIndex = _group.Items.Count - 2;
        return created;
    }

    /// <summary>Add, then stay open for more.</summary>
    protected override bool OnPrimary()
    {
        var group = ResolveTargetGroup();
        if (group == null) return false;
        int added = 0, skipped = 0;
        if (_browseItem.IsSelected)
        {
            var picked = _browse.SelectedItems.OfType<ComputerRow>().Select(r => r.Computer).ToList();
            if (picked.Count == 0) { _status.Text = "Pick at least one machine."; return false; }
            added = _vm.AddRosterDevices(group, picked);
            skipped = picked.Count - added;
        }
        else if (_manualItem.IsSelected)
        {
            var host = _host.Text.Trim();
            var ip = _ip.Text.Trim();
            if (host.Length == 0 && ip.Length == 0) { _status.Text = "Enter a hostname or an IP address."; return false; }
            if (ip.Length > 0 && !System.Net.IPAddress.TryParse(ip, out _)) { _status.Text = "That is not a valid IP address."; return false; }
            var roster = host.Length > 0 ? _vm.Roster.Source.FirstOrDefault(c => c.Hostname.Equals(host, StringComparison.OrdinalIgnoreCase)) : null;
            if (_vm.AddDevice(group, host, ip, roster?.Serial ?? "")) added = 1; else skipped = 1;
            _host.Text = _ip.Text = "";
        }
        else
        {
            if (ManageViewModel.ParseDeviceLines(_import.Text).Count == 0) { _status.Text = "Nothing to import."; return false; }
            (added, skipped) = ManageText.ImportDevices(_vm, group, _import.Text);
        }
        ResultGroup = group;
        _status.Text = ManageText.Added(added, skipped, group.Name);
        return false;
    }

    /// <summary>"Add to current view only": on screen, saved to no group (macOS parity).</summary>
    protected override bool OnSecondary()
    {
        var ip = _ip.Text.Trim();
        if (ip.Length == 0) { _status.Text = "Enter an address to add it to the current view."; return false; }
        if (_vm.AddAdhocComputer(_host.Text.Trim(), ip)) return true;
        _status.Text = _vm.HasSelection ? "That machine is already on screen." : "Select a lab or group first.";
        return false;
    }
}

/// <summary>A roster machine for a list: its friendly name over "hostname · location · serial".</summary>
[Microsoft.UI.Xaml.Data.Bindable]
internal sealed partial class ComputerRow(RosterComputer computer)
{
    public RosterComputer Computer { get; } = computer;
    public string Title => Computer.FriendlyName;
    public string Detail => string.Join(" · ", new[] { Computer.Hostname, Computer.Location, Computer.Serial }.Where(s => s.Length > 0));
}

/// <summary>
/// The Curriculum header's lab picker (macOS parity): labs grouped by area on
/// the left with a filter, the selection on the right. Tick a lab, press an
/// area's + to add every lab in it, or drag a lab or a whole area across.
/// All, Visible and Clear work on the whole list or the filtered one.
/// </summary>
internal sealed class LabPickerDialog : ManageDialog
{
    private readonly IReadOnlyList<RosterRoom> _labs;
    private readonly List<LabArea> _areas;
    private readonly HashSet<string> _selected;
    private readonly TextBox _filter = new() { PlaceholderText = "Filter areas or labs" };
    private readonly StackPanel _areaHost = new() { Spacing = 2 };
    private readonly StackPanel _selectedHost = new() { Spacing = 2 };
    private readonly TextBlock _summary = Caption("");
    private readonly Border _dropZone = new() { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6), Padding = new Thickness(8), AllowDrop = true };

    public LabPickerDialog(IReadOnlyList<RosterRoom> labs, IEnumerable<string> initiallySelected)
    {
        _labs = labs;
        _areas = LabPicker.Group(labs);
        _selected = initiallySelected.Where(id => labs.Any(l => l.Id == id)).ToHashSet();
    }

    public override string Title => "Select labs";
    public override string PrimaryText => "Apply";
    public List<RosterRoom> SelectedRooms { get; } = new();

    protected override FrameworkElement Build()
    {
        _filter.TextChanged += (_, _) => RenderAreas();
        _summary.VerticalAlignment = VerticalAlignment.Center;
        var tools = new Grid();
        tools.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                SmallButton("All", () => { _selected.UnionWith(_labs.Select(l => l.Id)); Refresh(); }),
                SmallButton("Visible", () => { _selected.UnionWith(LabPicker.Filter(_areas, _filter.Text).SelectMany(a => a.Rooms).Select(r => r.Id)); Refresh(); }),
                SmallButton("Clear", () => { _selected.Clear(); Refresh(); }),
            },
        });
        _summary.HorizontalAlignment = HorizontalAlignment.Right;
        tools.Children.Add(_summary);

        _dropZone.BorderBrush = Brush("ControlStrokeColorDefaultBrush");
        _dropZone.Background = Brush("LayerFillColorDefaultBrush");
        _dropZone.Child = new ScrollViewer { Content = _selectedHost };
        _dropZone.DragOver += OnDragOver;
        _dropZone.DragLeave += (_, _) => _dropZone.BorderBrush = Brush("ControlStrokeColorDefaultBrush");
        _dropZone.Drop += OnDrop;
        AutomationProperties.SetName(_dropZone, "Selected labs");

        var panes = new Grid { ColumnSpacing = 12, Height = 380 };
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panes.Children.Add(new ScrollViewer { Content = _areaHost });
        Grid.SetColumn(_dropZone, 1);
        panes.Children.Add(_dropZone);

        Refresh();
        return new StackPanel { Spacing = 10, Width = 620, Children = { _filter, tools, panes } };
    }

    private void Refresh()
    {
        RenderAreas();
        RenderSelected();
    }

    private void RenderAreas()
    {
        _areaHost.Children.Clear();
        foreach (var area in LabPicker.Filter(_areas, _filter.Text))
        {
            var header = new Grid { Margin = new Thickness(0, 8, 0, 2), ColumnSpacing = 6, CanDrag = true, Background = Brush("SubtleFillColorTransparentBrush") };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var add = IconButton("", $"Add every lab in {area.Name}", () => { _selected.UnionWith(area.Rooms.Select(r => r.Id)); Refresh(); });
            var title = new TextBlock { Text = $"{area.Name}  ({area.Rooms.Count} labs)", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            var count = Caption(area.ComputerCount.ToString());
            count.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(title, 1);
            Grid.SetColumn(count, 2);
            header.Children.Add(add);
            header.Children.Add(title);
            header.Children.Add(count);
            Draggable(header, LabPicker.AreaPayload(area));
            _areaHost.Children.Add(header);

            foreach (var room in area.Rooms)
            {
                var box = new CheckBox { Content = $"{room.Name}  ({room.Count})", Margin = new Thickness(34, 0, 0, 0), IsChecked = _selected.Contains(room.Id), CanDrag = true };
                box.Click += (_, _) =>
                {
                    if (box.IsChecked == true) _selected.Add(room.Id); else _selected.Remove(room.Id);
                    RenderSelected();
                };
                Draggable(box, LabPicker.RoomPayload(room));
                _areaHost.Children.Add(box);
            }
        }
    }

    private void RenderSelected()
    {
        var selected = LabPicker.Selected(_labs, _selected);
        _summary.Text = LabPicker.Summary(selected);
        _selectedHost.Children.Clear();
        if (selected.Count == 0)
        {
            var empty = Caption("No labs selected\nDrag areas or labs here");
            empty.TextAlignment = TextAlignment.Center;
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Margin = new Thickness(0, 40, 0, 0);
            _selectedHost.Children.Add(empty);
            return;
        }
        foreach (var room in selected)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var count = Caption(room.Count.ToString());
            count.VerticalAlignment = VerticalAlignment.Center;
            var remove = IconButton("", $"Remove {room.Name}", () => { _selected.Remove(room.Id); Refresh(); });
            Grid.SetColumn(count, 1);
            Grid.SetColumn(remove, 2);
            row.Children.Add(new TextBlock { Text = room.Name, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(count);
            row.Children.Add(remove);
            _selectedHost.Children.Add(row);
        }
    }

    private static void Draggable(UIElement element, string payload) =>
        element.DragStarting += (_, e) =>
        {
            e.Data.SetText(payload);
            e.Data.RequestedOperation = DataPackageOperation.Copy;
        };

    private async void OnDragOver(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var ours = e.DataView.Contains(StandardDataFormats.Text)
                && (await e.DataView.GetTextAsync()).StartsWith(LabPicker.DragPrefix, StringComparison.Ordinal);
            e.AcceptedOperation = ours ? DataPackageOperation.Copy : DataPackageOperation.None;
            if (ours)
            {
                e.DragUIOverride.Caption = "Add to the selection";
                _dropZone.BorderBrush = Brush("AccentFillColorDefaultBrush");
            }
        }
        finally { deferral.Complete(); }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        _dropZone.BorderBrush = Brush("ControlStrokeColorDefaultBrush");
        if (!e.DataView.Contains(StandardDataFormats.Text)) return;
        var deferral = e.GetDeferral();
        try
        {
            if (LabPicker.ApplyDrop(_selected, await e.DataView.GetTextAsync(), _areas)) Refresh();
        }
        finally { deferral.Complete(); }
    }

    protected override bool OnPrimary()
    {
        SelectedRooms.Clear();
        SelectedRooms.AddRange(LabPicker.Selected(_labs, _selected));
        return true;
    }
}
