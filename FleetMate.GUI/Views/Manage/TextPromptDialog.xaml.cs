using System.Windows;
using System.Windows.Input;

namespace FleetMate.GUI.Views.Manage;

/// <summary>One-line text prompt used for naming and renaming custom groups.</summary>
public partial class TextPromptDialog : Window
{
    public string Value => ValueBox.Text.Trim();

    public TextPromptDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptLabel.Text = prompt;
        ValueBox.Text = initial;
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    /// <summary>Returns the entered text, or null when cancelled or empty.</summary>
    public static string? Ask(Window? owner, string title, string prompt, string initial)
    {
        var dialog = new TextPromptDialog(title, prompt, initial) { Owner = owner };
        return dialog.ShowDialog() == true && dialog.Value.Length > 0 ? dialog.Value : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Value.Length == 0) return;
        DialogResult = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnOk(sender, e);
    }
}
