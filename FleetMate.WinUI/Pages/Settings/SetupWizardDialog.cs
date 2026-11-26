using FleetMate.Core.Config;
using FleetMate.Core.Models;
using FleetMate.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace FleetMate.WinUI.Pages.Settings;

/// <summary>
/// The guided setup, as on FleetMate for Mac and in the WPF app: Welcome,
/// Modules, Connections, the az/gh sign-in step, Summary. The steps, the
/// endpoints each module needs and how they are saved are
/// <see cref="SetupWizardPlan"/>'s. It opens by itself on a first launch
/// with nothing set up, and from Settings › General. Demo mode saves nothing.
/// </summary>
internal sealed class SetupWizardDialog : ContentDialog
{
    private readonly FleetMateConfig _config;
    private readonly bool _demo;
    private readonly HashSet<string> _hidden;
    private readonly Dictionary<string, string> _values = new();
    private readonly Dictionary<string, TextBox> _fields = new();
    private int _step;
    private CliAccount? _az;
    private CliAccount? _gh;
    private bool _accountsChecked;
    private string _azStatus = "";
    private string _ghStatus = "";

    private static string AppName => AppEdition.Current.Name;

    public SetupWizardDialog(FleetMateConfig config, bool demo)
    {
        _config = config;
        _demo = demo;
        _hidden = new HashSet<string>(demo ? new HashSet<string>() : UserPreferences.HiddenModules, StringComparer.OrdinalIgnoreCase);
        var settings = DesktopSettings.From(config);
        foreach (var field in DesktopSettingsFields.Connections) _values[field.Key] = field.Get(settings);
        Title = $"{AppName} Setup";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        PrimaryButtonClick += OnNext;
        SecondaryButtonClick += OnBack;
        Closed += (_, _) =>
        {
            // Closing without finishing still counts as having seen it, so a
            // first launch does not reopen it every time.
            if (!_demo) UserPreferences.MarkSetupCompleted();
        };
        Go(0);
    }

    /// <summary>A first launch with nothing set up and the wizard never finished or skipped.</summary>
    public static bool NeededAtLaunch(FleetMateConfig config) =>
        SetupWizardPlan.NeededAtLaunch(config, UserPreferences.SetupCompleted);

    private IReadOnlyList<string> Steps => SetupWizardPlan.Steps(AppEdition.Current, _hidden, includeRepositories: false);

    private bool Shows(string tag) => !_hidden.Contains(tag);

    private void Go(int step)
    {
        Capture();
        _step = Math.Clamp(step, 0, Steps.Count - 1);
        var title = Steps[_step];
        PrimaryButtonText = _step == Steps.Count - 1 ? "Finish" : "Next";
        SecondaryButtonText = _step > 0 ? "Back" : "";
        IsSecondaryButtonEnabled = _step > 0;
        IsPrimaryButtonEnabled = true;
        var body = title switch
        {
            SetupWizardPlan.Welcome => WelcomeStep(),
            SetupWizardPlan.Modules => ModulesStep(),
            SetupWizardPlan.Connections => ConnectionsStep(),
            SetupWizardPlan.Summary => SummaryStep(),
            _ => SignInStep(),
        };
        var root = new StackPanel { Spacing = 4, Width = 520 };
        root.Children.Add(Caption($"Step {_step + 1} of {Steps.Count} · {title}"));
        root.Children.Add(body);
        Content = new ScrollViewer { Content = root, MaxHeight = 560 };
    }

