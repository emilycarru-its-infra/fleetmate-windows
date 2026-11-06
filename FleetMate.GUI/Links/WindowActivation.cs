namespace FleetMate.GUI.Links;

/// <summary>
/// Which window a handoff from a later launch brings forward, and what it
/// takes to get it there. Kept free of WPF types so it can be tested without a
/// UI: the app passes its windows in as plain objects.
/// </summary>
internal static class WindowActivation
{
    /// <summary>
    /// The app's own main window, never any other. The window remembered at
    /// startup wins while it is still open; otherwise
    /// <c>Application.MainWindow</c>, then any open window, but only when it is
    /// of the main window's type <typeparamref name="TShell"/>. A hidden
    /// sign-in host, a dialog or a settings window is never picked, even when
    /// WPF has made it <c>Application.MainWindow</c>. Null when no main window
    /// is open.
    /// </summary>
    public static TShell? Target<TShell>(TShell? remembered, object? applicationMainWindow, IEnumerable<object> openWindows)
        where TShell : class
    {
        var open = openWindows.ToList();
        if (remembered != null && open.Contains(remembered)) return remembered;
        if (applicationMainWindow is TShell main && open.Contains(main)) return main;
        return open.OfType<TShell>().FirstOrDefault();
    }

    /// <summary>The steps that put a window on screen in front of the user from the state it is in.</summary>
    public static ActivationPlan Plan(bool visible, bool minimized) =>
        new(Show: !visible, Restore: minimized);
}

/// <summary>
/// <paramref name="Show"/> when the window is hidden or was never shown;
/// <paramref name="Restore"/> when it is minimized. Activating and raising it
/// always follow.
/// </summary>
internal readonly record struct ActivationPlan(bool Show, bool Restore);
