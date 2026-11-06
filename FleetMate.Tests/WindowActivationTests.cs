using FleetMate.GUI.Links;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// A second launch hands over to the running app, which must bring back its
/// own main window: never a hidden sign-in host that WPF made the main window,
/// and never a window that is still hidden or minimized.
/// </summary>
public class WindowActivationTests
{
    private sealed class Shell { }
    private sealed class SignInHost { }
    private sealed class Dialog { }

    [Fact]
    public void TheRememberedMainWindowWins()
    {
        var shell = new Shell();
        var host = new SignInHost();
        Assert.Same(shell, WindowActivation.Target(shell, host, new object[] { host, shell }));
    }

    [Fact]
    public void AHiddenSignInHostAsApplicationMainWindowIsNeverPicked()
    {
        var shell = new Shell();
        var host = new SignInHost();
        Assert.Same(shell, WindowActivation.Target<Shell>(null, host, new object[] { host, shell }));
    }

    [Fact]
    public void WithOnlyASignInHostOpenThereIsNothingToShow()
    {
        var host = new SignInHost();
        Assert.Null(WindowActivation.Target<Shell>(null, host, new object[] { host, new Dialog() }));
    }

    [Fact]
    public void AClosedRememberedWindowIsNotBroughtBack()
    {
        var closed = new Shell();
        var host = new SignInHost();
        Assert.Null(WindowActivation.Target(closed, host, new object[] { host }));
    }

    [Fact]
    public void ApplicationMainWindowIsUsedWhenNothingWasRemembered()
    {
        var first = new Shell();
        var main = new Shell();
        Assert.Same(main, WindowActivation.Target<Shell>(null, main, new object[] { first, main }));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, true, true)]
    public void AHiddenWindowIsShownAndAMinimizedOneRestored(bool visible, bool minimized, bool show, bool restore)
    {
        Assert.Equal(new ActivationPlan(show, restore), WindowActivation.Plan(visible, minimized));
    }
}
