using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FleetMate.Core.Config;

namespace FleetMate.WinUI.Views;

/// <summary>
/// First-run setup wizard (greenfield WinUI). A single ContentDialog stepping through
/// Welcome → Modules → Configure → Summary, persisting collected settings to the
/// registry via <see cref="FleetMateConfig.SaveSettings"/>. Auto-launches when nothing
/// is configured and is re-runnable from Settings.
/// </summary>
public sealed class OnboardingDialog
{
    private readonly ContentDialog _dialog;
    private int _step;
    private readonly UIElement[] _pages;

    // Module selection
    private readonly CheckBox _devices = new() { Content = "Devices (Intune / Microsoft Graph)", IsChecked = true };
    private readonly CheckBox _identity = new() { Content = "Identity (Entra groups & users)" };
    private readonly CheckBox _inventory = new() { Content = "Inventory (Snipe-IT)" };
    private readonly CheckBox _tickets = new() { Content = "Tickets (TeamDynamix)" };
    private readonly CheckBox _projects = new() { Content = "Projects (Azure DevOps)" };

    // Service fields
    private readonly TextBox _graphTenant = Field("Tenant ID");
    private readonly TextBox _graphClient = Field("Client ID");
    private readonly PasswordBox _graphSecret = new() { PlaceholderText = "Client secret (optional)" };
    private readonly TextBox _snipeUrl = Field("Snipe-IT URL (https://…)");
    private readonly TextBox _snipeKey = Field("API key (or leave blank to use a resource id)");
    private readonly TextBox _snipeResource = Field("OIDC resource id (optional, secretless)");
    private readonly TextBox _tdxUrl = Field("TeamDynamix base URL (https://…)");
    private readonly TextBox _devopsOrg = Field("Azure DevOps organization");
    private readonly TextBox _devopsProject = Field("Azure DevOps project");

