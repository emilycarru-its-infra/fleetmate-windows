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
            await WebView.EnsureCoreWebView2Async();
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            WebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
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

        var html = IsHtml(text) ? text : ConvertMarkdownToHtml(text);
        var fullHtml = WrapInHtmlPage(html);
        WebView.NavigateToString(fullHtml);
    }

    private static bool IsHtml(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith('<') &&
               (trimmed.Contains("</") || trimmed.Contains("/>"));
    }

    private static string ConvertMarkdownToHtml(string markdown)
    {
        var pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();
        return Markdown.ToHtml(markdown, pipeline);
    }

    private static string WrapInHtmlPage(string bodyHtml) => $$"""
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8">
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
