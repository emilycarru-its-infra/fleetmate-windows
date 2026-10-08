using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using FleetMate.Core.Config;
using FleetMate.Core.Models;
using FleetMate.Core.Services;
using Microsoft.Win32;
using Serilog;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// The guided setup, as on the macOS client: Welcome, Modules, Connections,
/// Sign In, Summary. It opens by itself on a first launch with nothing set
/// up, and from Settings › General › Setup Wizard at any time. Connections
/// writes the same non-secret endpoints Settings does, to the same values in
/// HKCU\SOFTWARE\FleetMate; there is never a secret to enter. Sign In runs
/// az login and gh auth login; FleetMate shows no sign-in window of its own.
/// </summary>
public sealed class SetupWizardWindow : Window
{
    private const string RegistryPath = @"SOFTWARE\FleetMate";
    private static readonly string[] StepTitles = { "Welcome", "Modules", "Connections", "Sign In", "Summary" };

    private readonly FleetMateConfig _config;
    private readonly HashSet<string> _hidden;
    private readonly Dictionary<string, TextBox> _fields = new();
    private readonly Dictionary<string, string> _values = new();
    private readonly ContentControl _body = new();
    private readonly TextBlock _stepLabel = new() { FontSize = 12 };
    private readonly Button _back = new() { Content = "Back", Padding = new Thickness(16, 6, 16, 6) };
    private readonly Button _next = new() { Content = "Next", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _hint = new() { FontSize = 11, Foreground = new SolidColorBrush(Colors.DarkOrange), VerticalAlignment = VerticalAlignment.Center };
    private int _step;
    private string _azStatus = "";
    private string _ghStatus = "";

    public SetupWizardWindow()
    {
        Title = "FleetMate Setup";
        Width = 640;
        Height = 600;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ModernWpf.Controls.Primitives.WindowHelper.SetUseModernWindowStyle(this, true);

        _config = (Application.Current as App)?.Config ?? FleetMateConfig.Load();
        _hidden = new HashSet<string>(UserPreferences.HiddenModules, StringComparer.OrdinalIgnoreCase);
        _values["GraphTenantId"] = _config.Graph?.TenantId ?? "";
        _values["GraphClientId"] = _config.Graph?.ClientId ?? "";
        _values["DevOpsOrganization"] = _config.AzureDevOps?.Organization ?? "";
        _values["DevOpsProject"] = _config.AzureDevOps?.Project ?? "";
        _values["SnipeUrl"] = _config.SnipeUrl ?? "";
        _values["TdxBaseUrl"] = _config.Tdx?.BaseUrl ?? "";
        _values["TdxAppId"] = _config.Tdx?.AppId > 0 ? _config.Tdx.AppId.ToString() : "";
        _values["ReportMateUrl"] = _config.ReportMateUrl ?? "";

        var footer = new DockPanel { Margin = new Thickness(24, 12, 24, 18), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_hint);
        _back.Click += (_, _) => Go(_step - 1);
        _next.Click += (_, _) => { if (_step == StepTitles.Length - 1) Finish(); else Go(_step + 1); };

        var root = new DockPanel();
        _stepLabel.Margin = new Thickness(24, 18, 24, 0);
        _stepLabel.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        DockPanel.SetDock(_stepLabel, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(_stepLabel);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(24, 8, 24, 0),
            Content = _body,
        });
        root.SetResourceReference(Panel.BackgroundProperty, "AppBackgroundBrush");
        root.SetResourceReference(TextElement.ForegroundProperty, "SystemControlForegroundBaseHighBrush");
        Content = root;
        Go(0);
    }

    /// <summary>
    /// A first launch needs the wizard when nothing FleetMate reads from has
    /// an endpoint yet and the operator has never finished or skipped it.
    /// Policy-supplied endpoints count, so a managed PC never sees it.
    /// </summary>
    public static bool NeededAtLaunch(FleetMateConfig config) =>
        !UserPreferences.SetupCompleted
        && string.IsNullOrWhiteSpace(config.AzureDevOps?.Organization)
        && string.IsNullOrWhiteSpace(config.SnipeUrl)
        && string.IsNullOrWhiteSpace(config.Tdx?.BaseUrl);

    private bool Shows(string tag) => !_hidden.Contains(tag);

