namespace FleetMate.Core.Models;

/// <summary>
/// Top-level grouping of connected systems, matching tab categories.
/// </summary>
public enum AuthCategory
{
    Devices,
    Inventory,
    Tickets,
    Projects,
    Identity
}

/// <summary>
/// Known system identifiers.
/// </summary>
public enum AuthSystemId
{
    Intune,
    Graph,
    Snipe,
    Tdx,
    DevOps,
    GitHub,
    Gitea,
    Entra
}

/// <summary>
/// Possible authentication states for a system.
/// </summary>
public enum AuthStateKind
{
    NotConfigured,
    Configured,
    Authenticating,
    Valid,
    Expired,
    Failed,
    ServicePrincipal
}

/// <summary>
/// Authentication state for a system, with optional details.
/// </summary>
public class AuthTokenState
{
    public AuthStateKind Kind { get; set; } = AuthStateKind.NotConfigured;
    public string? User { get; set; }
    public DateTime? Expiry { get; set; }
    public string? Message { get; set; }
    public string? ServicePrincipalName { get; set; }

    public bool IsHealthy => Kind == AuthStateKind.Valid;

    public string StatusLabel => Kind switch
    {
        AuthStateKind.NotConfigured => "Not Configured",
        AuthStateKind.Configured => "Configured",
        AuthStateKind.Authenticating => "Authenticating\u2026",
        AuthStateKind.Valid => "Valid",
        AuthStateKind.Expired => "Expired",
        AuthStateKind.Failed => $"Failed: {Message}",
        AuthStateKind.ServicePrincipal => "Service Principal",
        _ => "Unknown"
    };

    public string StatusColor => Kind switch
    {
        AuthStateKind.Valid => "Green",
        AuthStateKind.Configured => "Gold",
        AuthStateKind.Authenticating => "DodgerBlue",
        AuthStateKind.Expired => "Orange",
        AuthStateKind.Failed => "Red",
        AuthStateKind.ServicePrincipal => "Orange",
        AuthStateKind.NotConfigured => "Gray",
        _ => "Gray"
    };

    public static AuthTokenState NotConfigured() => new() { Kind = AuthStateKind.NotConfigured };
    public static AuthTokenState Configured() => new() { Kind = AuthStateKind.Configured };
    public static AuthTokenState Authenticating() => new() { Kind = AuthStateKind.Authenticating };
    public static AuthTokenState Valid(string? user = null, DateTime? expiry = null) => new() { Kind = AuthStateKind.Valid, User = user, Expiry = expiry };
    public static AuthTokenState Failed(string message) => new() { Kind = AuthStateKind.Failed, Message = message };
    public static AuthTokenState SP(string name) => new() { Kind = AuthStateKind.ServicePrincipal, ServicePrincipalName = name };
}

/// <summary>
/// Tracks auth state for a single connected system.
/// </summary>
public class AuthSystemStatus
{
    public AuthSystemId SystemId { get; set; }
    public AuthTokenState State { get; set; } = AuthTokenState.NotConfigured();
    public string? User { get; set; }
    public DateTime? LastChecked { get; set; }
}

/// <summary>
/// Extension methods for auth enums.
/// </summary>
public static class AuthExtensions
{
    public static string DisplayName(this AuthSystemId id) => id switch
    {
        AuthSystemId.Intune => "Intune",
        AuthSystemId.Graph => "Microsoft Graph",
        AuthSystemId.Snipe => "Snipe-IT",
        AuthSystemId.Tdx => "TeamDynamix",
        AuthSystemId.DevOps => "DevOps",
        AuthSystemId.GitHub => "GitHub",
        AuthSystemId.Gitea => "Gitea",
        AuthSystemId.Entra => "Entra ID",
        _ => id.ToString()
    };

    public static string Icon(this AuthSystemId id) => id switch
    {
        AuthSystemId.Intune => "\uE7F8",  // Device icon
        AuthSystemId.Graph => "\uE968",   // Network
        AuthSystemId.Snipe => "\uE7B8",   // Package
        AuthSystemId.Tdx => "\uE8EA",     // Ticket
        AuthSystemId.DevOps => "\uE770",  // Building
        AuthSystemId.GitHub => "\uE943",  // Code
        AuthSystemId.Gitea => "\uE8D4",   // Branch
        AuthSystemId.Entra => "\uE8FA",   // Shield
        _ => "\uE946"
    };

    public static AuthCategory Category(this AuthSystemId id) => id switch
    {
        AuthSystemId.Intune or AuthSystemId.Graph => AuthCategory.Devices,
        AuthSystemId.Snipe => AuthCategory.Inventory,
        AuthSystemId.Tdx => AuthCategory.Tickets,
        AuthSystemId.DevOps or AuthSystemId.GitHub or AuthSystemId.Gitea => AuthCategory.Projects,
        AuthSystemId.Entra => AuthCategory.Identity,
        _ => AuthCategory.Devices
    };

    public static string DisplayName(this AuthCategory cat) => cat switch
    {
        AuthCategory.Devices => "Devices",
        AuthCategory.Inventory => "Inventory",
        AuthCategory.Tickets => "Tickets",
        AuthCategory.Projects => "Projects",
        AuthCategory.Identity => "Identity",
        _ => cat.ToString()
    };
}
