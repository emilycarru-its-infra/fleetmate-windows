using FleetMate.Core.Services.Devices;
using Xunit;

namespace FleetMate.Tests;

/// <summary>
/// Verifies the native ChecklistService parses checklist.md's emoji vocabulary,
/// finds the next untested item, rewrites a line on update, and recomputes the
/// progress counters — matching Checklist-Manager.ps1.
/// </summary>
public class ChecklistServiceTests
{
    private static string WriteTempChecklist()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cl-{Guid.NewGuid():N}.md");
        File.WriteAllText(path,
            "# Cimian Testing Checklist\n\n" +
            "**Custom Installer Packages**: 1/3 ✅ | **Deployment Installers**: 0/0 ✅\n\n" +
            "## Custom Build Packages\n" +
            "- [x] ✅ Bifrost v2.14.0 - last tested 2025-08-26 12:00:00 by Nelson\n" +
            "- [ ] AdobeFresco\n" +
            "- [ ] ❌ AdobeMediaCore v2025.8.0 - last tested 2025-09-08 12:00:00 by Nelson\n\n" +
            "**Total Progress**: 1/3 items tested\n");
        return path;
    }

    [Fact]
    public void Parses_StatusesAndNames()
    {
        var path = WriteTempChecklist();
        try
        {
            var items = new ChecklistService().GetItems(path);

            Assert.Equal(3, items.Count);
            Assert.Equal("Bifrost", items[0].Name);
            Assert.Equal(ChecklistStatus.Passed, items[0].Status);
            Assert.Equal("AdobeFresco", items[1].Name);
            Assert.Equal(ChecklistStatus.Untested, items[1].Status);
            Assert.Equal("AdobeMediaCore", items[2].Name);
            Assert.Equal(ChecklistStatus.Failed, items[2].Status);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void GetNextUntested_ReturnsFirstUnchecked()
    {
        var path = WriteTempChecklist();
        try
        {
            var next = new ChecklistService().GetNextUntested(path);
            Assert.NotNull(next);
            Assert.Equal("AdobeFresco", next!.Name);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UpdateItem_RewritesLineAndProgress()
    {
        var path = WriteTempChecklist();
        try
        {
            var svc = new ChecklistService();
            var ok = svc.UpdateItem(path, "AdobeFresco", ChecklistStatus.Passed, note: null,
                timestamp: "2025-01-01 00:00:00", user: "Jane Doe");
            Assert.True(ok);

            var updated = svc.GetItems(path);
            Assert.Equal(ChecklistStatus.Passed, updated.First(i => i.Name == "AdobeFresco").Status);

            var text = File.ReadAllText(path);
            Assert.Contains("**Custom Installer Packages**: 2/3", text);
            Assert.Contains("by Jane", text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UpdateItem_UnknownItem_ReturnsFalse()
    {
        var path = WriteTempChecklist();
        try
        {
            Assert.False(new ChecklistService().UpdateItem(path, "DoesNotExist", ChecklistStatus.Passed, null));
        }
        finally { File.Delete(path); }
    }
}
