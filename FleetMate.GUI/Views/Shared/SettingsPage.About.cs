using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using FleetMate.Core.Config;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// Settings › About, laid out like the sibling tools' About panes and the
/// macOS client's: identity, build facts, related projects, then the author.
/// The version is the stamped release version (YYYY.MM.DD.HHMM) a release
/// build carries as its informational version; a local build reads
/// "dev (&lt;commit&gt;)".
/// </summary>
public partial class SettingsPage
{
    private const string Repository = "github.com/emilycarru-its-infra/fleetmate-windows";

    internal static readonly (string Name, string Description, string Url)[] RelatedProjects =
    {
        ("ReportMate", "Unified reporting + visibility for Mac + Windows fleets", "https://github.com/reportmate"),
        ("BootstrapMate", "Provisioning + bootstrap tooling with a DevOps-first workflow", "https://github.com/bootstrapmate"),
        ("Cimian", "Managed software deployment for MSI(X), EXE, NUPKG, and PWSH on Windows", "https://github.com/windowsadmins/cimian"),
        ("ASBMUtil", "Apple School & Business Manager CLI + GUI", "https://github.com/rodchristiansen/asbmutil"),
    };

    private static string DisplayVersion => AppVersionDisplay.For(typeof(App).Assembly);

    /// <summary>"Windows 11 Enterprise 10.0.26200", as close to what the system reports as .NET gives.</summary>
    private static string Platform => $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()})";

    private static string Runtime => RuntimeInformation.FrameworkDescription;

    private void BuildAboutPanel()
    {
        AboutPanel.Children.Clear();
        var name = AppEdition.Current.Name;

        var identity = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        identity.Children.Add(new TextBlock { Text = name, FontSize = 20, FontWeight = FontWeights.SemiBold });
        identity.Children.Add(Caption(AppEdition.Current.IsTicketsOnly
            ? "Service desk tickets for Windows"
            : "Unified fleet management for Windows", top: 2));
        identity.Children.Add(LinkLine(Repository, "https://" + Repository));
        AboutPanel.Children.Add(identity);

        var facts = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var line = 0;
        foreach (var (label, value) in new[] { ("Version", DisplayVersion), ("Platform", Platform), (".NET", Runtime) })
        {
            facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var lbl = Caption(label, top: 2);
            Grid.SetRow(lbl, line);
            var val = new TextBox
            {
                Text = value,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                MinHeight = 0,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 0),
            };
            System.Windows.Automation.AutomationProperties.SetName(val, label);
            if (label == "Version") System.Windows.Automation.AutomationProperties.SetAutomationId(val, "AboutVersionText");
            Grid.SetRow(val, line);
            Grid.SetColumn(val, 1);
            facts.Children.Add(lbl);
            facts.Children.Add(val);
            line++;
        }
        AboutPanel.Children.Add(facts);

        var copy = new MenuItem { Header = "Copy Version Info" };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText($"{name} {DisplayVersion} · {Platform} · {Runtime}"); }
            catch (Exception ex) { Serilog.Log.Debug(ex, "[settings] Could not copy version info"); }
        };
        AboutPanel.ContextMenu = new ContextMenu { Items = { copy } };

        AboutPanel.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 10) });
        AboutPanel.Children.Add(SectionLabel("Related Projects"));
        foreach (var (project, description, url) in RelatedProjects)
        {
            var entry = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            var link = LinkLine(project, url);
            link.FontSize = 13;
            link.FontWeight = FontWeights.SemiBold;
            entry.Children.Add(link);
            entry.Children.Add(Caption(description, top: 1));
            AboutPanel.Children.Add(entry);
        }

        AboutPanel.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 10) });
        AboutPanel.Children.Add(SectionLabel("Author"));
        AboutPanel.Children.Add(new TextBlock { Text = "Rod Christiansen", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) });
        AboutPanel.Children.Add(Caption("Devices Administrator Lead", top: 1));
        AboutPanel.Children.Add(Caption("Vancouver, BC, Canada", top: 1));
        AboutPanel.Children.Add(Caption("Managing a fleet of 1000+ computers. Focused on infrastructure, DevOps architecture, CI/CD pipelines, and automating at scale.", top: 1));
        var links = new WrapPanel { Margin = new Thickness(0, 6, 0, 10) };
        foreach (var (label, display, url) in new[]
                 {
                     ("GitHub", "github.com/rodchristiansen", "https://github.com/rodchristiansen"),
                     ("Blog", "blog.focused.systems", "https://blog.focused.systems"),
                 })
        {
            var pair = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
            pair.Children.Add(new TextBlock { Text = label + " ", FontWeight = FontWeights.SemiBold });
            pair.Children.Add(LinkLine(display, url));
            links.Children.Add(pair);
        }
        AboutPanel.Children.Add(links);
    }

    private TextBlock SectionLabel(string text)
    {
        var label = Caption(text.ToUpperInvariant(), top: 0);
        label.FontWeight = FontWeights.SemiBold;
        System.Windows.Automation.AutomationProperties.SetHeadingLevel(label, System.Windows.Automation.AutomationHeadingLevel.Level2);
        return label;
    }

    private static TextBlock LinkLine(string text, string url)
    {
        var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url) };
        link.RequestNavigate += (_, e) =>
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { Serilog.Log.Debug(ex, "[settings] Could not open {Url}", e.Uri); }
            e.Handled = true;
        };
        return new TextBlock(link) { FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
    }
}
