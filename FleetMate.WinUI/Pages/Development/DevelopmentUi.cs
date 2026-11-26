using FleetMate.Core.Links;
using FleetMate.Core.Models.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Serilog;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace FleetMate.WinUI.Pages.Development;

/// <summary>Small helpers every Development segment shares: the browser, the clipboard and the status colours.</summary>
public static class DevelopmentUi
{
    public static void OpenUrl(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url)) ExternalBrowser.Open(url);
    }

    public static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[development] Clipboard unavailable");
        }
    }

    /// <summary>A theme brush by key, falling back to the secondary text colour.</summary>
    public static Brush Theme(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    /// <summary>
    /// Run and section status: success green, failed and partial the caution
    /// colour (never red, as on Mac), running the attention colour, the rest grey.
    /// System fill brushes, so Light, Dark and High Contrast each get theirs.
    /// </summary>
    public static Brush StatusBrush(PipelineRunStatus status) => status switch
    {
        PipelineRunStatus.Succeeded => Theme("SystemFillColorSuccessBrush"),
        PipelineRunStatus.Failed or PipelineRunStatus.Partial => Theme("SystemFillColorCautionBrush"),
        PipelineRunStatus.Running => Theme("SystemFillColorAttentionBrush"),
        _ => Theme("TextFillColorSecondaryBrush"),
    };

    /// <summary>A pull request's state: open green, conflicts the caution colour, merged purple, the rest grey.</summary>
    public static Brush StateBrush(UnifiedPullRequest pr) => pr.State switch
    {
        PullRequestState.Merged => MergedBrush,
        PullRequestState.Open when pr.HasConflicts => Theme("SystemFillColorCautionBrush"),
        PullRequestState.Open => Theme("SystemFillColorSuccessBrush"),
        _ => Theme("TextFillColorSecondaryBrush"),
    };

    /// <summary>Merged purple, as on GitHub; the system text colour in high contrast.</summary>
    private static Brush MergedBrush => new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast
        ? Theme("TextFillColorPrimaryBrush")
        : new SolidColorBrush(Color.FromArgb(0xFF, 0x8A, 0x63, 0xD2));

    public static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
}
