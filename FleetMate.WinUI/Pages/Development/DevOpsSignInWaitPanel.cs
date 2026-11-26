using FleetMate.Core.Links;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FleetMate.WinUI.Pages.Development;

/// <summary>
/// Stands in for a detail that can't load until Azure DevOps sign-in finishes —
/// the shared half of holding an Azure DevOps request, as in FleetMate.GUI.
/// </summary>
/// <remarks>
/// A view that loads from Azure DevOps records whether it was signed in when the
/// load began, asks <c>App.Services.DevOpsRetry</c> what to do when it fails, and
/// while it waits shows this panel and loads again from <c>DevOpsSignedIn</c>.
/// Once an attempt has failed the panel says so and offers Sign In Again, which
/// runs the silent sign-in only.
/// </remarks>
public sealed partial class DevOpsSignInWaitPanel : UserControl
{
    private readonly string _subject;
    private readonly string? _error;

    /// <param name="subject">What is waiting, as in "Waiting for Azure DevOps sign-in to open {subject}…".</param>
    /// <param name="error">The failure the held request reported, shown once sign-in has failed.</param>
    public DevOpsSignInWaitPanel(string subject, string? error = null)
    {
        _subject = subject;
        _error = error;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Padding = new Thickness(16);
        MaxWidth = 460;
        AutomationProperties.SetAutomationId(this, "DevOpsSignInWait");
        Loaded += (_, _) => { App.Services.DevOpsSignInStatusChanged += Render; Render(); };
        Unloaded += (_, _) => App.Services.DevOpsSignInStatusChanged -= Render;
        Render();
    }

    private void Render()
    {
        var services = App.Services;
        var failed = services?.DevOpsSignInStatus == SignInStatus.Failed;
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 8 };

        if (failed)
        {
            stack.Children.Add(Line($"Azure DevOps sign-in didn't finish, so {_subject} can't open yet.", "BodyTextBlockStyle"));
            if (!string.IsNullOrWhiteSpace(_error)) stack.Children.Add(Line(_error!, "CaptionTextBlockStyle"));
            var again = new Button { Content = "Sign In Again", HorizontalAlignment = HorizontalAlignment.Center };
            // The held request reloads from DevOpsSignedIn once this lands.
            again.Click += (_, _) => _ = services?.RetryDevOpsRequestAsync(() => Task.CompletedTask);
            stack.Children.Add(again);
        }
        else
        {
            stack.Children.Add(new ProgressRing { IsActive = true, Width = 32, Height = 32 });
            stack.Children.Add(Line($"Waiting for Azure DevOps sign-in to open {_subject}…", "BodyTextBlockStyle"));
        }
        Content = stack;
    }

    private static TextBlock Line(string text, string style) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources[style],
        Foreground = DevelopmentUi.Theme("TextFillColorSecondaryBrush"),
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
    };
}
