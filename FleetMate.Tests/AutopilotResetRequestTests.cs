using System.Text.Json;
using FleetMate.Core.Services;
using Xunit;

namespace FleetMate.Tests;

public class AutopilotResetRequestTests
{
    [Fact]
    public void AutopilotReset_IsAWipeThatKeepsEnrollment_NotFreshStart()
    {
        var (url, body) = GraphService.AutopilotResetRequest("device-1");

        Assert.Equal("deviceManagement/managedDevices/device-1/wipe", url);
        Assert.DoesNotContain("cleanWindowsDevice", url);

        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.GetProperty("keepEnrollmentData").GetBoolean());
        Assert.False(json.RootElement.GetProperty("keepUserData").GetBoolean());
        Assert.Equal(2, json.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void AutopilotReset_PassesKeepUserDataWhenAsked()
    {
        var (_, body) = GraphService.AutopilotResetRequest("device-1", keepUserData: true);

        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.GetProperty("keepEnrollmentData").GetBoolean());
        Assert.True(json.RootElement.GetProperty("keepUserData").GetBoolean());
    }
}
