using System.Text;
using FleetMate.Core.Services.Repos;
using FleetMate.Core.Shared;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.ViewManagement;

using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace FleetMate.WinUI.Pages.Development.Repos;

/// <summary>
/// The Files panel's editor: monospaced, a line-number gutter, undo, a find
/// bar (Ctrl+F, Enter or F3 for next, Shift+F3 for previous, Esc to close),
/// Ctrl+S to save, Tab inserts a tab, and light syntax colouring.
///
/// The text box does the editing and draws the text. The colouring is a text
/// layer over it in the same font, kept on the same scroll offsets, that draws
/// only the coloured tokens (everything else in it is transparent), so the
/// caret and selection stay the box's own. Past <see cref="CodeHighlighter.SizeLimit"/>,
/// and in a contrast theme, the layer is dropped.
///
/// The box hands back text with carriage returns only; the document keeps the
/// line endings the file had (CRLF when it had any, else LF).
/// </summary>
public sealed partial class CodeEditor : UserControl
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private const double EditorFontSize = 12.5;

    private readonly TextBox _box;
    private readonly TextBlock _layer;
    private readonly TextBlock _gutter;
    private readonly TranslateTransform _layerShift = new();
    private readonly TranslateTransform _gutterShift = new();
    private readonly Grid _findBar;
    private readonly TextBox _findBox;
    private readonly TextBlock _findStatus;
    private readonly DispatcherQueueTimer _recolour;
    private readonly AccessibilitySettings _accessibility = new();
    private ScrollViewer? _viewer;
    private RepoEditorDocument? _document;
    private CodeLanguage _language = CodeLanguage.PlainText;
    private string _newline = "\n";
    private bool _loading;
    private int _lineCount = -1;

    /// <summary>Raised as the text changes, after <see cref="RepoEditorDocument.Text"/> is updated.</summary>
    public event Action? TextEdited;

    /// <summary>Ctrl+S.</summary>
    public event Action? SaveRequested;

    public CodeEditor()
    {
        _box = new TextBox
        {
            AcceptsReturn = true,
            FontFamily = Mono,
            FontSize = EditorFontSize,
            TextWrapping = TextWrapping.NoWrap,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4, 6, 4),
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            MinHeight = 0,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_box, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_box, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(_box, "Editor");
        AutomationProperties.SetAutomationId(_box, "ReposEditor");
        _box.TextChanged += OnTextChanged;
        _box.Loaded += (_, _) => HookScroll();
        _box.SizeChanged += (_, _) => _calibrated = false;
        // The box's character positions exist only after it lays the text out.
        _box.LayoutUpdated += (_, _) => { if (!_calibrated) _calibrated = Calibrate(); };
        _box.PreviewKeyDown += OnEditorKey;

        _layer = new TextBlock
        {
            FontFamily = Mono,
            FontSize = EditorFontSize,
            RenderTransform = _layerShift,
            IsHitTestVisible = false,
            TextWrapping = TextWrapping.NoWrap,
        };
        var layerHost = new Canvas { IsHitTestVisible = false };
        layerHost.Children.Add(_layer);
        layerHost.SizeChanged += (_, e) => layerHost.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };

        _gutter = new TextBlock
        {
            FontFamily = Mono,
            FontSize = EditorFontSize,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(8, 0, 8, 0),
            RenderTransform = _gutterShift,
            Foreground = RepoBrushes.Secondary,
            IsHitTestVisible = false,
        };
        AutomationProperties.SetAccessibilityView(_gutter, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var gutterHost = new Border
        {
            Child = _gutter,
            MinWidth = 40,
            Background = DevelopmentUi.Theme("LayerFillColorDefaultBrush"),
            BorderBrush = DevelopmentUi.Theme("DividerStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
        gutterHost.SizeChanged += (_, e) => gutterHost.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };

        var editorArea = new Grid();
        editorArea.Children.Add(_box);
        editorArea.Children.Add(layerHost);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(editorArea, 1);
        body.Children.Add(gutterHost);
        body.Children.Add(editorArea);

        _findBox = new TextBox { PlaceholderText = "Find", MinWidth = 200 };
        AutomationProperties.SetName(_findBox, "Find in file");
        AutomationProperties.SetAutomationId(_findBox, "ReposEditorFind");
        _findBox.KeyDown += OnFindKey;
        _findStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = RepoBrushes.Secondary,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
        var previous = IconButton("\uE70E", "Previous match (Shift+F3)", () => FindNext(backwards: true));
        var next = IconButton("\uE70D", "Next match (Enter or F3)", () => FindNext(backwards: false));
        var close = new Button { Content = "Done" };
        AutomationProperties.SetAutomationId(close, "ReposEditorFindDone");
        close.Click += (_, _) => HideFind();
        _findBar = new Grid { Padding = new Thickness(6, 4, 6, 4), ColumnSpacing = 6, Visibility = Visibility.Collapsed,
            Background = DevelopmentUi.Theme("LayerFillColorDefaultBrush") };
        foreach (var width in new[] { new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto })
            _findBar.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        UIElement[] parts = { _findBox, previous, next, _findStatus, close };
        for (var i = 0; i < parts.Length; i++)
        {
            Grid.SetColumn((FrameworkElement)parts[i], i);
            _findBar.Children.Add(parts[i]);
        }

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        root.Children.Add(_findBar);
        root.Children.Add(body);
        Content = root;

        _recolour = DispatcherQueue.CreateTimer();
        _recolour.Interval = TimeSpan.FromMilliseconds(160);
        _recolour.IsRepeating = false;
        _recolour.Tick += (_, _) => Recolour();
        // A contrast theme change reaches the element as a theme change (AccessibilitySettings' own event is not raised in desktop apps).
        ActualThemeChanged += (_, _) => Recolour();
    }

    private static Button IconButton(string glyph, string name, Action action)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 12 }, Padding = new Thickness(8, 6, 8, 6) };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => action();
        return button;
    }

    public bool IsReadOnly => _box.IsReadOnly;

    /// <summary>Shows <paramref name="document"/>, or nothing.</summary>
    public void Show(RepoEditorDocument? document)
    {
        _document = document;
        _loading = true;
        try
        {
            var text = document?.Text ?? "";
            _newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            _box.Text = text;
            _box.IsReadOnly = document?.IsReadOnly ?? true;
            _language = document == null ? CodeLanguage.PlainText : CodeLanguages.Detect(document.Path, document.Text);
            _box.Select(0, 0);
            _viewer?.ChangeView(0, 0, null, disableAnimation: true);
        }
        finally
        {
            _loading = false;
        }
        _lineCount = -1;
        UpdateGutter();
        Recolour();
        _calibrated = false;
        Reveal();
    }

    /// <summary>Scrolls to the document's reveal line and selects it.</summary>
    public void Reveal()
    {
        if (_document?.RevealLine is not { } line) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var text = _box.Text;
            var start = 0;
            for (var i = 1; i < line && start >= 0; i++)
            {
                start = text.IndexOf('\r', start);
                if (start >= 0) start++;
            }
            if (start < 0) start = text.Length;
            var end = text.IndexOf('\r', start);
            _box.Focus(FocusState.Programmatic);
            _box.Select(start, (end < 0 ? text.Length : end) - start);
            ScrollTo(start);
        });
    }

    public void FocusEditor() => _box.Focus(FocusState.Programmatic);

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateGutter();
        if (_loading || _document == null) return;
        _document.Text = _box.Text.Replace("\r", _newline, StringComparison.Ordinal);
        TextEdited?.Invoke();
        _recolour.Stop();
        _recolour.Start();
    }

    private void UpdateGutter()
    {
        var text = _box.Text;
        var lines = 1;
        foreach (var c in text) if (c == '\r') lines++;
        if (lines == _lineCount) return;
        _lineCount = lines;
        var builder = new StringBuilder(lines * 5);
        for (var i = 1; i <= lines; i++)
        {
            builder.Append(i);
            if (i < lines) builder.Append('\n');
        }
        _gutter.Text = builder.ToString();
    }

    /// <summary>Redraws the colour layer: coloured tokens over the box's own text, the rest transparent.</summary>
    private void Recolour()
    {
        _layer.Inlines.Clear();
        var text = _box.Text;
        var coloured = _language != CodeLanguage.PlainText && text.Length <= CodeHighlighter.SizeLimit && !_accessibility.HighContrast;
        _layer.Visibility = DevelopmentUi.Show(coloured);
        if (!coloured) return;
        // The tokenizer works on newline-separated text; '\r' to '\n' keeps every offset.
        var source = text.Replace('\r', '\n');
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var position = 0;
        foreach (var token in CodeHighlighter.Tokens(source, _language))
        {
            if (token.Start < position) continue;
            if (token.Start > position) _layer.Inlines.Add(new Run { Text = Layer(text, position, token.Start - position), Foreground = clear });
            _layer.Inlines.Add(new Run { Text = Layer(text, token.Start, token.Length), Foreground = TokenBrush(token.Kind) });
            position = token.Start + token.Length;
        }
        if (position < text.Length) _layer.Inlines.Add(new Run { Text = Layer(text, position, text.Length - position), Foreground = clear });
    }

    /// <summary>A slice of the box's text as the layer draws it: one line break per line, as the box does.</summary>
    private static string Layer(string text, int start, int length) => text.Substring(start, length).Replace('\r', '\n');

    private Brush TokenBrush(CodeTokenKind kind) => (kind, ActualTheme == ElementTheme.Dark) switch
    {
        (CodeTokenKind.Comment, false) => Solid(0x3B, 0x7A, 0x2A),
        (CodeTokenKind.Comment, true) => Solid(0x7C, 0xB8, 0x66),
        (CodeTokenKind.String, false) => Solid(0xA3, 0x4E, 0x12),
        (CodeTokenKind.String, true) => Solid(0xE0, 0x95, 0x5A),
        (CodeTokenKind.Keyword, false) => Solid(0x1F, 0x5F, 0xC4),
        (CodeTokenKind.Keyword, true) => Solid(0x6E, 0xA6, 0xF5),
        (_, false) => Solid(0x7A, 0x45, 0xB0),
        _ => Solid(0xB8, 0x92, 0xE6),
    };

    private static SolidColorBrush Solid(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(0xFF, r, g, b));

    /// <summary>The layer and gutter follow the box's scroll offsets, and sit where its text starts.</summary>
    private void HookScroll()
    {
        if (_viewer != null) return;
        _viewer = Find<ScrollViewer>(_box);
        if (_viewer == null) return;
        _viewer.ViewChanging += (_, e) => Shift(e.NextView.HorizontalOffset, e.NextView.VerticalOffset);
        _viewer.ViewChanged += (_, _) => Shift(_viewer.HorizontalOffset, _viewer.VerticalOffset);
        Shift(0, 0);
    }

    private void Shift(double x, double y)
    {
        _layerShift.X = _origin.X - x;
        _layerShift.Y = _origin.Y - y;
        _gutterShift.Y = _origin.Y - y;
    }

    /// <summary>Where the box's text starts (in its content, before scrolling) and its line height.</summary>
    private Windows.Foundation.Point _origin = new(6, 4);

    /// <summary>
    /// Measures where the box draws its first character and how far apart its
    /// lines are, and puts the colour layer and the gutter on exactly that grid.
    /// </summary>
    private bool Calibrate()
    {
        var text = _box.Text;
        if (text.Length == 0 || _box.ActualWidth <= 0) return false;
        var x = _viewer?.HorizontalOffset ?? 0;
        var y = _viewer?.VerticalOffset ?? 0;
        var first = _box.GetRectFromCharacterIndex(0, false);
        if (first.Height <= 0) return false;
        // Average over the furthest line that has text (an empty line has no glyph
        // to measure), so the grid stays exact all the way down a long file.
        var (line, start) = LastTextLine(text);
        if (line > 0)
        {
            var lineHeight = (_box.GetRectFromCharacterIndex(start, false).Top - first.Top) / line;
            if (lineHeight <= 4) return false;
            foreach (var block in new[] { _layer, _gutter })
            {
                block.LineHeight = lineHeight;
                block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            }
        }
        // The rectangle is measured inside the box's padding.
        _origin = new Windows.Foundation.Point(first.Left + x + _box.Padding.Left, first.Top + y + _box.Padding.Top);
        Shift(x, y);
        return true;
    }

    private bool _calibrated;

    /// <summary>The zero-based number and start index of the last line that begins with a character, or (0, 0).</summary>
    private static (int Line, int Start) LastTextLine(string text)
    {
        var line = 0;
        var best = (0, 0);
        for (var i = 0; i < text.Length; i++)
        {
            if (i > 0 && text[i - 1] == '\r') line++;
            if ((i == 0 || text[i - 1] == '\r') && text[i] != '\r') best = (line, i);
        }
        return best;
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (Find<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private void ScrollTo(int index)
    {
        if (_viewer == null) return;
        var rect = _box.GetRectFromCharacterIndex(Math.Clamp(index, 0, Math.Max(0, _box.Text.Length - 1)), false);
        var top = _viewer.VerticalOffset + rect.Top - _viewer.ViewportHeight / 3;
        _viewer.ChangeView(null, Math.Max(0, top), null, disableAnimation: true);
    }

    // MARK: - Keys and find

    private void OnEditorKey(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Keys.Control;
        if (ctrl && e.Key == VirtualKey.S)
        {
            SaveRequested?.Invoke();
            e.Handled = true;
        }
        else if (ctrl && e.Key == VirtualKey.F)
        {
            ShowFind();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.F3)
        {
            FindNext(backwards: Keys.Shift);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Tab && !ctrl && !Keys.Shift && !_box.IsReadOnly)
        {
            // A tab character, as in any code editor; Ctrl+Tab and Shift+Tab still move focus.
            // Replacing the selection keeps the box's own undo history.
            _box.SelectedText = "\t";
            _box.Select(_box.SelectionStart + _box.SelectionLength, 0);
            e.Handled = true;
        }
    }

    private void OnFindKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Enter or VirtualKey.F3)
        {
            FindNext(backwards: Keys.Shift);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            HideFind();
            e.Handled = true;
        }
    }

    private void ShowFind()
    {
        _findBar.Visibility = Visibility.Visible;
        if (_box.SelectionLength > 0 && !_box.SelectedText.Contains('\r')) _findBox.Text = _box.SelectedText;
        _findBox.Focus(FocusState.Keyboard);
        _findBox.SelectAll();
    }

    private void HideFind()
    {
        _findBar.Visibility = Visibility.Collapsed;
        _findStatus.Text = "";
        _box.Focus(FocusState.Keyboard);
    }

    private void FindNext(bool backwards)
    {
        var needle = _findBox.Text;
        if (needle.Length == 0) return;
        var text = _box.Text;
        int index;
        if (backwards)
        {
            var from = Math.Max(0, _box.SelectionStart - 1);
            index = from <= 0 ? -1 : text.LastIndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
            if (index < 0) index = text.LastIndexOf(needle, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var from = Math.Min(text.Length, _box.SelectionStart + _box.SelectionLength);
            index = text.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
            if (index < 0) index = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        }
        if (index < 0)
        {
            _findStatus.Text = "Not found";
            return;
        }
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase); at >= 0 && count < 10_000;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.OrdinalIgnoreCase)) count++;
        _findStatus.Text = $"{count} match{(count == 1 ? "" : "es")}";
        _box.Select(index, needle.Length);
        ScrollTo(index);
    }
}
