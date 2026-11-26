using FleetMate.Core.Knowledge;
using FleetMate.Core.Links;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using XamlInline = Microsoft.UI.Xaml.Documents.Inline;

namespace FleetMate.WinUI.Pages.Development.Shared;

/// <summary>
/// Markdown drawn natively rather than in a browser, for text other people
/// write (pull request descriptions, shared skills), as FleetMate.GUI's
/// <c>MarkdownDocument</c> draws it. No HTML engine: raw HTML shows as text,
/// nothing loads from the network, images show as their alt text, and a link
/// opens in the browser only when it is http(s). Text is selectable, and it
/// sizes to its content so it sits inside a scrolling pane.
/// </summary>
public sealed partial class MarkdownView : UserControl
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .Build();

    private const string CodeFont = "Cascadia Code, Consolas";
    private readonly StackPanel _blocks = new() { Spacing = 8 };
    private double _size = 14;

    public MarkdownView()
    {
        IsTabStop = false;
        Content = _blocks;
    }

    /// <summary>Show <paramref name="markdown"/> at <paramref name="fontSize"/>, replacing what was shown.</summary>
    public void Show(string? markdown, double fontSize = 14)
    {
        _size = fontSize;
        _blocks.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown)) return;
        foreach (var block in Markdown.Parse(markdown, Pipeline))
            if (Convert(block) is { } element) _blocks.Children.Add(element);
    }

    private FrameworkElement? Convert(MdBlock block) => block switch
    {
        HeadingBlock h => Heading(h),
        ParagraphBlock p => Text(p.Inline),
        QuoteBlock q => Quote(q),
        ListBlock l => ListOf(l),
        FencedCodeBlock or CodeBlock => Code(((LeafBlock)block).Lines.ToString().TrimEnd('\n', '\r')),
        ThematicBreakBlock => new Border { Height = 1, Background = Brush("DividerStrokeColorDefaultBrush"), Margin = new Thickness(0, 4, 0, 4) },
        MdTable t => TableOf(t),
        HtmlBlock html => Literal(html.Lines.ToString()),
        LeafBlock leaf => Literal(leaf.Lines.ToString()),
        ContainerBlock container => Stack(container),
        _ => null,
    };

    private StackPanel Stack(ContainerBlock container)
    {
        var stack = new StackPanel { Spacing = 8 };
        foreach (var child in container)
            if (Convert(child) is { } element) stack.Children.Add(element);
        return stack;
    }

    private RichTextBlock Heading(HeadingBlock heading)
    {
        var scale = heading.Level switch { 1 => 1.6, 2 => 1.35, 3 => 1.15, _ => 1.0 };
        var text = Text(heading.Inline);
        text.FontSize = _size * scale;
        text.FontWeight = FontWeights.SemiBold;
        text.Margin = new Thickness(0, heading.Level <= 2 ? 6 : 2, 0, 0);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(text, heading.Level switch
        {
            1 => Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1,
            2 => Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2,
            3 => Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level3,
            _ => Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level4,
        });
        return text;
    }

    private RichTextBlock Text(ContainerInline? inlines)
    {
        var paragraph = new Paragraph();
        if (inlines != null)
            foreach (var inline in inlines) AddInline(paragraph.Inlines, inline);
        var text = new RichTextBlock { FontSize = _size, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
        text.Blocks.Add(paragraph);
        return text;
    }

    private Border Quote(QuoteBlock quote)
    {
        var stack = Stack(quote);
        return new Border
        {
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = Brush("DividerStrokeColorDefaultBrush"),
            Padding = new Thickness(10, 2, 0, 2),
            Child = stack,
        };
    }

    private StackPanel ListOf(ListBlock list)
    {
        var stack = new StackPanel { Spacing = 2 };
        var number = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var marker = new TextBlock
            {
                Text = list.IsOrdered ? $"{number++}." : "•",
                FontSize = _size,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            row.Children.Add(marker);
            var body = Stack(item);
            body.Spacing = 2;
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            stack.Children.Add(row);
        }
        return stack;
    }

    private Border Code(string text) => new()
    {
        Background = Brush("SubtleFillColorSecondaryBrush"),
        BorderBrush = Brush("CardStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(8),
        Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = new TextBlock { Text = text, FontFamily = new FontFamily(CodeFont), FontSize = _size - 1, IsTextSelectionEnabled = true },
        },
    };

    private TextBlock Literal(string text) => new()
    {
        Text = text.TrimEnd('\n', '\r'),
        FontFamily = new FontFamily(CodeFont),
        FontSize = _size - 1,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
    };

    private Grid TableOf(MdTable table)
    {
        var grid = new Grid();
        var rows = table.OfType<MdTableRow>().ToList();
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cells = rows[r].OfType<MdTableCell>().ToList();
            for (var c = 0; c < cells.Count; c++)
            {
                var content = Stack(cells[c]);
                if (rows[r].IsHeader)
                    foreach (var text in content.Children.OfType<RichTextBlock>()) text.FontWeight = FontWeights.SemiBold;
                var border = new Border
                {
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    BorderBrush = Brush("DividerStrokeColorDefaultBrush"),
                    Padding = new Thickness(6, 2, 6, 2),
                    Child = content,
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }
        return grid;
    }

    private void AddInline(InlineCollection target, MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run { Text = literal.Content.ToString() });
                break;
            case CodeInline code:
                target.Add(new Run { Text = code.Content, FontFamily = new FontFamily(CodeFont) });
                break;
            case LineBreakInline br:
                target.Add(br.IsHard ? new LineBreak() : new Run { Text = " " });
                break;
            case TaskList task:
                target.Add(new Run { Text = task.Checked ? "☑ " : "☐ " });
                break;
            case EmphasisInline emphasis:
                Span span = emphasis.DelimiterChar == '~'
                    ? new Span { TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough }
                    : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                foreach (var child in emphasis) AddInline(span.Inlines, child);
                target.Add(span);
                break;
            case LinkInline { IsImage: true } image:
                // Images are not fetched; the alt text stands in.
                target.Add(new Run { Text = $"[image: {PlainText(image)}]" });
                break;
            case LinkInline link:
                target.Add(Link(link.Url, link));
                break;
            case AutolinkInline auto:
                target.Add(Link(auto.Url, null));
                break;
            case HtmlInline html:
                target.Add(new Run { Text = html.Tag });
                break;
            case HtmlEntityInline entity:
                target.Add(new Run { Text = entity.Transcoded.ToString() });
                break;
            case ContainerInline container:
                foreach (var child in container) AddInline(target, child);
                break;
        }
    }

    private static XamlInline Link(string? url, ContainerInline? label)
    {
        var text = label != null ? PlainText(label) : url ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !HandbookLinks.IsWeb(uri))
            return new Run { Text = text };

        // Opened by hand rather than through NavigateUri, so only http(s) ever leaves the app.
        var link = new Hyperlink();
        link.Inlines.Add(new Run { Text = text });
        ToolTipService.SetToolTip(link, uri.AbsoluteUri);
        link.Click += (_, _) => ExternalBrowser.Open(uri.AbsoluteUri);
        return link;
    }

    private static string PlainText(ContainerInline container)
    {
        var parts = new List<string>();
        foreach (var child in container)
        {
            switch (child)
            {
                case LiteralInline l: parts.Add(l.Content.ToString()); break;
                case CodeInline c: parts.Add(c.Content); break;
                case ContainerInline inner: parts.Add(PlainText(inner)); break;
            }
        }
        return string.Concat(parts);
    }

    private static Brush Brush(string key) => DevelopmentUi.Theme(key);
}
