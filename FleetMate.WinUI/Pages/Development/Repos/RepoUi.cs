using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FleetMate.WinUI.Pages.Development.Repos;

/// <summary>
/// The Repos palette, from theme brushes so Light, Dark and High Contrast each
/// get theirs. Nothing destructive or removed is red: deletions and warnings
/// take the caution colour, as in FleetMate for Mac.
/// </summary>
internal static class RepoBrushes
{
    public static Brush Added => DevelopmentUi.Theme("Series3Brush");
    public static Brush Removed => DevelopmentUi.Theme("Series2Brush");
    public static Brush Warning => DevelopmentUi.Theme("SystemFillColorCautionBrush");
    public static Brush Success => DevelopmentUi.Theme("SystemFillColorSuccessBrush");
    public static Brush Other => DevelopmentUi.Theme("TextFillColorTertiaryBrush");
    public static Brush Secondary => DevelopmentUi.Theme("TextFillColorSecondaryBrush");
    public static Brush Text => DevelopmentUi.Theme("TextFillColorPrimaryBrush");
    public static Brush Accent => DevelopmentUi.Theme("AccentFillColorDefaultBrush");
    public static Brush OnAccent => DevelopmentUi.Theme("TextOnAccentFillColorPrimaryBrush");
    public static Brush Subtle => DevelopmentUi.Theme("SubtleFillColorSecondaryBrush");

    /// <summary>Series colours in a fixed order, never cycled into red (the eighth slot).</summary>
    public const int SeriesCount = 7;

    public static Brush Series(int index) => DevelopmentUi.Theme($"Series{(((index % SeriesCount) + SeriesCount) % SeriesCount) + 1}Brush");

    public static Brush Lane(int index) => Series(index);
}

/// <summary>Confirmations and warnings: a ContentDialog, one at a time, Cancel the default.</summary>
internal static class RepoDialogs
{
    private static bool _open;

    /// <summary>Asks before something destructive; true only when the person chose <paramref name="action"/>.</summary>
    public static Task<bool> ConfirmAsync(XamlRoot? root, string title, string message, string action) =>
        ShowAsync(root, title, Body(message), action);

    /// <summary>A plain notice with one button.</summary>
    public static async Task NoticeAsync(XamlRoot? root, string title, string message) =>
        await ShowAsync(root, title, Body(message), null);

    /// <summary>
    /// The warning before deleting a clone that holds work its remotes do not:
    /// exactly what would be lost, one line each, and a destructive button that
    /// says what it does. Cancel is the default, so Enter never deletes.
    /// </summary>
    public static Task<bool> CloneLossAsync(XamlRoot? root, string name, string path, IReadOnlyList<string> losses)
    {
        var panel = new StackPanel { Spacing = 6, MaxWidth = 460 };
        panel.Children.Add(Muted(path));
        panel.Children.Add(new TextBlock { Text = "This is not on any remote:", Margin = new Thickness(0, 8, 0, 0) });
        foreach (var loss in losses)
            panel.Children.Add(new TextBlock { Text = "•  " + loss, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 0, 0, 0), IsTextSelectionEnabled = true });
        panel.Children.Add(Muted("The folder goes to the Recycle Bin, so it can be restored from there, and FleetMate stops tracking it."));
        return ShowAsync(root, $"Deleting “{name}” would lose work", panel, "Move to Recycle Bin Anyway");
    }

    private static UIElement Body(string message) => new ScrollViewer
    {
        MaxHeight = 360,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 460 },
    };

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = RepoBrushes.Secondary,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        IsTextSelectionEnabled = true,
    };

    private static async Task<bool> ShowAsync(XamlRoot? root, string title, UIElement content, string? action)
    {
        if (root == null || _open) return false;
        _open = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = title,
                Content = content,
                CloseButtonText = action == null ? "OK" : "Cancel",
                DefaultButton = ContentDialogButton.Close,
                Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            };
            if (root.Content is FrameworkElement shell) dialog.RequestedTheme = shell.ActualTheme;
            if (action != null) dialog.PrimaryButtonText = action;
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _open = false;
        }
    }
}

/// <summary>One field of a <see cref="NamePopover"/>.</summary>
internal sealed record PopoverField(string Label, string Initial = "", bool Required = true, string? AutomationId = null);

