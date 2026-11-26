using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FleetMate.Core.Config;
using FleetMate.WinUI.Shell;
using Serilog;

namespace FleetMate.WinUI.Pages.Settings;

/// <summary>
/// Settings › Connections, Manage and Terminal: the values the WPF page
/// edits, drawn from <see cref="DesktopSettingsFields"/> and saved by
/// <see cref="DesktopSettings.Save"/>, so both apps write the same keys by
/// the same rules. Demo mode never saves.
/// </summary>
public sealed partial class SettingsEditViewModel : ObservableObject
{
    private readonly ShellServices _services;
    private DesktopSettings _settings;

    public SettingsEditViewModel(ShellServices services)
    {
        _services = services;
        _settings = DesktopSettings.From(services.Config);
        Connections = Cards(DesktopSettingsFields.Connections, Array.Empty<DesktopSettingToggle>());
        Manage = Cards(DesktopSettingsFields.Manage, DesktopSettingsFields.ManageToggles);
        Terminal = Cards(DesktopSettingsFields.Terminal, DesktopSettingsFields.TerminalToggles);
    }

    public IReadOnlyList<SettingsCard> Connections { get; }
    public IReadOnlyList<SettingsCard> Manage { get; }
    public IReadOnlyList<SettingsCard> Terminal { get; }

    public bool CanSave => !_services.DemoMode;

    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _status = "";

    /// <summary>The SSH key's state, as the Manage section shows it.</summary>
    public string SshKeyStatus
    {
        get
        {
            var manage = _services.Config.Manage ?? new ManageConfig();
            return manage.HasSshKey
                ? $"Key found at {manage.ResolvedSshKeyPath}. Sessions connect as {manage.ResolvedSshUser}."
                : $"No key at {manage.ResolvedSshKeyPath}. Machine details and command runs need the fleet admin key; sessions and scanning still work without it.";
        }
    }

    internal void Update(Func<DesktopSettings, DesktopSettings> change)
    {
        _settings = change(_settings);
        IsDirty = true;
        Status = "";
    }

    [RelayCommand]
    private void Save()
    {
        if (!CanSave)
        {
            Status = "Demo mode does not save.";
            return;
        }
        try
        {
            using (var sink = new RegistrySettingsSink())
                _settings.Save(sink, _services.Config.Terminal);
            IsDirty = false;
            // The WinUI shell builds its services once; the WPF app reloads in place.
            Status = "Saved. Restart the app to connect with the new settings.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[settings] Could not save settings");
            Status = $"Could not save: {ex.Message}";
        }
    }

    private IReadOnlyList<SettingsCard> Cards(IReadOnlyList<DesktopSettingField> fields, IReadOnlyList<DesktopSettingToggle> toggles) =>
        fields.Select(f => f.Card).Concat(toggles.Select(t => t.Card)).Distinct()
            .Select(card => new SettingsCard(card,
                fields.Where(f => f.Card == card).Select(f => new SettingsFieldViewModel(this, f, f.Get(_settings))).ToList(),
                toggles.Where(t => t.Card == card).Select(t => new SettingsToggleViewModel(this, t, t.Get(_settings))).ToList()))
            .ToList();
}

/// <summary>A titled group of fields and switches.</summary>
public sealed record SettingsCard(string Title, IReadOnlyList<SettingsFieldViewModel> Fields, IReadOnlyList<SettingsToggleViewModel> Toggles);

public sealed partial class SettingsFieldViewModel : ObservableObject
{
    private readonly SettingsEditViewModel _owner;
    private readonly DesktopSettingField _field;

    public SettingsFieldViewModel(SettingsEditViewModel owner, DesktopSettingField field, string value)
    {
        _owner = owner;
        _field = field;
        _value = value;
    }

    public string Label => _field.Label;
    public string Placeholder => _field.Placeholder;
    public bool Multiline => _field.Multiline;
    public double MinHeight => _field.Multiline ? 96 : 0;
    public string AutomationId => $"Settings{_field.Key}";

    [ObservableProperty] private string _value;

    partial void OnValueChanged(string value) => _owner.Update(s => _field.Set(s, value));
}

public sealed partial class SettingsToggleViewModel : ObservableObject
{
    private readonly SettingsEditViewModel _owner;
    private readonly DesktopSettingToggle _toggle;

    public SettingsToggleViewModel(SettingsEditViewModel owner, DesktopSettingToggle toggle, bool value)
    {
        _owner = owner;
        _toggle = toggle;
        _isOn = value;
    }

    public string Label => _toggle.Label;
    public string AutomationId => $"Settings{_toggle.Key}";

    [ObservableProperty] private bool _isOn;

    partial void OnIsOnChanged(bool value) => _owner.Update(s => _toggle.Set(s, value));
}