    private void Go(int step)
    {
        CaptureFields();
        _step = Math.Clamp(step, 0, StepTitles.Length - 1);
        _stepLabel.Text = $"Step {_step + 1} of {StepTitles.Length} · {StepTitles[_step]}";
        _back.IsEnabled = _step > 0;
        _next.Content = _step == StepTitles.Length - 1 ? "Finish" : "Next";
        if (_step == StepTitles.Length - 1) _next.Style = (Style)FindResource("AccentButtonStyle");
        else _next.ClearValue(StyleProperty);
        _hint.Text = "";
        _body.Content = _step switch
        {
            0 => WelcomeStep(),
            1 => ModulesStep(),
            2 => ConnectionsStep(),
            3 => SignInStep(),
            _ => SummaryStep(),
        };
    }

    // ── Steps ────────────────────────────────────────────────────────────

    private UIElement WelcomeStep()
    {
        var panel = Heading("Welcome to FleetMate",
            "FleetMate brings devices, inventory, projects, tickets and reporting together. This takes a minute: choose the tabs you want, point them at your services, and sign in to az and gh.");
        panel.Children.Add(Note("FleetMate never stores a password or client secret. Services sign in with your Windows account, az or gh."));
        return panel;
    }

    private UIElement ModulesStep()
    {
        var panel = Heading("Choose Your Modules", "Select the tabs you want. You can change this later in Settings › General.");
        foreach (var module in AppModules.All)
        {
            var box = new CheckBox { IsChecked = Shows(module.Tag), Margin = new Thickness(0, 6, 0, 0) };
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = module.Title, FontWeight = FontWeights.SemiBold });
            label.Children.Add(Note(module.Subtitle, top: 0));
            box.Content = label;
            var tag = module.Tag;
            box.Click += (_, _) =>
            {
                if (box.IsChecked == true) _hidden.Remove(tag);
                else _hidden.Add(tag);
                UpdateModulesHint();
            };
            panel.Children.Add(box);
        }
        UpdateModulesHint();
        return panel;
    }

    private void UpdateModulesHint()
    {
        var none = AppModules.All.All(m => _hidden.Contains(m.Tag));
        _hint.Text = none ? "Select at least one module to continue." : "";
        _next.IsEnabled = !none;
    }

    private UIElement ConnectionsStep()
    {
        var panel = Heading("Connections", "Endpoints for the modules you chose. Only addresses and identifiers go here; there is no secret to enter.");
        _fields.Clear();
        if (Shows("Devices") || Shows("Identity"))
        {
            Section(panel, "Microsoft Graph");
            Field(panel, "GraphTenantId", "Tenant ID");
            Field(panel, "GraphClientId", "Client ID");
        }
        if (Shows("Projects") || Shows("Development"))
        {
            Section(panel, "Azure DevOps");
            Field(panel, "DevOpsOrganization", "Organization");
            Field(panel, "DevOpsProject", "Project");
        }
        if (Shows("Inventory"))
        {
            Section(panel, "Snipe-IT");
            Field(panel, "SnipeUrl", "Base URL");
        }
        if (Shows("Tickets"))
        {
            Section(panel, "TeamDynamix");
            Field(panel, "TdxBaseUrl", "Base URL");
            Field(panel, "TdxAppId", "Ticketing App ID");
        }
        if (Shows("Reporting"))
        {
            Section(panel, "ReportMate");
            Field(panel, "ReportMateUrl", "API URL");
        }
        if (_fields.Count == 0)
            panel.Children.Add(Note("The modules you chose need no endpoints."));
        return panel;
    }

    private UIElement SignInStep()
    {
        var panel = Heading("Sign In", "az is the sign-in Azure DevOps and the elevation sessions use; gh backs the GitHub inbox and issues. Each opens your browser; FleetMate never asks for a password.");

        var az = new Button { Content = "az login", Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        az.ToolTip = CliSignIn.AzLoginCommandDescription(Value("GraphTenantId"));
        var azStatus = Note(_azStatus);
        az.Click += async (_, _) =>
        {
            az.IsEnabled = false;
            azStatus.Text = "Finish signing in in your browser…";
            var outcome = await CliSignIn.AzLoginAsync(Value("GraphTenantId"));
            _azStatus = outcome.Message;
            azStatus.Text = _azStatus;
            az.IsEnabled = true;
        };
        panel.Children.Add(az);
        panel.Children.Add(azStatus);

        var gh = new Button { Content = "gh auth login", Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        gh.ToolTip = $"Opens a console running {CliSignIn.GhLoginCommand}";
        var ghStatus = Note(_ghStatus);
        gh.Click += (_, _) =>
        {
            _ghStatus = CliSignIn.GhLoginInConsole().Message;
            ghStatus.Text = _ghStatus;
        };
        panel.Children.Add(gh);
        panel.Children.Add(ghStatus);

        panel.Children.Add(Note("Both are optional here; Settings › Authentication offers them again, with a Re-check on every card.", top: 18));
        return panel;
    }

    private UIElement SummaryStep()
    {
        var panel = Heading("Summary", "Finish saves these choices and reloads FleetMate's services.");
        var shown = AppModules.All.Where(m => Shows(m.Tag)).Select(m => m.Title).ToList();
        Section(panel, "Tabs");
        panel.Children.Add(Note(string.Join(", ", shown), top: 2));
        var endpoints = _fields.Keys.Concat(_values.Keys).Distinct()
            .Where(k => _fields.ContainsKey(k) && !string.IsNullOrWhiteSpace(Value(k)))
            .Select(k => $"{FieldLabel(k)}: {Value(k)}")
            .ToList();
        Section(panel, "Endpoints");
        panel.Children.Add(Note(endpoints.Count > 0 ? string.Join(Environment.NewLine, endpoints) : "None entered.", top: 2));
        if (!string.IsNullOrEmpty(_azStatus) || !string.IsNullOrEmpty(_ghStatus))
        {
            Section(panel, "Sign-in");
            if (!string.IsNullOrEmpty(_azStatus)) panel.Children.Add(Note("az: " + _azStatus, top: 2));
            if (!string.IsNullOrEmpty(_ghStatus)) panel.Children.Add(Note("gh: " + _ghStatus, top: 2));
        }
        return panel;
    }

    // ── Finish ───────────────────────────────────────────────────────────

    private void Finish()
    {
        CaptureFields();
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath)
                ?? throw new InvalidOperationException("Cannot open registry key");
            // Only the endpoints this run showed are written, and a blank one is
            // left as it was: the wizard never clears what Settings or policy set.
            foreach (var name in _fields.Keys)
                if (!string.IsNullOrWhiteSpace(Value(name)))
                    key.SetValue(name, Value(name));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[setup] Could not save endpoints");
            MessageBox.Show(this, $"Couldn't save the endpoints:\n{ex.Message}", "FleetMate Setup",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        UserPreferences.SetHiddenModules(_hidden);
        UserPreferences.MarkSetupCompleted();
        (Application.Current as App)?.ReloadConfiguration();
        DialogResult = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        // Closing without finishing still counts as having seen it, so a
        // first launch does not reopen it every time.
        UserPreferences.MarkSetupCompleted();
        base.OnClosed(e);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void CaptureFields()
    {
        foreach (var (name, box) in _fields) _values[name] = box.Text.Trim();
    }

    private string Value(string name) =>
        _fields.TryGetValue(name, out var box) ? box.Text.Trim() : _values.GetValueOrDefault(name, "");

    private static string FieldLabel(string name) => name switch
    {
        "GraphTenantId" => "Graph tenant",
        "GraphClientId" => "Graph client",
        "DevOpsOrganization" => "DevOps organization",
        "DevOpsProject" => "DevOps project",
        "SnipeUrl" => "Snipe-IT",
        "TdxBaseUrl" => "TeamDynamix",
        "TdxAppId" => "Ticketing app",
        "ReportMateUrl" => "ReportMate",
        _ => name,
    };

    private void Field(Panel panel, string name, string label)
    {
        var caption = new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        var box = new TextBox { Text = _values.GetValueOrDefault(name, "") };
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        _fields[name] = box;
        panel.Children.Add(caption);
        panel.Children.Add(box);
    }

    private static void Section(Panel panel, string title) =>
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 16, 0, 0) });

    private static StackPanel Heading(string title, string subtitle)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(Note(subtitle, top: 4));
        return panel;
    }

    private static TextBlock Note(string text, double top = 8)
    {
        var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        return note;
    }
}