    private StackPanel _graphSection = null!, _snipeSection = null!, _tdxSection = null!, _devopsSection = null!;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };

    public OnboardingDialog(XamlRoot xamlRoot)
    {
        _pages = new[] { BuildWelcome(), BuildModules(), BuildConfigure(), BuildSummary() };

        _dialog = new ContentDialog
        {
            Title = "Welcome to FleetMate",
            XamlRoot = xamlRoot,
            PrimaryButtonText = "Next",
            SecondaryButtonText = "Back",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = _pages[0],
        };
        _dialog.PrimaryButtonClick += OnPrimary;
        _dialog.SecondaryButtonClick += OnSecondary;
        UpdateChrome();
    }

    /// <summary>Show the wizard. Returns true if the user finished (saved).</summary>
    public async Task<bool> ShowAsync() => await _dialog.ShowAsync() == ContentDialogResult.Primary;

    // MARK: - Navigation

    private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_step < _pages.Length - 1)
        {
            args.Cancel = true; // don't close yet
            _step++;
            if (_step == 2) RefreshSections();
            if (_step == 3) BuildSummaryText();
            _dialog.Content = _pages[_step];
            UpdateChrome();
        }
        else
        {
            Save(); // last step → allow close (result = Primary)
        }
    }

    private void OnSecondary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_step > 0)
        {
            _step--;
            _dialog.Content = _pages[_step];
            UpdateChrome();
        }
    }

    private void UpdateChrome()
    {
        _dialog.Title = _step switch
        {
            0 => "Welcome to FleetMate",
            1 => "Choose modules",
            2 => "Configure services",
            _ => "Review & finish",
        };
        _dialog.PrimaryButtonText = _step == _pages.Length - 1 ? "Finish" : "Next";
        _dialog.IsSecondaryButtonEnabled = _step > 0;
    }

    private void RefreshSections()
    {
        var graph = _devices.IsChecked == true || _identity.IsChecked == true;
        _graphSection.Visibility = graph ? Visibility.Visible : Visibility.Collapsed;
        _snipeSection.Visibility = _inventory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _tdxSection.Visibility = _tickets.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _devopsSection.Visibility = _projects.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // MARK: - Save

    private void Save()
    {
        var values = new Dictionary<string, string?>();
        if (_devices.IsChecked == true || _identity.IsChecked == true)
        {
            Put(values, "GraphTenantId", _graphTenant.Text);
            Put(values, "GraphClientId", _graphClient.Text);
            Put(values, "GraphClientSecret", _graphSecret.Password);
        }
        if (_inventory.IsChecked == true)
        {
            Put(values, "SnipeUrl", _snipeUrl.Text);
            Put(values, "SnipeApiKey", _snipeKey.Text);
            Put(values, "SnipeResourceId", _snipeResource.Text);
        }
        if (_tickets.IsChecked == true) Put(values, "TdxBaseUrl", _tdxUrl.Text);
        if (_projects.IsChecked == true)
        {
            Put(values, "DevOpsOrganization", _devopsOrg.Text);
            Put(values, "DevOpsProject", _devopsProject.Text);
        }

        if (values.Count > 0)
            FleetMateConfig.SaveSettings(values);
    }

    private static void Put(IDictionary<string, string?> d, string key, string? value)
    {
        var v = value?.Trim();
        if (!string.IsNullOrEmpty(v)) d[key] = v;
    }

    // MARK: - Pages

    private static UIElement BuildWelcome() => new StackPanel
    {
        Spacing = 10,
        Width = 460,
        Children =
        {
            new TextBlock { Text = "FleetMate connects your device fleet tools in one place.", TextWrapping = TextWrapping.Wrap },
            new TextBlock
            {
                Text = "This quick setup collects the credentials for the modules you use. You can change any of it later in Settings.",
                TextWrapping = TextWrapping.Wrap, Opacity = 0.75,
            },
        },
    };

    private UIElement BuildModules()
    {
        var panel = new StackPanel { Spacing = 6, Width = 460 };
        panel.Children.Add(new TextBlock { Text = "Which modules do you want to set up?", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(_devices);
        panel.Children.Add(_identity);
        panel.Children.Add(_inventory);
        panel.Children.Add(_tickets);
        panel.Children.Add(_projects);
        return panel;
    }

    private UIElement BuildConfigure()
    {
        _graphSection = Section("Microsoft Graph (Devices / Identity)", _graphTenant, _graphClient, _graphSecret);
        _snipeSection = Section("Snipe-IT (Inventory)", _snipeUrl, _snipeKey, _snipeResource);
        _tdxSection = Section("TeamDynamix (Tickets)", _tdxUrl);
        _devopsSection = Section("Azure DevOps (Projects)", _devopsOrg, _devopsProject);

        var stack = new StackPanel { Spacing = 16, Width = 460 };
        stack.Children.Add(_graphSection);
        stack.Children.Add(_snipeSection);
        stack.Children.Add(_tdxSection);
        stack.Children.Add(_devopsSection);

        return new ScrollViewer { Content = stack, MaxHeight = 420, HorizontalScrollMode = ScrollMode.Disabled };
    }

    private UIElement BuildSummary()
    {
        var panel = new StackPanel { Spacing = 10, Width = 460 };
        panel.Children.Add(_summary);
        panel.Children.Add(new TextBlock
        {
            Text = "Settings are saved to the FleetMate registry. Sign in with az / gh where needed, then use each tab's Refresh (or Settings → Refresh All) to connect.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12,
        });
        return panel;
    }

    private void BuildSummaryText()
    {
        var lines = new List<string>();
        if (_devices.IsChecked == true) lines.Add("• Devices");
        if (_identity.IsChecked == true) lines.Add("• Identity");
        if (_inventory.IsChecked == true) lines.Add("• Inventory");
        if (_tickets.IsChecked == true) lines.Add("• Tickets");
        if (_projects.IsChecked == true) lines.Add("• Projects");
        _summary.Text = lines.Count == 0
            ? "No modules selected — nothing will be saved."
            : "You're about to configure:\n" + string.Join("\n", lines);
    }

    // MARK: - Builders

    private static TextBox Field(string placeholder) => new() { PlaceholderText = placeholder };

    private static StackPanel Section(string title, params FrameworkElement[] fields)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var f in fields) panel.Children.Add(f);
        return panel;
    }
}
