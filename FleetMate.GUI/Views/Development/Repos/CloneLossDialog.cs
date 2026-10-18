using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace FleetMate.GUI.Views.Development.Repos;

/// <summary>
/// The warning before deleting a clone that holds work its remotes do not:
/// exactly what would be lost, one line each, and a destructive button that
/// says what it does. Cancel is the default, so Enter never deletes.
/// </summary>
internal sealed class CloneLossDialog : Window
{
    private CloneLossDialog(string name, string path, IReadOnlyList<string> losses)
    {
        Title = "Delete Local Clone";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ModernWpf.Controls.Primitives.WindowHelper.SetUseModernWindowStyle(this, true);

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
        var header = new DockPanel();
        var icon = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 22,
            Margin = new Thickness(0, 0, 12, 0),
            Foreground = RepoBrushes.Warning,
            VerticalAlignment = VerticalAlignment.Top,
        };
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(icon);
        header.Children.Add(new TextBlock
        {
            Text = $"Deleting “{name}” would lose work",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(header);
        panel.Children.Add(Muted(path, top: 6));
        panel.Children.Add(new TextBlock { Text = "This is not on any remote:", Margin = new Thickness(0, 14, 0, 4) });
        foreach (var loss in losses)
            panel.Children.Add(new TextBlock { Text = "•  " + loss, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 2, 0, 0) });
        panel.Children.Add(Muted("The folder goes to the Recycle Bin, so it can be restored from there, and FleetMate stops tracking it.", top: 14));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var delete = new Button { Content = "Move to Recycle Bin Anyway", Padding = new Thickness(14, 6, 14, 6) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(delete, "CloneLossDelete");
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0), IsDefault = true, IsCancel = true };
        delete.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(delete);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        panel.SetResourceReference(TextElement.ForegroundProperty, "SystemControlForegroundBaseHighBrush");
        var root = new Border { Child = panel };
        root.SetResourceReference(Border.BackgroundProperty, "AppBackgroundBrush");
        Content = root;
        Loaded += (_, _) => cancel.Focus();
    }

    private static TextBlock Muted(string text, double top)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0), FontSize = 12 };
        block.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        return block;
    }

    /// <summary>True only when the person chose to delete anyway.</summary>
    public static bool Confirm(Window? owner, string name, string path, IReadOnlyList<string> losses)
    {
        var dialog = new CloneLossDialog(name, path, losses);
        if (owner != null) dialog.Owner = owner;
        return dialog.ShowDialog() == true;
    }
}
