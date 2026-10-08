using System.Windows;
using System.Windows.Controls;
using Markdig;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// WPF control that renders Markdown or HTML content using WebView2 + Markdig.
/// Auto-detects HTML vs Markdown and renders accordingly.
/// </summary>
public partial class MarkdownViewer : UserControl
{
    private bool _isInitialized;
    private string? _pendingContent;

    public static readonly DependencyProperty MarkdownTextProperty =
        DependencyProperty.Register(nameof(MarkdownText), typeof(string), typeof(MarkdownViewer),
            new PropertyMetadata(null, OnMarkdownTextChanged));

    public string? MarkdownText
    {
        get => (string?)GetValue(MarkdownTextProperty);
        set => SetValue(MarkdownTextProperty, value);
    }

    /// <summary>
    /// Content from a repository many people edit (Handbook pages, skills).
    /// Raw HTML in the Markdown is shown as text, never rendered; the page
    /// carries a Content-Security-Policy that loads nothing but inline styles
    /// and images from <see cref="ImageOrigin"/>; and script is off. Set it
    /// before the content.
    /// </summary>
    public bool Untrusted { get; set; }

    /// <summary>With <see cref="Untrusted"/>, the one origin images may load from (the Handbook site), besides inline data.</summary>
    public string? ImageOrigin { get; set; }

    /// <summary>
    /// A link in the content was clicked. The viewer never navigates itself:
    /// with no handler, only an http(s) link opens, in the browser.
    /// </summary>
    public event Action<string>? LinkClicked;

    /// <summary>Set just before showing content, so only that navigation is let through.</summary>
    private bool _expectingContent;

    public MarkdownViewer()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialized) return;
        try
        {
            await WebView.EnsureCoreWebView2Async(await WebViewEnvironments.Content());
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            WebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            WebView.CoreWebView2.Settings.IsWebMessageEnabled = false;
            if (Untrusted) WebView.CoreWebView2.Settings.IsScriptEnabled = false;
            // Links never navigate the viewer or open a window of their own.
            WebView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            WebView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            _isInitialized = true;
            WebView.Visibility = Visibility.Visible;

            if (_pendingContent != null)
            {
                RenderContent(_pendingContent);
                _pendingContent = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to initialize MarkdownViewer WebView2");
        }
    }

    private static void OnMarkdownTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownViewer viewer)
        {
            var text = e.NewValue as string;
            if (viewer._isInitialized)
                viewer.RenderContent(text);
            else
                viewer._pendingContent = text;
        }
    }

    private void RenderContent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            WebView.NavigateToString("<html><body></body></html>");
            return;
        }

        var html = !Untrusted && IsHtml(text) ? text : ConvertMarkdownToHtml(text, Untrusted);
        var fullHtml = WrapInHtmlPage(html, Untrusted ? ContentSecurityPolicy(ImageOrigin) : null);
        _expectingContent = true;
        WebView.NavigateToString(fullHtml);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_expectingContent && !e.IsUserInitiated)
        {
            _expectingContent = false;
            return;
        }
        e.Cancel = true;
        FollowLink(e.Uri);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        FollowLink(e.Uri);
    }

    private void FollowLink(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return;
        if (LinkClicked != null)
        {
            LinkClicked(uri);
            return;
        }
        if (FleetMate.Core.Knowledge.HandbookLinks.External(uri) is { } url)
            OpenInBrowser(url);
    }

    /// <summary>Open an http(s) address in the browser. Anything else is refused.</summary>
    public static void OpenInBrowser(Uri url)
    {
        if (!FleetMate.Core.Knowledge.HandbookLinks.IsWeb(url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warning(ex, "Could not open {Url}", url.AbsoluteUri); }
    }

    /// <summary>Nothing loads but inline styles and images from <paramref name="imageOrigin"/> or inline data.</summary>
    internal static string ContentSecurityPolicy(string? imageOrigin)
    {
        var images = "data:";
        if (Uri.TryCreate(imageOrigin, UriKind.Absolute, out var origin) && FleetMate.Core.Knowledge.HandbookLinks.IsWeb(origin))
            images += " " + origin.GetLeftPart(UriPartial.Authority);
        return $"default-src 'none'; img-src {images}; style-src 'unsafe-inline'";
    }

    private static bool IsHtml(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith('<') &&
               (trimmed.Contains("</") || trimmed.Contains("/>"));
    }

    internal static string ConvertMarkdownToHtml(string markdown, bool untrusted = false)
    {
        var builder = new MarkdownPipelineBuilder().UseAdvancedExtensions();
        // Raw HTML in untrusted text is shown as text: no script, iframe or on* handler can render.
        if (untrusted) builder.DisableHtml();
        return Markdown.ToHtml(markdown, builder.Build());
    }

    private static string WrapInHtmlPage(string bodyHtml, string? contentSecurityPolicy) => $$"""
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8">
        {{(contentSecurityPolicy == null ? "" : $"<meta http-equiv=\"Content-Security-Policy\" content=\"{contentSecurityPolicy}\">")}}
        <style>
            body {
                font-family: 'Segoe UI', sans-serif;
                font-size: 13px;
                line-height: 1.6;
                color: #e0e0e0;
                background: transparent;
                margin: 0;
                padding: 4px 0;
            }
            a { color: #4da6ff; }
            code {
                background: rgba(255,255,255,0.08);
                padding: 2px 6px;
                border-radius: 3px;
                font-family: 'Cascadia Code', Consolas, monospace;
                font-size: 12px;
            }
            pre {
                background: rgba(255,255,255,0.06);
                padding: 12px;
                border-radius: 6px;
                overflow-x: auto;
            }
            pre code { background: none; padding: 0; }
            blockquote {
                border-left: 3px solid #4da6ff;
                margin: 8px 0;
                padding: 4px 12px;
                color: #aaa;
            }
            table { border-collapse: collapse; width: 100%; margin: 8px 0; }
            th, td { border: 1px solid #444; padding: 6px 10px; text-align: left; }
            th { background: rgba(255,255,255,0.05); }
            img { max-width: 100%; }
            h1, h2, h3, h4 { margin: 12px 0 6px 0; }
            ul, ol { padding-left: 24px; }
            hr { border: none; border-top: 1px solid #444; margin: 12px 0; }
        </style>
        </head>
        <body>{{bodyHtml}}</body>
        </html>
        """;
}
