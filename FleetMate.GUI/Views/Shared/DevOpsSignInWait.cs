using System.Windows;
using System.Windows.Controls;
using FleetMate.Core.Links;
using ModernWpf.Controls;

namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// Stands in for a detail that can't load until Azure DevOps sign-in finishes —
/// the shared half of holding an Azure DevOps request.
/// </summary>
/// <remarks>
/// A view that loads from Azure DevOps records whether it was signed in when
/// the load began, asks <see cref="App.DevOpsRetry"/> what to do when it fails,
/// and while it waits shows this panel and loads again from
/// <see cref="App.DevOpsSignedIn"/>. Once an attempt has failed the panel says
/// so and offers Sign In Again, which runs the silent sign-in only; the held
/// request still loads if any later sign-in succeeds.
/// </remarks>
public sealed class DevOpsSignInWaitPanel : Border
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
        Loaded += (_, _) => { if (Application.Current is App app) app.DevOpsSignInStatusChanged += Render; Render(); };
        Unloaded += (_, _) => { if (Application.Current is App app) app.DevOpsSignInStatusChanged -= Render; };
        Render();
    }

    private void Render()
    {
        var app = Application.Current as App;
        var failed = app?.DevOpsSignInStatus == SignInStatus.Failed;
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "DevOpsSignInWait");

        if (failed)
        {
            stack.Children.Add(Line($"Azure DevOps sign-in didn't finish, so {_subject} can't open yet.", 13, 1));
            if (!string.IsNullOrWhiteSpace(_error)) stack.Children.Add(Line(_error!, 11, 0.7));
            var again = new Button { Content = "Sign In Again", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
            // The held request reloads from App.DevOpsSignedIn once this lands.
            again.Click += (_, _) => { if (app != null) _ = app.RetryDevOpsRequestAsync(() => Task.CompletedTask); };
            stack.Children.Add(again);
        }
        else
        {
            stack.Children.Add(new ProgressRing { IsActive = true, Width = 32, Height = 32 });
            var text = Line($"Waiting for Azure DevOps sign-in to open {_subject}…", 12, 0.7);
            text.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(text);
        }
        Child = stack;
    }

    private static TextBlock Line(string text, double size, double opacity) => new()
    {
        Text = text,
        FontSize = size,
        Opacity = opacity,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        Margin = new Thickness(0, 2, 0, 2),
    };
}
