using System.Xml.Linq;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// The Assigned to Me toggle's checked style fills it with the accent colour.
/// Its label and icon once also turned accent when on, so the button read as
/// an empty blue box. Nothing inside the toggle may set an accent foreground.
/// </summary>
public class AssignedToMeToggleTests
{
    private static string TicketsPageXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FleetMate.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "FleetMate.GUI", "Views", "Tickets", "TicketsPage.xaml");
    }

    [Fact]
    public void TheToggleContentNeverTakesTheAccentColour()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var toggle = XDocument.Load(TicketsPageXaml()).Descendants()
            .Single(e => (string?)e.Attribute(x + "Name") == "AssignedToMeToggle");

        var accentForegrounds = toggle.Descendants()
            .Where(e => e.Name.LocalName == "Setter"
                        && ((string?)e.Attribute("Property"))?.Contains("Foreground") == true
                        && ((string?)e.Attribute("Value"))?.Contains("Accent") == true)
            .ToList();
        var accentAttributes = toggle.DescendantsAndSelf()
            .SelectMany(e => e.Attributes())
            .Where(a => a.Name.LocalName.EndsWith("Foreground") && a.Value.Contains("Accent"))
            .ToList();

        Assert.Empty(accentForegrounds);
        Assert.Empty(accentAttributes);
    }
}
