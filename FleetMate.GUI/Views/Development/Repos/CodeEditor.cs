using System.Windows.Controls.Primitives;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FleetMate.Core.Shared;

namespace FleetMate.GUI.Views.Development.Repos;

/// <summary>
/// The Files panel's editor: monospaced, a line-number gutter, undo, a find
/// bar (Ctrl+F, Enter or F3 for next, Shift+F3 for previous, Esc to close),
/// Ctrl+S to save, and light syntax colouring.
///
/// The text box does the editing; the colouring is a text layer drawn
/// underneath it in the same font and kept on the same scroll offsets, with the
/// box's own glyphs transparent. Past <see cref="CodeHighlighter.SizeLimit"/>
/// the layer is dropped and the box draws its text itself.
/// </summary>
public sealed class CodeEditor : UserControl
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private const double EditorFontSize = 12.5;

    private readonly TextBox _box;
    private readonly TextBlock _layer;
    private readonly TextBlock _gutter;
    private readonly TranslateTransform _layerShift = new();
    private readonly TranslateTransform _gutterShift = new();
    private readonly Border _findBar;
    private readonly TextBox _findBox;
    private readonly TextBlock _findStatus;
    private readonly DispatcherTimer _recolour = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private RepoEditorDocument? _document;
    private CodeLanguage _language = CodeLanguage.PlainText;
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
            AcceptsTab = true,
            FontFamily = Mono,
            FontSize = EditorFontSize,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            Template = PlainTemplate(),
            IsUndoEnabled = true,
            UndoLimit = 500,
            IsInactiveSelectionHighlightEnabled = true,
        };
        AutomationPropertiesName(_box, "Editor");
        _box.SetResourceReference(TextBoxBase.CaretBrushProperty, "SystemControlForegroundBaseHighBrush");
        _box.TextChanged += OnTextChanged;
        _box.Loaded += (_, _) => HookScroll();
        _box.PreviewKeyDown += OnEditorKey;

        // TextBox draws its text two pixels in from its edge; the layer matches.
        _layer = new TextBlock
        {
            FontFamily = Mono,
            FontSize = EditorFontSize,
            Margin = new Thickness(2, 0, 2, 0),
            RenderTransform = _layerShift,
            IsHitTestVisible = false,
        };
        _layer.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseHighBrush");
        var layerHost = new Canvas { ClipToBounds = true, IsHitTestVisible = false };
        layerHost.Children.Add(_layer);

        _gutter = new TextBlock
        {
            FontFamily = Mono,
            FontSize = EditorFontSize,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(6, 0, 8, 0),
            RenderTransform = _gutterShift,
        };
        _gutter.SetResourceReference(TextBlock.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        var gutterHost = new Border { ClipToBounds = true, Child = _gutter, MinWidth = 36 };
        gutterHost.SetResourceReference(Border.BackgroundProperty, "SubtleFillBrush");

        var editorArea = new Grid();
        editorArea.Children.Add(layerHost);
        editorArea.Children.Add(_box);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(editorArea, 1);
        body.Children.Add(gutterHost);
        body.Children.Add(editorArea);

        _findBox = new TextBox { MinWidth = 220, Margin = new Thickness(0, 0, 6, 0) };
        AutomationPropertiesName(_findBox, "Find");
        _findBox.PreviewKeyDown += OnFindKey;
        _findStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, FontSize = 11, Margin = new Thickness(6, 0, 6, 0) };
        var previous = new Button { Content = "↑", Padding = new Thickness(8, 2, 8, 2), ToolTip = "Previous (Shift+F3)" };
        previous.Click += (_, _) => FindNext(backwards: true);
        var next = new Button { Content = "↓", Padding = new Thickness(8, 2, 8, 2), ToolTip = "Next (Enter or F3)", Margin = new Thickness(4, 0, 0, 0) };
        next.Click += (_, _) => FindNext(backwards: false);
        var close = new Button { Content = "Done", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        close.Click += (_, _) => HideFind();
        var findRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 4) };
        findRow.Children.Add(_findBox);
        findRow.Children.Add(previous);
        findRow.Children.Add(next);
        findRow.Children.Add(_findStatus);
        findRow.Children.Add(close);
        _findBar = new Border { Child = findRow, Visibility = Visibility.Collapsed };
        _findBar.SetResourceReference(Border.BackgroundProperty, "SubtleFillBrush");

        var root = new DockPanel();
        DockPanel.SetDock(_findBar, Dock.Top);
        root.Children.Add(_findBar);
        root.Children.Add(body);
        Content = root;

        _recolour.Tick += (_, _) =>
        {
            _recolour.Stop();
            Recolour();
        };
    }

    private static void AutomationPropertiesName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    /// <summary>A text box with nothing but its scrolling host, so the layer underneath lines up exactly.</summary>
    private static ControlTemplate PlainTemplate()
    {
        var template = new ControlTemplate(typeof(TextBox));
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(ScrollViewer.FocusableProperty, false);
        template.VisualTree = host;
        return template;
    }

    public bool IsReadOnly => _box.IsReadOnly;

    /// <summary>Shows <paramref name="document"/>, or nothing.</summary>
    public void Show(RepoEditorDocument? document)
    {
        _document = document;
        _loading = true;
        try
        {
            _box.Text = document?.Text ?? "";
            _box.IsReadOnly = document?.IsReadOnly ?? true;
            _language = document == null ? CodeLanguage.PlainText : CodeLanguages.Detect(document.Path, document.Text);
            _box.CaretIndex = 0;
            _box.ScrollToHome();
        }
        finally
        {
            _loading = false;
        }
        _lineCount = -1;
        UpdateGutter();
        Recolour();
        Reveal();
    }

    /// <summary>Scrolls to the document's reveal line and selects it.</summary>
    public void Reveal()
    {
        if (_document?.RevealLine is not { } line) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var index = Math.Clamp(line - 1, 0, Math.Max(0, _box.LineCount - 1));
            var start = _box.GetCharacterIndexFromLineIndex(index);
            if (start < 0) return;
            _box.Focus();
            _box.Select(start, Math.Max(0, _box.GetLineLength(index) - 1));
            _box.ScrollToLine(index);
        });
    }

    public void FocusEditor() => _box.Focus();

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateGutter();
        if (_loading || _document == null) return;
        _document.Text = _box.Text;
        TextEdited?.Invoke();
        _recolour.Stop();
        _recolour.Start();
    }

    private void UpdateGutter()
    {
        var lines = Math.Max(1, _box.Text.Count(c => c == '\n') + 1);
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

    private void Recolour()
    {
        var text = _box.Text;
        var tokens = _language == CodeLanguage.PlainText ? new List<CodeToken>() : CodeHighlighter.Tokens(text, _language);
        var coloured = _language != CodeLanguage.PlainText && text.Length <= CodeHighlighter.SizeLimit;
        if (!coloured)
        {
            _layer.Inlines.Clear();
            _layer.Visibility = Visibility.Collapsed;
            _box.SetResourceReference(ForegroundProperty, "SystemControlForegroundBaseHighBrush");
            return;
        }
        _box.Foreground = Brushes.Transparent;
        _layer.Visibility = Visibility.Visible;
        _layer.Inlines.Clear();
        var position = 0;
        foreach (var token in tokens)
        {
            if (token.Start < position) continue;
            if (token.Start > position) _layer.Inlines.Add(new Run(text[position..token.Start]));
            _layer.Inlines.Add(new Run(text.Substring(token.Start, token.Length)) { Foreground = TokenBrush(token.Kind) });
            position = token.Start + token.Length;
        }
        if (position < text.Length) _layer.Inlines.Add(new Run(text[position..]));
    }

    private static Brush TokenBrush(CodeTokenKind kind) => kind switch
    {
        CodeTokenKind.Comment => CommentBrush,
        CodeTokenKind.String => StringBrush,
        CodeTokenKind.Keyword => KeywordBrush,
        _ => NumberBrush,
    };

    // Mid-tone hues that read on both the light and the dark theme.
    private static readonly Brush CommentBrush = Frozen(0x6A, 0x9A, 0x55);
    private static readonly Brush StringBrush = Frozen(0xC0, 0x6A, 0x2B);
    private static readonly Brush KeywordBrush = Frozen(0x4A, 0x7F, 0xD6);
    private static readonly Brush NumberBrush = Frozen(0x9B, 0x6B, 0xC9);

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void HookScroll()
    {
        if (_box.Template.FindName("PART_ContentHost", _box) is not ScrollViewer viewer) return;
        viewer.ScrollChanged += (_, _) =>
        {
            _layerShift.X = -viewer.HorizontalOffset;
            _layerShift.Y = -viewer.VerticalOffset;
            _gutterShift.Y = -viewer.VerticalOffset;
        };
    }

    // ── Keys and find ───────────────────────────────────────────────────

    private void OnEditorKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (ctrl && e.Key == Key.S)
        {
            SaveRequested?.Invoke();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.F)
        {
            ShowFind();
            e.Handled = true;
        }
        else if (e.Key == Key.F3)
        {
            FindNext(backwards: Keyboard.Modifiers == ModifierKeys.Shift);
            e.Handled = true;
        }
    }

    private void OnFindKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.F3)
        {
            FindNext(backwards: Keyboard.Modifiers == ModifierKeys.Shift);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideFind();
            e.Handled = true;
        }
    }

    private void ShowFind()
    {
        _findBar.Visibility = Visibility.Visible;
        if (_box.SelectionLength > 0 && !_box.SelectedText.Contains('\n')) _findBox.Text = _box.SelectedText;
        _findBox.Focus();
        _findBox.SelectAll();
    }

    private void HideFind()
    {
        _findBar.Visibility = Visibility.Collapsed;
        _findStatus.Text = "";
        _box.Focus();
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
        _box.ScrollToLine(_box.GetLineIndexFromCharacterIndex(index));
    }
}
