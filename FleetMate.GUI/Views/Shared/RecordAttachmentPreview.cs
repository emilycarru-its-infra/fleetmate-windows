using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Projects;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;

namespace FleetMate.GUI.Views.Shared;

/// <summary>Read-only previews. Downloaded attachments are never launched locally.</summary>
public sealed class RecordAttachmentPreview : Window
{
    private readonly RecordAttachment _attachment;
    private readonly Func<CancellationToken, Task<StagedRecordAttachment>> _download;
    private readonly CancellationTokenSource _cts = new();
    private readonly ContentControl _content = new();
    private readonly Button _save = new() { Content = "Save As…", IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
    private StagedRecordAttachment? _staged;
    private WebView2? _pdf;
    private bool _closed;

    public RecordAttachmentPreview(RecordAttachment attachment, Func<CancellationToken, Task<StagedRecordAttachment>> download)
    {
        _attachment = attachment;
        _download = download;
        Title = attachment.Name;
        Width = Math.Min(920, SystemParameters.WorkArea.Width - 48);
        Height = Math.Min(720, SystemParameters.WorkArea.Height - 48);
        MinWidth = 420; MinHeight = 280;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        SetResourceReference(ForegroundProperty, "SystemControlForegroundBaseHighBrush");
        var root = new DockPanel { Margin = new Thickness(16) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var browser = new Button { Content = "Open in Browser" };
        browser.Click += (_, _) => OpenOriginal();
        _save.Click += OnSave;
        actions.Children.Add(browser); actions.Children.Add(_save);
        DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
        header.Children.Add(new TextBlock { Text = attachment.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        root.Children.Add(_content); Content = root;
        _content.Content = Status("Loading attachment…");
        Loaded += async (_, _) => await LoadAsync();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Closed += (_, _) =>
        {
            _closed = true; _cts.Cancel();
            _pdf?.Dispose(); _pdf = null;
            _staged?.Dispose(); _staged = null;
        };
    }

    private static TextBlock Status(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    private async Task LoadAsync()
    {
        StagedRecordAttachment? downloaded = null;
        try
        {
            downloaded = await _download(_cts.Token);
            if (_closed) { downloaded.Dispose(); return; }
            _staged = downloaded;
            MarkOfTheWeb.Apply(downloaded.Path, _attachment.OriginalUrl);
            _save.IsEnabled = true;
            var type = downloaded.ContentType;
            var extension = Path.GetExtension(_attachment.Name).ToLowerInvariant();
            var signature = new byte[8];
            using (var file = File.OpenRead(downloaded.Path)) _ = await file.ReadAsync(signature, _cts.Token);
            if (signature.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
            {
                _pdf = new WebView2(); _content.Content = _pdf;
                await _pdf.EnsureCoreWebView2Async(await WebViewEnvironments.Content());
                if (_closed) return;
                var core = _pdf.CoreWebView2;
                core.Settings.IsScriptEnabled = false;
                core.Settings.AreHostObjectsAllowed = false;
                core.Settings.IsWebMessageEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                var localUrl = new Uri(downloaded.Path).AbsoluteUri;
                foreach (var pattern in new[] { "http://*", "https://*", "file://*" })
                    core.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, e) =>
                {
                    if (!AllowsLocalResource(e.Request.Uri, downloaded.Path))
                        e.Response = core.Environment.CreateWebResourceResponse(Stream.Null, 403, "Forbidden", "Content-Type: text/plain");
                };
                core.NavigationStarting += (_, e) => e.Cancel = e.Uri != "about:blank" && !AllowsLocalResource(e.Uri, downloaded.Path);
                core.NewWindowRequested += (_, e) => e.Handled = true;
                core.DownloadStarting += (_, e) => e.Cancel = true;
                core.Navigate(localUrl);
            }
            else if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && type != "image/svg+xml"
                     || extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff")
            {
                var width = 1600;
                using (var source = File.OpenRead(downloaded.Path))
                {
                    var decoder = BitmapDecoder.Create(source, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    var frame = decoder.Frames[0];
                    width = Math.Min(width, frame.PixelWidth);
                    if ((long)frame.PixelWidth * frame.PixelHeight > 64_000_000 || frame.PixelWidth > 16384 || frame.PixelHeight > 16384)
                        throw new InvalidDataException("Image is too large to preview.");
                }
                var bitmap = new BitmapImage();
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = width;
                bitmap.UriSource = new Uri(downloaded.Path); bitmap.EndInit(); bitmap.Freeze();
                _content.Content = new ScrollViewer { Content = new Image { Source = bitmap, Stretch = Stretch.Uniform }, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            }
            else if (new FileInfo(downloaded.Path).Length <= 1024 * 1024 && (type is "text/plain" or "text/csv" or "application/json" || extension is ".txt" or ".md" or ".log" or ".csv" or ".json" or ".yaml" or ".yml" or ".ini"))
            {
                var text = await File.ReadAllTextAsync(downloaded.Path, _cts.Token);
                if (_closed) return;
                _content.Content = new TextBox { Text = text, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            }
            else _content.Content = Status("Preview is unavailable for this file type. Save it or open the original attachment in your browser.");
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { downloaded?.Dispose(); }
        catch
        {
            if (!_closed) _content.Content = Status("This attachment could not be previewed. Open it in your browser or try again.");
        }
    }

    internal static bool AllowsLocalResource(string address, string expectedPath)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc || !string.IsNullOrEmpty(uri.Query)) return false;
        try { return Path.GetFullPath(uri.LocalPath).Equals(Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    private void OpenOriginal()
    {
        if (Uri.TryCreate(_attachment.OriginalUrl, UriKind.Absolute, out var url) && RecordAttachments.IsWeb(url)) MarkdownViewer.OpenInBrowser(url);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_staged == null) return;
        var dialog = new SaveFileDialog { FileName = RecordAttachments.SafeName(_attachment.Name), OverwritePrompt = true, InitialDirectory = KnownFolders.Downloads };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.Copy(_staged.Path, dialog.FileName, overwrite: true);
            if (!MarkOfTheWeb.Apply(dialog.FileName, _attachment.OriginalUrl))
                MessageBox.Show(this, "The file was saved, but Windows could not mark it as downloaded.", "Save attachment", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch { MessageBox.Show(this, "The attachment could not be saved to that location.", "Save attachment", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}

/// <summary>Shared attachment rows for task sidebars and lightboxes.</summary>
public sealed class RecordAttachmentList : StackPanel
{
    private RecordAttachmentPreview? _preview;
    private readonly App? _app;
    private bool _listening;
    public RecordAttachmentList(IEnumerable<RecordAttachment> attachments, Func<RecordAttachment, CancellationToken, Task<StagedRecordAttachment>> download)
    {
        foreach (var attachment in attachments.DistinctBy(a => a.Url))
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var preview = new Button { Content = "Preview", ToolTip = $"Preview {attachment.Name}", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3) };
            System.Windows.Automation.AutomationProperties.SetName(preview, $"Preview {attachment.Name}");
            DockPanel.SetDock(preview, Dock.Right); row.Children.Add(preview);
            row.Children.Add(new TextBlock { Text = attachment.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            preview.Click += (_, _) =>
            {
                ClosePreview();
                _preview = new RecordAttachmentPreview(attachment, ct => download(attachment, ct)) { Owner = Window.GetWindow(this) };
                _preview.Show();
            };
            Children.Add(row);
        }
        _app = Application.Current as App;
        Loaded += (_, _) => { if (_app != null && !_listening) { _app.ServicesReloaded += ClosePreview; _listening = true; } };
        Unloaded += (_, _) => { if (_app != null && _listening) { _app.ServicesReloaded -= ClosePreview; _listening = false; } ClosePreview(); };
    }
    private void ClosePreview() { var preview = _preview; _preview = null; preview?.Close(); }
}
