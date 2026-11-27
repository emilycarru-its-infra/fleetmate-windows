using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FleetMate.WinUI.Pages.Manage;

/// <summary>
/// One of the Manage tab's dialogs: its title, buttons and body. Shown as a
/// <see cref="ContentDialog"/>; the QA capture shows the same body in place
/// (<see cref="Preview"/>), since a dialog's popup layer is outside what a
/// capture renders. A primary or secondary handler returning false keeps the
/// dialog open, for a validation message or for adding more than once.
/// </summary>
internal abstract class ManageDialog
{
    private FrameworkElement? _body;
    private bool _primaryEnabled = true;

    protected ContentDialog? Dialog { get; private set; }

    public abstract string Title { get; }
    public virtual string PrimaryText => "OK";
    public virtual string? SecondaryText => null;
    public virtual string CloseText => "Cancel";

    public FrameworkElement Body => _body ??= Build();

    protected abstract FrameworkElement Build();

    /// <summary>Primary pressed; false keeps the dialog open.</summary>
    protected virtual bool OnPrimary() => true;

    /// <summary>Secondary pressed; false keeps the dialog open.</summary>
    protected virtual bool OnSecondary() => true;

    protected bool PrimaryEnabled
    {
        get => _primaryEnabled;
        set
        {
            _primaryEnabled = value;
            if (Dialog != null) Dialog.IsPrimaryButtonEnabled = value;
        }
    }

    public async Task<ContentDialogResult> ShowAsync(XamlRoot root)
    {
        Dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Title,
            Content = Body,
            PrimaryButtonText = PrimaryText,
            SecondaryButtonText = SecondaryText ?? "",
            CloseButtonText = CloseText,
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = _primaryEnabled,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };
        Dialog.PrimaryButtonClick += (_, e) => { if (!OnPrimary()) e.Cancel = true; };
        Dialog.SecondaryButtonClick += (_, e) => { if (!OnSecondary()) e.Cancel = true; };
        try
        {
            return await Dialog.ShowAsync();
        }
        finally
        {
            Dialog.Content = null;
            Dialog = null;
            _body = null;
        }
    }

    /// <summary>The dialog drawn in place for the QA capture: a smoke layer, the dialog card, its buttons.</summary>
    public FrameworkElement Preview()
    {
        var buttons = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 24, 0, 0) };
        var labels = new[] { PrimaryText, SecondaryText, CloseText }.Where(t => !string.IsNullOrEmpty(t)).ToList();
        for (var i = 0; i < labels.Count; i++)
        {
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            var button = new Button { Content = labels[i], HorizontalAlignment = HorizontalAlignment.Stretch };
            if (i == 0) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            Grid.SetColumn(button, i);
            buttons.Children.Add(button);
        }
        var card = new Border
        {
            Background = Brush("ContentDialogBackground"),
            BorderBrush = Brush("ContentDialogBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(24),
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = Title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], Margin = new Thickness(0, 0, 0, 12) },
                    Body,
                    buttons,
                },
            },
        };
        AutomationProperties.SetName(card, Title);
        return new Grid { Background = Brush("SmokeFillColorDefaultBrush"), Children = { card } };
    }

    // MARK: - Building blocks

    protected static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    protected static TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = Brush("TextFillColorSecondaryBrush"),
        TextWrapping = TextWrapping.Wrap,
    };

    protected static Button SmallButton(string text, Action click, string? tip = null)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 3, 10, 4) };
        button.Click += (_, _) => click();
        if (tip != null) ToolTipService.SetToolTip(button, tip);
        return button;
    }

    protected static Button IconButton(string glyph, string name, Action click)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Padding = new Thickness(6),
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => click();
        return button;
    }
}
