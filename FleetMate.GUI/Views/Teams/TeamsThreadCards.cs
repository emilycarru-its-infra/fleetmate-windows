using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetMate.Core.Services.Teams;
using FleetMate.GUI.Views.Shared;

namespace FleetMate.GUI.Views.Teams;

/// <summary>Native selectable message cards; channel content never becomes executable HTML.</summary>
internal static class TeamsThreadCards
{
    internal static void Show(Panel host, TeamsThread thread, IReadOnlyList<string> allowedHosts, Action<string> copy, Action<Uri> open)
    {
        host.Children.Clear();
        if (!string.IsNullOrWhiteSpace(thread.Subject))
            host.Children.Add(new TextBlock
            {
                Text = TeamsText.OneLine(thread.Subject), FontSize = 22, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            });
        if (thread.Messages.Count == 0)
        {
            host.Children.Add(new TextBlock { Text = "This thread has no indexed messages.", TextWrapping = TextWrapping.Wrap });
            return;
        }
        var root = thread.Messages.FirstOrDefault(message => message.ReplyToId == null) ?? thread.Messages[0];
        var rootBody = new StackPanel();
        rootBody.Children.Add(Card(root, allowedHosts, copy, open));
        var rootRow = new ConversationRailRow { Author = TeamsText.OneLine(root.AuthorDisplayName), Child = rootBody };
        host.Children.Add(rootRow);
        var replies = thread.Messages.Where(message => message.MessageId != root.MessageId).ToList();
        if (replies.Count == 0) return;
        var body = new StackPanel { Visibility = Visibility.Collapsed };
        for (var index = 0; index < replies.Count; index++)
        {
            var reply = replies[index];
            body.Children.Add(new ConversationRailRow
            {
                Author = TeamsText.OneLine(reply.AuthorDisplayName), IsReply = true, IsFirstReply = index == 0,
                Continues = index < replies.Count - 1, Child = Card(reply, allowedHosts, copy, open),
            });
        }
        var disclosure = new Expander { Header = replies.Count == 1 ? "1 reply" : $"{replies.Count} replies", Margin = new Thickness(0, 0, 0, 8) };
        disclosure.Expanded += (_, _) => { body.Visibility = Visibility.Visible; rootRow.Continues = true; };
        disclosure.Collapsed += (_, _) => { body.Visibility = Visibility.Collapsed; rootRow.Continues = false; };
        rootBody.Children.Add(disclosure);
        host.Children.Add(body);
    }

    private static Border Card(TeamsMessage message, IReadOnlyList<string> allowedHosts, Action<string> copy, Action<Uri> open)
    {
        var body = new StackPanel();
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var copyButton = new Button { Content = "Copy message", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(12, 0, 0, 0) };
        copyButton.Click += (_, _) => copy(TeamsThreadCopy.Message(message));
        DockPanel.SetDock(copyButton, Dock.Right);
        heading.Children.Add(copyButton);
        var byline = new StackPanel();
        byline.Children.Add(new TextBlock { Text = TeamsText.OneLine(message.AuthorDisplayName ?? "Unknown author"), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        byline.Children.Add(new TextBlock { Text = TeamsText.LocalTime(message.CreatedAt) + (message.EditedAt == null ? "" : " · edited"), Opacity = 0.65, FontSize = 11, Margin = new Thickness(0, 3, 0, 0) });
        heading.Children.Add(byline);
        body.Children.Add(heading);
        body.Children.Add(new TextBox
        {
            Text = TeamsText.TerminalSafe(message.Text), IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Padding = new Thickness(0), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        foreach (var attachment in message.Attachments)
        {
            if (TeamsLinkPolicy.SafeUri(attachment.Url, allowedHosts) is { } uri)
            {
                var button = new Button { Content = "Attachment: " + TeamsText.OneLine(attachment.Name), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), ToolTip = uri.AbsoluteUri };
                button.Click += (_, _) => open(uri);
                body.Children.Add(button);
            }
            else body.Children.Add(new TextBlock { Text = "Attachment: " + TeamsText.OneLine(attachment.Name), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Opacity = 0.7 });
        }
        var card = new Border { Child = body, Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BorderBrushProperty, "SystemControlForegroundBaseLowBrush");
        return card;
    }
}
