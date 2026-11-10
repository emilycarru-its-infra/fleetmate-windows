using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using FleetMate.GUI.Views.Shared;
using Xunit;

namespace FleetMate.Tests;

[Collection(WpfCollection.Name)]
public sealed class CopyFeedbackTests
{
    [Fact]
    public Task CopyPreviewRetainsExactTextAndResetsItsFiveSecondLifetime() => WpfFixture.RunAsync(async () =>
    {
        var button = new Button { Content = "Copy" };
        var window = new Window { Content = button, Width = 400, Height = 200, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            const string text = "Title\nhttps://example.invalid/item/1\n\nExact <content> & spacing";
            CopyFeedback.Show(button, text);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.True(CopyFeedback.IsOpen);
            Assert.Equal(text, CopyFeedback.CurrentText);
            Assert.Same(button, CopyFeedback.CurrentAnchor);
            await Task.Delay(TimeSpan.FromSeconds(3));
            CopyFeedback.Show(button, "Second copy");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(TimeSpan.FromSeconds(2.3));
            Assert.True(CopyFeedback.IsOpen);
            Assert.Equal("Second copy", CopyFeedback.CurrentText);
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.False(CopyFeedback.IsOpen);
            Assert.Null(CopyFeedback.CurrentText);
        }
        finally { CopyFeedback.Dismiss(); window.Close(); }
    });

    [Fact]
    public Task EscapeDismissesAndClearsCopiedContent() => WpfFixture.RunAsync(async () =>
    {
        var button = new Button { Content = "Copy" };
        var window = new Window { Content = button, Width = 400, Height = 200, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            CopyFeedback.Show(button, "copied content");
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var popup = (Popup)typeof(CopyFeedback).GetField("_popup", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            popup.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Assert.False(CopyFeedback.IsOpen);
            Assert.Null(CopyFeedback.CurrentText);
        }
        finally { CopyFeedback.Dismiss(); window.Close(); }
    });
}
