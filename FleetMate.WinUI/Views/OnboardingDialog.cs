using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using FleetMate.Core.Config;

namespace FleetMate.WinUI.Views;

/// <summary>
/// First-run setup wizard (greenfield WinUI). A single ContentDialog stepping through
/// Welcome → Modules → Configure → Summary, persisting collected settings to the
/// registry via <see cref="FleetMateConfig.SaveDesktopSettings"/>. Auto-launches when nothing
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
    private readonly TextBox _snipeUrl = Field("Snipe-IT URL (https://…)");
    private readonly TextBox _snipeAudience = Field("Entra audience / application ID");
    private readonly TextBox _tdxUrl = Field("TeamDynamix base URL (https://…)");
    private readonly TextBox _tdxTicketingAppId = Field("TeamDynamix ticketing app ID");
    private readonly TextBox _devopsOrg = Field("Azure DevOps organization");
    private readonly TextBox _devopsProject = Field("Azure DevOps project");

    private StackPanel _graphSection = null!, _snipeSection = null!, _tdxSection = null!, _devopsSection = null!;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };

    public OnboardingDialog(XamlRoot xamlRoot)
    {
        var config = App.Current.Config;
        _graphTenant.Text = config.Graph?.TenantId ?? FleetMateConfig.DefaultTenantId;
        _graphClient.Text = config.Graph?.ClientId ?? "";
        _snipeUrl.Text = config.SnipeUrl ?? "";
        _snipeAudience.Text = config.SnipeOidcAudience ?? FleetMateConfig.DefaultSnipeOidcAudience;
        _tdxUrl.Text = config.Tdx?.BaseUrl ?? "";
        _tdxTicketingAppId.Text = (config.Tdx?.TicketingAppId ?? 115).ToString();
        _devopsOrg.Text = config.AzureDevOps?.Organization ?? "";
        _devopsProject.Text = config.AzureDevOps?.Project ?? "";

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
        }
        if (_inventory.IsChecked == true)
        {
            Put(values, "SnipeUrl", _snipeUrl.Text);
            Put(values, "SnipeOidcAudience", _snipeAudience.Text);
        }
        if (_tickets.IsChecked == true)
        {
            Put(values, "TdxBaseUrl", _tdxUrl.Text);
            Put(values, "TdxTicketingAppId", _tdxTicketingAppId.Text);
        }
        if (_projects.IsChecked == true)
        {
            Put(values, "DevOpsOrganization", _devopsOrg.Text);
            Put(values, "DevOpsProject", _devopsProject.Text);
        }

        if (values.Count > 0)
            FleetMateConfig.SaveDesktopSettings(values);
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
                Text = "This quick setup collects service endpoints and public application IDs. Authentication uses your Windows account; FleetMate does not store service credentials.",
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
        _graphSection = Section("Microsoft Graph (Devices / Identity)", _graphTenant, _graphClient);
        _snipeSection = Section("Snipe-IT (Inventory)", _snipeUrl, _snipeAudience);
        _tdxSection = Section("TeamDynamix (Tickets)", _tdxUrl, _tdxTicketingAppId);
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
            Text = "Non-secret settings are saved to the FleetMate registry. FleetMate uses Windows broker SSO and browser SSO, then refreshes services without a restart.",
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
