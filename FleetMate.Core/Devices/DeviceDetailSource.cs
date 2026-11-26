using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;
using FleetMate.Core.Services;

namespace FleetMate.Core.Devices;

/// <summary>
/// Where the device detail panel and the compliance lightbox read what they
/// load on selection: groups, policy states, detected apps, a policy's
/// setting states and definition. Microsoft Graph in use
/// (<see cref="GraphDeviceDetailSource"/>), fixtures in a demo.
/// </summary>
public interface IDeviceDetailSource
{
    Task<List<EntraGroup>> GroupsAsync(string azureAdDeviceId);
    Task<List<DeviceCompliancePolicyState>> CompliancePoliciesAsync(string intuneDeviceId);
    Task<List<DetectedApp>> DetectedAppsAsync(string intuneDeviceId);
    Task<List<ComplianceSettingState>> SettingStatesAsync(string intuneDeviceId, string policyId);
    Task<CompliancePolicyDefinition?> PolicyDefinitionAsync(string policyId);

    /// <summary>A group's name, or its id when it cannot be read.</summary>
    Task<string> GroupNameAsync(string groupId);
}

/// <summary>The detail panel's reads, from Microsoft Graph.</summary>
public sealed class GraphDeviceDetailSource(GraphService graph) : IDeviceDetailSource
{
    public Task<List<EntraGroup>> GroupsAsync(string azureAdDeviceId) => graph.GetDeviceGroupMembershipsAsync(azureAdDeviceId);
    public Task<List<DeviceCompliancePolicyState>> CompliancePoliciesAsync(string intuneDeviceId) => graph.GetDeviceComplianceAsync(intuneDeviceId);
    public Task<List<DetectedApp>> DetectedAppsAsync(string intuneDeviceId) => graph.GetDetectedAppsAsync(intuneDeviceId);
    public Task<List<ComplianceSettingState>> SettingStatesAsync(string intuneDeviceId, string policyId) =>
        graph.GetPolicySettingStatesAsync(intuneDeviceId, policyId);
    public Task<CompliancePolicyDefinition?> PolicyDefinitionAsync(string policyId) => graph.GetCompliancePolicyDefinitionAsync(policyId);

    public async Task<string> GroupNameAsync(string groupId)
    {
        try
        {
            return (await graph.GetGroupByIdAsync(groupId))?.DisplayName ?? groupId;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Could not resolve group {GroupId}", groupId);
            return groupId;
        }
    }
}

/// <summary>
/// Loads what the compliance lightbox shows for one policy on one device:
/// setting states and the definition at once, then the assigned groups' names.
/// </summary>
public static class ComplianceReportLoader
{
    public static async Task<ComplianceReport> LoadAsync(IDeviceDetailSource source, IntuneDevice device, DeviceCompliancePolicyState policy)
    {
        var report = new ComplianceReport(device, policy);
        var states = source.SettingStatesAsync(device.Id, policy.Id);
        var definition = source.PolicyDefinitionAsync(policy.Id);
        report.SettingStates = await states;
        report.Definition = await definition;
        if (report.Definition is { AssignedGroupIds.Count: > 0 } d)
            foreach (var id in d.AssignedGroupIds) report.AssignedGroupNames.Add(await source.GroupNameAsync(id));
        return report;
    }
}