    private void OnNext(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_step < Steps.Count - 1)
        {
            args.Cancel = true;
            Go(_step + 1);
            return;
        }
        Finish();
    }

    private void OnBack(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        Go(_step - 1);
    }

    // MARK: - Steps

    private UIElement WelcomeStep()
    {
        if (AppEdition.Current.IsTicketsOnly)
            return Heading($"Welcome to {AppName}",
                $"{AppName} is your TeamDynamix service desk. This takes a minute: point it at your TeamDynamix site.",
                $"{AppName} never stores a password. It signs in with your Windows account.");
        return Heading("Welcome to FleetMate",
            "FleetMate brings devices, inventory, projects, tickets and reporting together. This takes a minute: choose the tabs you want, point them at your services, and sign in to az and gh.",
            "FleetMate never stores a password or client secret. Services sign in with your Windows account, az or gh.");
    }

    private UIElement ModulesStep()
    {
        var panel = Heading("Choose your modules", $"Pick what you want {AppName} to show. The next steps connect each one; you can change this later in Settings › General.");
        var hint = Caption("");
        foreach (var module in AppModules.All)
        {
            var box = new CheckBox { IsChecked = Shows(module.Tag), Content = $"{module.Title} — {module.Subtitle}" };
            AutomationProperties.SetAutomationId(box, $"SetupModule{module.Tag}");
            AutomationProperties.SetName(box, module.Title);
            var tag = module.Tag;
            box.Click += (_, _) =>
            {
                if (box.IsChecked == true) _hidden.Remove(tag);
                else _hidden.Add(tag);
                var none = AppModules.Tabs.All(m => _hidden.Contains(m.Tag));
                hint.Text = none ? "Select at least one tab to continue." : "";
                IsPrimaryButtonEnabled = !none;
            };
            panel.Children.Add(box);
        }
        panel.Children.Add(hint);
        return panel;
    }

    private UIElement ConnectionsStep()
    {
        var panel = Heading("Connections", "Endpoints for the modules you chose. Only addresses and identifiers go here; there is no secret to enter.");
        _fields.Clear();
        var sections = SetupWizardPlan.ConnectionSections(_hidden);
        foreach (var section in sections)
        {
            panel.Children.Add(new TextBlock { Text = section.Title, Style = Style("BodyStrongTextBlockStyle"), Margin = new Thickness(0, 12, 0, 0) });
            panel.Children.Add(Caption(section.Description));
            foreach (var field in section.Fields)
            {
                var box = new TextBox { Header = field.Label, Text = _values.GetValueOrDefault(field.Key, "") };
                AutomationProperties.SetAutomationId(box, $"Setup{field.Key}");
                _fields[field.Key] = box;
                panel.Children.Add(box);
            }
        }
        if (sections.Count == 0) panel.Children.Add(Caption("The modules you chose need no endpoints."));
        return panel;
    }

    private UIElement SignInStep()
    {
        var development = Shows("Development");
        var panel = development
            ? Heading("Development", "Repositories, pull requests and pipelines come from your own command-line sign-ins.")
            : Heading("Sign In", "az is the sign-in the elevation sessions behind Devices and Identity start from.");
        var rows = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(rows);
        var check = new Button { Content = "Check again", Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(check);
        panel.Children.Add(Caption($"{AppName} reads with your own identity and never stores a token."));

        void Render()
        {
            rows.Children.Clear();
            if (development)
                rows.Children.Add(SignInRow("GitHub CLI", _gh, "gh auth login", _ghStatus, () =>
                {
                    _ghStatus = _demo ? "Demo mode does not sign in." : CliSignIn.GhLoginInConsole().Message;
                    Render();
                    return Task.CompletedTask;
                }));
            rows.Children.Add(SignInRow("Azure CLI", _az, "az login", _azStatus, async () =>
            {
                if (_demo)
                {
                    _azStatus = "Demo mode does not sign in.";
                    Render();
                    return;
                }
                _azStatus = "Finish signing in in your browser…";
                Render();
                _azStatus = (await CliSignIn.AzLoginAsync(Value("GraphTenantId"))).Message;
                await CheckAsync();
                Render();
            }));
        }

        async Task CheckAsync()
        {
            if (_demo)
            {
                _accountsChecked = true;
                return;
            }
            var gh = CliAccountProbe.GhAccountAsync();
            var az = CliAccountProbe.AzAccountAsync();
            _gh = await gh;
            _az = await az;
            _accountsChecked = true;
        }

        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            _accountsChecked = false;
            Render();
            await CheckAsync();
            Render();
            check.IsEnabled = true;
        };
        Render();
        if (!_accountsChecked)
            DispatcherQueue.TryEnqueue(async () =>
            {
                await CheckAsync();
                Render();
            });
        return panel;
    }

    private UIElement SignInRow(string title, CliAccount? account, string action, string status, Func<Task> signIn)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var mark = new FontIcon
        {
            Glyph = account != null ? "" : "",
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[account != null ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush"],
        };
        AutomationProperties.SetName(mark, account != null ? "Signed in" : "Not signed in");
        row.Children.Add(mark);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, Style = Style("BodyStrongTextBlockStyle") });
        text.Children.Add(Caption(account != null ? $"Signed in as {account.User}" : _accountsChecked ? "Not signed in." : "Checking…"));
        if (!string.IsNullOrEmpty(status)) text.Children.Add(Caption(status));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        if (_accountsChecked && account == null)
        {
            var button = new Button { Content = action, VerticalAlignment = VerticalAlignment.Top };
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                await signIn();
            };
            Grid.SetColumn(button, 2);
            row.Children.Add(button);
        }
        return row;
    }

    private UIElement SummaryStep()
    {
        var panel = Heading("Summary", _demo
            ? "Demo mode: Finish closes the wizard without saving."
            : $"Finish saves these choices. Restart {AppName} to connect with the new endpoints.");
        panel.Children.Add(new TextBlock { Text = "Modules", Style = Style("BodyStrongTextBlockStyle"), Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(Caption(string.Join(", ", AppModules.All.Where(m => Shows(m.Tag)).Select(m => m.Title))));
        var endpoints = SetupWizardPlan.ConnectionSections(_hidden).SelectMany(s => s.Fields)
            .Where(f => !string.IsNullOrWhiteSpace(Value(f.Key)))
            .Select(f => $"{SetupWizardPlan.FieldLabel(f.Key)}: {Value(f.Key)}")
            .ToList();
        panel.Children.Add(new TextBlock { Text = "Endpoints", Style = Style("BodyStrongTextBlockStyle"), Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(Caption(endpoints.Count > 0 ? string.Join(Environment.NewLine, endpoints) : "None entered."));
        return panel;
    }

    // MARK: - Finish

    private void Finish()
    {
        Capture();
        if (_demo) return;
        try
        {
            var shown = SetupWizardPlan.ConnectionSections(_hidden).SelectMany(s => s.Fields).Select(f => f.Key).ToHashSet();
            using (var sink = new RegistrySettingsSink())
                SetupWizardPlan.SaveEndpoints(sink, _values.Where(v => shown.Contains(v.Key)));
            if (!AppEdition.Current.IsTicketsOnly) UserPreferences.SetHiddenModules(_hidden);
            UserPreferences.MarkSetupCompleted();
            SettingsViewModel.RaiseModulesChanged();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[setup] Could not save the setup choices");
        }
    }

    // MARK: - Helpers

    private void Capture()
    {
        foreach (var (key, box) in _fields) _values[key] = box.Text.Trim();
    }

    private string Value(string key) =>
        _fields.TryGetValue(key, out var box) ? box.Text.Trim() : _values.GetValueOrDefault(key, "");

    private static StackPanel Heading(string title, string subtitle, string? note = null)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = title, Style = Style("SubtitleTextBlockStyle") });
        panel.Children.Add(Caption(subtitle));
        if (note != null) panel.Children.Add(Caption(note));
        return panel;
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };

    private static Style Style(string key) => (Style)Application.Current.Resources[key];
}
