using FleetMate.Core.Models.Identity;
using FleetMate.Core.Services;

namespace FleetMate.Core.Providers.Identity;

/// <summary>
/// Wraps GraphService to expose Entra ID groups through the unified IGroupProvider interface.
/// </summary>
public class EntraGroupProvider : IGroupProvider
{
    private readonly GraphService _graphService;

    public string ProviderId => "entra";
    public string ProviderName => "Entra ID";
    public bool IsEnabled => true;

    public EntraGroupProvider(GraphService graphService)
    {
        _graphService = graphService;
    }

    public Task<bool> AuthenticateAsync(CancellationToken ct = default) =>
        Task.FromResult(true);

    public async Task<List<UnifiedGroup>> ListGroupsAsync(string? prefix = null, int? limit = null, CancellationToken ct = default)
    {
        var groups = await _graphService.SearchGroupsAsync(prefix ?? "", limit ?? 100);
        return groups.Select(ToUnified).ToList();
    }

    public async Task<UnifiedGroup?> GetGroupAsync(string groupId, CancellationToken ct = default)
    {
        var group = await _graphService.GetGroupByIdAsync(groupId);
        return group != null ? ToUnified(group) : null;
    }

    public async Task<List<UnifiedGroup>> SearchGroupsAsync(string query, int? limit = null, CancellationToken ct = default)
    {
        var groups = await _graphService.SearchGroupsAsync(query, limit ?? 50);
        return groups.Select(ToUnified).ToList();
    }

    public async Task<List<UnifiedGroupMember>> GetGroupMembersAsync(string groupId, CancellationToken ct = default)
    {
        var members = await _graphService.GetGroupMembersAsync(groupId);
        return members.Select(m => new UnifiedGroupMember
        {
            Id = m.Id,
            DisplayName = m.DisplayName,
            UserPrincipalName = m.UserPrincipalName,
            MemberType = "user",
        }).ToList();
    }

    private static UnifiedGroup ToUnified(EntraGroup g) => new()
    {
        Id = g.Id,
        ProviderId = "entra",
        DisplayName = g.DisplayName,
        Description = g.Description,
        GroupType = g.IsDynamic ? "Dynamic" : g.IsM365Group ? "Microsoft 365" : "Security",
        Mail = g.Mail,
    };
}
