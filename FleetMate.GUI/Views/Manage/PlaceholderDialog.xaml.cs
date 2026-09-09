using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Models.Manage;

namespace FleetMate.GUI.Views.Manage;

/// <summary>
/// Prompts for each &lt;PLACEHOLDER&gt; in a template command. Secret-like
/// placeholders use a masked box and are redacted in the preview; the
/// resolved command is only produced when Run is pressed.
/// </summary>
public partial class PlaceholderDialog : Window
{
    private readonly PlaceholderTemplate _template;
    private readonly Dictionary<string, Control> _inputs = new();

    public string? ResolvedCommand { get; private set; }

    public PlaceholderDialog(PlaceholderTemplate template)
    {
        InitializeComponent();
        _template = template;
        TitleText.Text = template.Label;

        foreach (var placeholder in template.Placeholders)
        {
            var label = new TextBlock
            {
                Text = PlaceholderTemplate.FieldLabel(placeholder),
                Margin = new Thickness(0, 6, 0, 2),
                Foreground = (System.Windows.Media.Brush)FindResource("SystemControlForegroundBaseMediumBrush")
            };
            Fields.Children.Add(label);

            Control input;
            if (PlaceholderTemplate.IsSensitive(placeholder))
            {
                var box = new PasswordBox();
                box.PasswordChanged += (_, _) => UpdatePreview();
                input = box;
            }
            else
            {
                var box = new TextBox();
                box.TextChanged += (_, _) => UpdatePreview();
                input = box;
            }
            _inputs[placeholder] = input;
            Fields.Children.Add(input);
        }

        Loaded += (_, _) => { _inputs.Values.FirstOrDefault()?.Focus(); UpdatePreview(); };
    }

    private Dictionary<string, string> Values() =>
        _inputs.ToDictionary(kv => kv.Key, kv => kv.Value switch
        {
            PasswordBox p => p.Password,
            TextBox t => t.Text,
            _ => ""
        });

    private void UpdatePreview()
    {
        var values = Values();
        PreviewBox.Text = _template.Resolve(values, redactSensitive: true);
        RunButton.IsEnabled = _template.IsComplete(values);
    }

    private void OnRun(object sender, RoutedEventArgs e)
    {
        var values = Values();
        if (!_template.IsComplete(values)) return;
        ResolvedCommand = _template.Resolve(values);
        DialogResult = true;
    }
}
