using FleetMate.Core.Config;
using Xunit;

namespace FleetMate.Tests;

/// <summary>The Settings field table both pages draw from.</summary>
public class DesktopSettingsFieldsTests
{
    private static IEnumerable<DesktopSettingField> AllFields =>
        DesktopSettingsFields.Connections.Concat(DesktopSettingsFields.Manage).Concat(DesktopSettingsFields.Terminal);

    private static IEnumerable<DesktopSettingToggle> AllToggles =>
        DesktopSettingsFields.ManageToggles.Concat(DesktopSettingsFields.TerminalToggles);

    [Fact]
    public void EveryFieldReadsBackWhatItWrites()
    {
        foreach (var field in AllFields.Where(f => !f.Multiline))
        {
            var changed = field.Set(new DesktopSettings(), "synthetic-value");
            Assert.Equal("synthetic-value", field.Get(changed));
        }
        var repos = DesktopSettingsFields.Terminal.Single(f => f.Key == "Repos");
        Assert.Equal(new[] { "org/a", "org/b" }, repos.Set(new DesktopSettings(), "org/a\r\norg/b").Repos);
    }

    [Fact]
    public void EveryToggleReadsBackWhatItWrites()
    {
        foreach (var toggle in AllToggles)
            Assert.True(toggle.Get(toggle.Set(new DesktopSettings(), true)));
    }

    [Fact]
    public void KeysAreUniqueAndNoFieldAsksForASecret()
    {
        var keys = AllFields.Select(f => f.Key).Concat(AllToggles.Select(t => t.Key)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.DoesNotContain(keys, k => DesktopSettings.RetiredSecrets.Contains(k));
        Assert.DoesNotContain(keys, k => k.Contains("Secret") || k.Contains("Password") || k.Contains("ApiKey"));
    }
}
