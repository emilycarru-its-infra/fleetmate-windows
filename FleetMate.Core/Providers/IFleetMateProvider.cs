namespace FleetMate.Core.Providers;

/// <summary>
/// Base interface for all FleetMate providers. Supplies identity and lifecycle.
/// </summary>
public interface IFleetMateProvider
{
    string ProviderId { get; }
    string ProviderName { get; }
    bool IsEnabled { get; }
    Task<bool> AuthenticateAsync(CancellationToken ct = default);
}

/// <summary>
/// Provider that supplies managed devices (e.g. Intune).
/// </summary>
public interface IManagementProvider : IFleetMateProvider
{
    Task<List<UnifiedManagedDevice>> ListDevicesAsync(DeviceFilter? filter = null, CancellationToken ct = default);
    Task<UnifiedManagedDevice?> GetDeviceAsync(string deviceId, CancellationToken ct = default);
    Task<UnifiedManagedDevice?> GetDeviceBySerialAsync(string serial, CancellationToken ct = default);
    Task<List<UnifiedManagedDevice>> SearchDevicesAsync(string query, CancellationToken ct = default);
    Task SyncDeviceAsync(string deviceId, CancellationToken ct = default);
}

/// <summary>
/// Provider that supplies asset inventory (e.g. Snipe-IT).
/// </summary>
public interface IAssetProvider : IFleetMateProvider
{
    Task<List<UnifiedAsset>> ListAssetsAsync(AssetFilter? filter = null, CancellationToken ct = default);
    Task<UnifiedAsset?> GetAssetAsync(string assetId, CancellationToken ct = default);
    Task<UnifiedAsset?> GetAssetByTagAsync(string assetTag, CancellationToken ct = default);
    Task<List<UnifiedAsset>> SearchAssetsAsync(string query, CancellationToken ct = default);
}

/// <summary>
/// Provider that supplies tickets (e.g. TDX).
/// </summary>
public interface ITicketProvider : IFleetMateProvider
{
    Task<List<UnifiedTicket>> ListTicketsAsync(TicketFilter? filter = null, CancellationToken ct = default);
    Task<UnifiedTicket?> GetTicketAsync(string ticketId, CancellationToken ct = default);
    Task<List<UnifiedTicket>> SearchTicketsAsync(string query, CancellationToken ct = default);
}

/// <summary>
/// Provider that supplies groups (e.g. Entra ID).
/// </summary>
public interface IGroupProvider : IFleetMateProvider
{
    Task<List<UnifiedGroup>> ListGroupsAsync(string? prefix = null, int? limit = null, CancellationToken ct = default);
    Task<UnifiedGroup?> GetGroupAsync(string groupId, CancellationToken ct = default);
    Task<List<UnifiedGroup>> SearchGroupsAsync(string query, int? limit = null, CancellationToken ct = default);
    Task<List<UnifiedGroupMember>> GetGroupMembersAsync(string groupId, CancellationToken ct = default);
}
