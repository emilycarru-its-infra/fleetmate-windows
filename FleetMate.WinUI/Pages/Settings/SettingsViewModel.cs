using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FleetMate.Core.Config;
using FleetMate.Core.Models;
using FleetMate.WinUI.Shell;

namespace FleetMate.WinUI.Pages.Settings;

/// <summary>
/// Settings › General, Authentication, Appearance and About. The rules are
/// FleetMate.Core's (<see cref="ModuleRows"/>, <see cref="UserPreferences"/>,
/// <see cref="AppAbout"/>), shared with the WPF Settings page.
///
/// In demo mode nothing is written: switches and the theme change this window
/// only, so a capture never touches the operator's settings.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ShellServices _services;
    private readonly HashSet<string> _demoHidden = new(StringComparer.OrdinalIgnoreCase);

    public SettingsViewModel(ShellServices services)
    {
        _services = services;
        _themeIndex = UserPreferences.UiTheme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        _ticketBadges = UserPreferences.TicketsBadgesEnabled(managedDefault: true);
        RefreshModules();
        RefreshAuth();
    }

    /// <summary>Raised after a module is switched on or off, so the tab bar can rebuild.</summary>
    public static event Action? ModulesChanged;

    /// <summary>Raised with Light, Dark or System when the theme changes.</summary>
    public static event Action<string>? ThemeChanged;

    public bool IsDemo => _services.DemoMode;

    /// <summary>TicketsMate has one tab, which cannot be switched off.</summary>
    public bool ShowsModules => !AppEdition.Current.IsTicketsOnly;

    // MARK: - General

    public ObservableCollection<ModuleRowViewModel> Modules { get; } = new();

    private IReadOnlySet<string> Hidden => IsDemo ? _demoHidden : UserPreferences.HiddenModules;

    internal void SetModuleShown(string tag, bool shown)
    {
        if (IsDemo)
        {
            if (shown) _demoHidden.Remove(tag);
            else if (!AppModules.Tabs.Where(t => t.Tag != tag).All(t => _demoHidden.Contains(t.Tag))) _demoHidden.Add(tag);
        }
        else
        {
            UserPreferences.SetModuleShown(tag, shown);
        }
        RefreshModules();
        ModulesChanged?.Invoke();
    }

    private void RefreshModules()
    {
        var rows = ModuleRows.Build(_services.Config, Hidden, hasEnrollmentOrganizations: false);
        Modules.Clear();
        foreach (var row in rows) Modules.Add(new ModuleRowViewModel(this, row));
    }

    /// <summary>Where the configuration is read from.</summary>
    public string ConfigurationStore => $@"HKCU\{AppEdition.Current.UserRegistryPath}";

    // MARK: - Authentication

    public ObservableCollection<AuthRow> AuthSystems { get; } = new();

    public bool HasAuthSystems => AuthSystems.Count > 0;

    public bool CanSignInToTeamDynamix => _services.TdxSession.IsEnabled;

    [RelayCommand]
    private async Task SignInToTeamDynamixAsync()
    {
        await _services.TdxSession.RetryAsync();
        RefreshAuth();
    }

    [RelayCommand]
    private void RefreshAuth()
    {
        AuthSystems.Clear();
        foreach (var system in _services.Auth.ConfiguredSystems)
            AuthSystems.Add(new AuthRow(system.SystemId.DisplayName(), system.State.StatusLabel,
                system.State.User ?? system.User));
        OnPropertyChanged(nameof(HasAuthSystems));
    }

    // MARK: - Appearance

    [ObservableProperty]
    private int _themeIndex;

    partial void OnThemeIndexChanged(int value)
    {
        var theme = value switch { 1 => "Light", 2 => "Dark", _ => "System" };
        if (!IsDemo) UserPreferences.SetUiTheme(theme);
        ThemeChanged?.Invoke(theme);
    }

    [ObservableProperty]
    private bool _ticketBadges;

    partial void OnTicketBadgesChanged(bool value)
    {
        if (!IsDemo) UserPreferences.SetTicketsBadgesEnabled(value);
    }

    // MARK: - About

    public string AppName => AppEdition.Current.Name;
    public string Tagline => AppAbout.Tagline(AppEdition.Current);
    public string Repository => AppAbout.Repository;
    public Uri RepositoryUri => new("https://" + AppAbout.Repository);
    public string Version => AppAbout.Version(typeof(App).Assembly);
    public string Platform => AppAbout.Platform;
    public string Runtime => AppAbout.Runtime;
    public IReadOnlyList<RelatedProject> RelatedProjects { get; } =
        AppAbout.RelatedProjects.Select(p => new RelatedProject(p.Name, p.Description, new Uri(p.Url))).ToList();
    public string AuthorName => AppAbout.AuthorName;
    public string AuthorDetails => string.Join(Environment.NewLine, AppAbout.AuthorLines);
    public IReadOnlyList<RelatedProject> AuthorLinks { get; } =
        AppAbout.AuthorLinks.Select(l => new RelatedProject(l.Label, l.Display, new Uri(l.Url))).ToList();

    [RelayCommand]
    private void CopyVersionInfo()
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(AppAbout.VersionInfo(AppEdition.Current, typeof(App).Assembly));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }
}

/// <summary>One module switch.</summary>
public sealed partial class ModuleRowViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;

    public ModuleRowViewModel(SettingsViewModel owner, ModuleRow row)
    {
        _owner = owner;
        Row = row;
        _shown = row.Shown;
    }

    public ModuleRow Row { get; }
    public string Title => Row.Title;
    public string Subtitle => Row.Subtitle;
    public string Glyph => Row.Glyph;
    public string ToggleTip => Row.ToggleTip;
    public string AutomationId => $"SettingsModule{Row.Tag}";

    [ObservableProperty]
    private bool _shown;

    partial void OnShownChanged(bool value)
    {
        if (value != Row.Shown) _owner.SetModuleShown(Row.Tag, value);
    }
}

/// <summary>A system's sign-in state, as Settings › Authentication lists it.</summary>
public sealed record AuthRow(string Name, string Status, string? Account)
{
    public string Detail => Account is { Length: > 0 } ? $"{Status} · {Account}" : Status;
}

/// <summary>A linked project or profile in About.</summary>
public sealed record RelatedProject(string Name, string Description, Uri Url);
