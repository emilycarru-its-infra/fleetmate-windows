using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The headless web sign-in: which Entra account tile is picked, what the
/// injected scripts carry, and that the app has no sign-in window left to show.
/// </summary>
public class HeadlessSignInTests
{
    [Theory]
    [InlineData("adoe@example.edu\nSigned in", "adoe@example.edu")]
    [InlineData("Alex Doe ADOE@Example.edu Connected to Windows", "adoe@example.edu")]
    [InlineData("adoe@example.edu", "  ADOE@EXAMPLE.EDU ")]
    public void MatchesAccountTile_AcceptsTheExactAddress(string tile, string upn)
    {
        Assert.True(EntraWebSignIn.MatchesAccountTile(tile, upn));
    }

    [Theory]
    // A second account whose address contains the real one must never match:
    // picking it signs in as the wrong identity and every API call returns 403.
    [InlineData("aws-adoe@example.edu Signed in", "adoe@example.edu")]
    [InlineData("adoe@example.edu.au", "adoe@example.edu")]
    [InlineData("xadoe@example.edu", "adoe@example.edu")]
    [InlineData("Use another account", "adoe@example.edu")]
    [InlineData("", "adoe@example.edu")]
    [InlineData("adoe@example.edu", "")]
    public void MatchesAccountTile_RejectsAnythingButTheExactAddress(string tile, string upn)
    {
        Assert.False(EntraWebSignIn.MatchesAccountTile(tile, upn));
    }

    [Fact]
    public void MatchesAccountTile_PicksTheRightTileWhenBothAccountsAreSignedIn()
    {
        var tiles = new[] { "aws-adoe@example.edu Signed in", "adoe@example.edu Signed in" };
        var chosen = tiles.Single(t => EntraWebSignIn.MatchesAccountTile(t, "adoe@example.edu"));
        Assert.Equal("adoe@example.edu Signed in", chosen);
    }

    [Fact]
    public void AccountScript_MatchesByWholeAddressAndReportsToTheHost()
    {
        var script = EntraWebSignIn.AccountScript("ADoe@Example.edu");
        Assert.Contains("var wanted = 'adoe@example.edu';", script);
        Assert.Contains("emails.indexOf(wanted) !== -1", script);
        Assert.DoesNotContain(".includes(wanted)", script);
        Assert.Contains("chrome.webview.postMessage", script);
    }

    [Fact]
    public void AccountScript_EscapesTheAddress()
    {
        var script = EntraWebSignIn.AccountScript("o'neil\\x@example.edu");
        Assert.Contains(@"'o\'neil\\x@example.edu'", script);
    }

    [Fact]
    public void KmsiScript_OnlyClicksOnTheStaySignedInPage()
    {
        Assert.Contains("if (!isKmsi) return 'not-kmsi';", EntraWebSignIn.KmsiScript);
        Assert.Contains("#idSIButton9", EntraWebSignIn.KmsiScript);
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/common/oauth2/authorize?x=1", true)]
    [InlineData("https://LOGIN.MICROSOFTONLINE.COM/tenant/saml2", true)]
    [InlineData("https://login.microsoft.com/common/kmsi", true)]
    [InlineData("https://td.example.edu/TDWorkManagement/", false)]
    [InlineData("https://login.microsoftonline.com.example.net/", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void IsEntraPage_RecognisesOnlyEntraHosts(string? url, bool expected)
    {
        Assert.Equal(expected, EntraWebSignIn.IsEntraPage(url));
    }

    [Fact]
    public void HeadlessTimeout_LeavesRoomForAPhonePushButIsBounded()
    {
        Assert.InRange(EntraWebSignIn.HeadlessTimeout, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void TheAppHasNoSignInWindow()
    {
        // Web sign-in runs in a hidden WebView2 and fails rather than prompting;
        // a Window subclass for sign-in would be a way back to a visible prompt.
        var assembly = typeof(FleetMate.GUI.Views.Shared.HiddenWebView).Assembly;
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types.OfType<Type>().ToArray(); }
        var signInWindows = types
            .Where(t => typeof(System.Windows.Window).IsAssignableFrom(t))
            .Where(t => t.Name.Contains("Sso", StringComparison.OrdinalIgnoreCase)
                     || t.Name.Contains("Login", StringComparison.OrdinalIgnoreCase)
                     || t.Name.Contains("SignIn", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.FullName)
            .ToList();
        Assert.Empty(signInWindows);
    }
}
