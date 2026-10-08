using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// Markdown drawn as a WPF document rather than in a browser, for text other
/// people write (pull request descriptions, shared skills). No HTML engine is
/// involved: raw HTML is shown as text, nothing loads from the network, and a
/// link opens in the browser only when it is http(s). It sizes to its content,
/// so it sits inside a scrolling pane the way a TextBlock does.
/// </summary>
public static class MarkdownDocument
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .Build();

    /// <summary>A read-only box showing <paramref name="markdown"/>.</summary>
    public static RichTextBox Viewer(string? markdown, double fontSize = 12)
    {
        var box = new RichTextBox
        {
            IsReadOnly = true,
            IsDocumentEnabled = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        box.SetResourceReference(Control.ForegroundProperty, "SystemControlForegroundBaseHighBrush");
        box.Document = Build(markdown, fontSize);
        return box;
    }

    public static FlowDocument Build(string? markdown, double fontSize = 12)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = fontSize,
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
        };
        if (string.IsNullOrWhiteSpace(markdown)) return document;

        var parsed = Markdown.Parse(markdown, Pipeline);
        foreach (var block in parsed)
            if (Convert(block, fontSize) is { } converted) document.Blocks.Add(converted);
        return document;
    }

    /// <summary>Plain text of <paramref name="markdown"/> as drawn, for tests.</summary>
    internal static string PlainText(string? markdown)
    {
        var document = Build(markdown);
        return new TextRange(document.ContentStart, document.ContentEnd).Text;
    }

    /// <summary>The web addresses a reader can follow in <paramref name="markdown"/>.</summary>
    internal static List<Uri> Links(string? markdown)
    {
        var found = new List<Uri>();
        void Walk(TextElementCollection<WpfBlock> blocks)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case Paragraph p: WalkInlines(p.Inlines); break;
                    case Section s: Walk(s.Blocks); break;
                    case List l: foreach (var item in l.ListItems) Walk(item.Blocks); break;
                }
            }
        }
        void WalkInlines(InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                if (inline is Hyperlink { NavigateUri: { } uri }) found.Add(uri);
                if (inline is Span span) WalkInlines(span.Inlines);
            }
        }
        Walk(Build(markdown).Blocks);
        return found;
    }

    private static WpfBlock? Convert(MdBlock block, double size) => block switch
    {
        HeadingBlock h => Heading(h, size),
        ParagraphBlock p => Para(p.Inline, new Thickness(0, 0, 0, 8)),
        QuoteBlock q => Quote(q, size),
        ListBlock l => ListOf(l, size),
        FencedCodeBlock or CodeBlock => Code((LeafBlock)block, size),
        ThematicBreakBlock => Rule(),
        Table t => TableOf(t, size),
        HtmlBlock html => Literal(html.Lines.ToString(), size),
        LeafBlock leaf => Literal(leaf.Lines.ToString(), size),
        ContainerBlock container => Section(container, size),
        _ => null,
    };

    private static Section Section(ContainerBlock container, double size)
    {
        var section = new Section();
        foreach (var child in container)
            if (Convert(child, size) is { } converted) section.Blocks.Add(converted);
        return section;
    }

    private static Paragraph Heading(HeadingBlock heading, double size)
    {
        var scale = heading.Level switch { 1 => 1.6, 2 => 1.35, 3 => 1.15, _ => 1.0 };
        var p = Para(heading.Inline, new Thickness(0, heading.Level <= 2 ? 10 : 6, 0, 6));
        p.FontSize = size * scale;
        p.FontWeight = FontWeights.SemiBold;
        return p;
    }

    private static Paragraph Para(ContainerInline? inlines, Thickness margin)
    {
        var p = new Paragraph { Margin = margin };
        if (inlines != null)
            foreach (var inline in inlines) AddInline(p.Inlines, inline);
        return p;
    }

    private static Section Quote(QuoteBlock quote, double size)
    {
        var section = Section(quote, size);
        section.Margin = new Thickness(0, 0, 0, 8);
        section.Padding = new Thickness(10, 2, 0, 2);
        section.BorderThickness = new Thickness(3, 0, 0, 0);
        section.SetResourceReference(WpfBlock.BorderBrushProperty, "SystemControlForegroundBaseLowBrush");
        section.SetResourceReference(TextElement.ForegroundProperty, "SystemControlForegroundBaseMediumBrush");
        return section;
    }

    private static List ListOf(ListBlock list, double size)
    {
        var wpf = new List
        {
            MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(20, 0, 0, 0),
        };
        if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start)) wpf.StartIndex = start;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var li = new ListItem();
            foreach (var child in item)
            {
                if (Convert(child, size) is not { } converted) continue;
                if (converted is Paragraph p) p.Margin = new Thickness(0, 0, 0, 2);
                li.Blocks.Add(converted);
            }
            wpf.ListItems.Add(li);
        }
        return wpf;
    }

    private static Paragraph Code(LeafBlock code, double size)
    {
        var p = Literal(code.Lines.ToString().TrimEnd('\n', '\r'), size);
        p.Padding = new Thickness(8);
        p.SetResourceReference(TextElement.BackgroundProperty, "SubtleFillBrush");
        return p;
    }

    private static Paragraph Literal(string text, double size) => new(new Run(text))
    {
        FontFamily = new FontFamily("Cascadia Code, Consolas"),
        FontSize = size - 1,
        Margin = new Thickness(0, 0, 0, 8),
    };

    private static Paragraph Rule()
    {
        var p = new Paragraph { Margin = new Thickness(0, 4, 0, 8), BorderThickness = new Thickness(0, 1, 0, 0), FontSize = 2 };
        p.SetResourceReference(WpfBlock.BorderBrushProperty, "SystemControlForegroundBaseLowBrush");
        return p;
    }

    private static System.Windows.Documents.Table TableOf(Markdig.Extensions.Tables.Table table, double size)
    {
        var wpf = new System.Windows.Documents.Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 8) };
        var group = new TableRowGroup();
        foreach (var row in table.OfType<TableRow>())
        {
            var wpfRow = new System.Windows.Documents.TableRow();
            foreach (var cell in row.OfType<TableCell>())
            {
                var wpfCell = new System.Windows.Documents.TableCell { Padding = new Thickness(6, 2, 6, 2), BorderThickness = new Thickness(0, 0, 0, 1) };
                wpfCell.SetResourceReference(System.Windows.Documents.TableCell.BorderBrushProperty, "SystemControlForegroundBaseLowBrush");
                foreach (var child in cell)
                    if (Convert(child, size) is { } converted)
                    {
                        if (converted is Paragraph p) { p.Margin = new Thickness(0); if (row.IsHeader) p.FontWeight = FontWeights.SemiBold; }
                        wpfCell.Blocks.Add(converted);
                    }
                wpfRow.Cells.Add(wpfCell);
            }
            group.Rows.Add(wpfRow);
        }
        wpf.RowGroups.Add(group);
        return wpf;
    }

    private static void AddInline(InlineCollection target, MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;
            case CodeInline code:
                var run = new Run(code.Content) { FontFamily = new FontFamily("Cascadia Code, Consolas") };
                run.SetResourceReference(TextElement.BackgroundProperty, "SubtleFillBrush");
                target.Add(run);
                break;
            case LineBreakInline br:
                target.Add(br.IsHard ? new LineBreak() : new Run(" "));
                break;
            case TaskList task:
                target.Add(new Run(task.Checked ? "☑ " : "☐ "));
                break;
            case EmphasisInline emphasis:
                WpfInline span = emphasis.DelimiterChar == '~'
                    ? new Span { TextDecorations = TextDecorations.Strikethrough }
                    : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                foreach (var child in emphasis) AddInline(((Span)span).Inlines, child);
                target.Add(span);
                break;
            case LinkInline { IsImage: true } image:
                // Images are not fetched; the alt text and address stand in.
                target.Add(new Run($"[image: {Text(image)}]"));
                break;
            case LinkInline link:
                target.Add(Link(link.Url, link));
                break;
            case AutolinkInline auto:
                target.Add(Link(auto.Url, null));
                break;
            case HtmlInline html:
                target.Add(new Run(html.Tag));
                break;
            case HtmlEntityInline entity:
                target.Add(new Run(entity.Transcoded.ToString()));
                break;
            case ContainerInline container:
                foreach (var child in container) AddInline(target, child);
                break;
        }
    }

    private static WpfInline Link(string? url, ContainerInline? label)
    {
        var text = label != null ? Text(label) : url ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !FleetMate.Core.Knowledge.HandbookLinks.IsWeb(uri))
            return new Run(text);

        var hyperlink = new Hyperlink(new Run(text)) { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
        hyperlink.RequestNavigate += (_, e) =>
        {
            MarkdownViewer.OpenInBrowser(e.Uri);
            e.Handled = true;
        };
        return hyperlink;
    }

    private static string Text(ContainerInline container)
    {
        var parts = new List<string>();
        foreach (var child in container)
        {
            switch (child)
            {
                case LiteralInline l: parts.Add(l.Content.ToString()); break;
                case CodeInline c: parts.Add(c.Content); break;
                case ContainerInline inner: parts.Add(Text(inner)); break;
            }
        }
        return string.Concat(parts);
    }
}
