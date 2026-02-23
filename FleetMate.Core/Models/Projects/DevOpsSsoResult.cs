namespace FleetMate.Core.Models.Projects;

/// <summary>
/// Result of a DevOps SSO authentication attempt.
/// </summary>
public class DevOpsSsoResult
{
    public bool Success { get; set; }
    public string? Token { get; set; }
    public string? RefreshToken { get; set; }
    public string? UserName { get; set; }
    public string? UserEmail { get; set; }
    public string? Error { get; set; }
    public DateTime Expiry { get; set; }

    public static DevOpsSsoResult Succeeded(string token, DateTime expiry, string? refreshToken = null,
        string? userName = null, string? userEmail = null) => new()
    {
        Success = true,
        Token = token,
        RefreshToken = refreshToken,
        UserName = userName,
        UserEmail = userEmail,
        Expiry = expiry
    };

    public static DevOpsSsoResult Failed(string error) => new()
    {
        Success = false,
        Error = error
    };
}