/// <summary>
/// The anchored popover for lightweight naming — a new branch, a branch at a
/// commit, a tag — never a modal naming dialog. The first field is focused
/// with its text selected; Enter saves, Escape cancels, clicking away dismisses.
/// </summary>
internal static class NamePopover
{
    public static void Show(FrameworkElement anchor, string title, string? prompt, IReadOnlyList<PopoverField> fields,
        string saveLabel, Func<IReadOnlyList<string>, Task> save)
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.Bottom, ShouldConstrainToRootBounds = false };
        var panel = new StackPanel { Spacing = 8, Width = 340 };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        if (prompt != null)
            panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Foreground = RepoBrushes.Secondary,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
        var boxes = new List<TextBox>();
        var saveButton = new Button { Content = saveLabel, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(saveButton, "NamePopoverSave");
        void Validate() => saveButton.IsEnabled = fields.Zip(boxes).All(p => !p.First.Required || p.Second.Text.Trim().Length > 0);
        async void Save()
        {
            Validate();
            if (!saveButton.IsEnabled) return;
            var values = boxes.Select(b => b.Text.Trim()).ToList();
            flyout.Hide();
            await save(values);
        }
        foreach (var field in fields)
        {
            var box = new TextBox { Header = field.Label, Text = field.Initial };
            if (field.AutomationId != null) Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(box, field.AutomationId);
            box.TextChanged += (_, _) => Validate();
            box.KeyDown += (_, e) =>
            {
                if (e.Key != VirtualKey.Enter) return;
                e.Handled = true;
                Save();
            };
            boxes.Add(box);
            panel.Children.Add(box);
        }
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => flyout.Hide();
        saveButton.Click += (_, _) => Save();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(saveButton);
        panel.Children.Add(buttons);
        flyout.Content = panel;
        flyout.Opened += (_, _) =>
        {
            if (boxes.FirstOrDefault() is not { } first) return;
            first.Focus(FocusState.Programmatic);
            first.SelectAll();
            // A prefix such as "feature/" is typed after, not replaced.
            if (first.Text.EndsWith('/')) first.Select(first.Text.Length, 0);
        };
        Validate();
        flyout.ShowAt(anchor);
    }
}

/// <summary>Context menus for list rows, opened by right-click, Shift+F10 or the Menu key alike.</summary>
internal static class RowMenus
{
    /// <summary>
    /// Shows the menu <paramref name="build"/> makes for the row the request came
    /// from, at the pointer or beside the focused row, and selects nothing new.
    /// </summary>
    public static void Attach(ListViewBase list, Func<object, FrameworkElement, MenuFlyout?> build)
    {
        list.ContextRequested += (_, e) =>
        {
            var container = Container(e.OriginalSource as DependencyObject, list);
            if (container == null || list.ItemFromContainer(container) is not { } item) return;
            if (build(item, container) is not { } menu || menu.Items.Count == 0) return;
            e.Handled = true;
            if (e.TryGetPosition(container, out var point)) menu.ShowAt(container, new FlyoutShowOptions { Position = point });
            else menu.ShowAt(container);
        };
    }

    private static SelectorItem? Container(DependencyObject? element, DependencyObject stop)
    {
        while (element != null && element != stop)
        {
            if (element is SelectorItem item) return item;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    public static MenuFlyoutItem Item(string text, Action action, bool enabled = true, string? glyph = null, string? tooltip = null)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        if (glyph != null) item.Icon = new FontIcon { Glyph = glyph };
        if (tooltip != null) ToolTipService.SetToolTip(item, tooltip);
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>An async action from a menu: failures are already reported by the model.</summary>
    public static MenuFlyoutItem Item(string text, Func<Task> action, bool enabled = true, string? glyph = null, string? tooltip = null) =>
        Item(text, () => { _ = action(); }, enabled, glyph, tooltip);
}

/// <summary>Whether a key event came with Shift or Control held, read from the keyboard's current state.</summary>
internal static class Keys
{
    public static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    public static bool Shift => IsDown(VirtualKey.Shift);
    public static bool Control => IsDown(VirtualKey.Control);
    public static bool Alt => IsDown(VirtualKey.Menu);
}
